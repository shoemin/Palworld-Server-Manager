using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal enum PeerUnpairNotification { NotAttempted=1, Confirmed=2, Unconfirmed=3 }
internal sealed record PeerUnpairCommit(PeerTrustRevocationResult Revocation,Task<PeerUnpairNotification> Notification);
internal delegate Task<PeerUnpairNotification> PrepareUnpairConnection(Guid peer,Uri address,
    Func<PeerUnpairConnection,CancellationToken,Task<PeerUnpairNotification>> work,CancellationToken ct);

// Only ephemeral already-prepared security connections. Never a durable/offline command queue.
internal sealed class PeerUnpairCoordinator : IAsyncDisposable
{
    internal const int MaximumWorkers=32;
    internal static readonly TimeSpan MaximumLifetime=TimeSpan.FromMinutes(2);
    internal Guid HostId{get;}
    private readonly PrepareUnpairConnection prepare;
    private readonly TimeProvider time;
    private readonly object gate=new();
    private readonly Dictionary<Guid,Entry> prepared=[];
    private readonly HashSet<Entry> workers=[];
    private readonly CancellationTokenSource stopping=new();
    private readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closing;
    private sealed class Entry(Guid peer)
    {
        internal Guid Peer{get;}=peer;
        internal readonly TaskCompletionSource Start=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<PeerTrustRevocationResult?> Notice=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<PeerUnpairNotification> Work=null!;
        internal bool ReadyToSend,Retired,Notified;
    }
    internal PeerUnpairCoordinator(Guid hostId,PrepareUnpairConnection prepare,TimeProvider? time=null)
    {
        if(hostId==Guid.Empty)throw new ArgumentException("Host identity required.");
        HostId=hostId;this.prepare=prepare??throw new ArgumentNullException(nameof(prepare));this.time=time??TimeProvider.System;
    }
    internal static PeerUnpairCoordinator? MatchHost(PeerUnpairCoordinator? value,Guid host)
        =>value is not null&&value.HostId!=host?throw new ArgumentException("Unpair coordinator belongs to another Host."):value;
    internal Task<bool> Prepare(Guid peer,Uri address,CancellationToken ct=default)
    {
        if(peer==Guid.Empty||peer==HostId)throw new ArgumentException("Remote peer required.");
        ArgumentNullException.ThrowIfNull(address);ct.ThrowIfCancellationRequested();Entry entry;
        lock(gate)
        {
            if(closing)throw new InvalidOperationException("Unpair preparation is closed.");
            workers.RemoveWhere(e=>e.Work.IsCompleted);
            if(prepared.ContainsKey(peer)||workers.Count>=MaximumWorkers)return Task.FromResult(false);
            entry=new(peer);
            // Kickoff keeps transport/callback work outside this lock; suppress ambient user context.
            if(ExecutionContext.IsFlowSuppressed())entry.Work=Task.Run(()=>Run(entry,address,ct));
            else using(ExecutionContext.SuppressFlow())entry.Work=Task.Run(()=>Run(entry,address,ct));
            prepared.Add(peer,entry);workers.Add(entry);entry.Start.TrySetResult();
        }
        return entry.Ready.Task;
    }
    // Called only AFTER a canonical local/remote administrator transaction commits. This
    // path never connects, awaits, cancels callbacks, disposes, or invokes the send body.
    internal Task<PeerUnpairNotification> Notify(PeerTrustRevocationResult revoked)
    {
        lock(gate)
        {
            if(closing||!revoked.Changed||!prepared.Remove(revoked.PeerHostId,out var entry))
                return Task.FromResult(PeerUnpairNotification.NotAttempted);
            if(!entry.ReadyToSend)
            {
                entry.Retired=true;entry.Notice.TrySetResult(null);
                return Task.FromResult(PeerUnpairNotification.NotAttempted);
            }
            entry.Notified=true;entry.Notice.TrySetResult(revoked);return entry.Work;
        }
    }
    private async Task<PeerUnpairNotification> Run(Entry entry,Uri address,CancellationToken ct)
    {
        await entry.Start.Task.ConfigureAwait(false);
        try
        {
            lock(gate)if(closing||entry.Retired)return PeerUnpairNotification.NotAttempted;
            using var retention=new CancellationTokenSource(MaximumLifetime,time);
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct,stopping.Token,retention.Token);
            return await prepare(entry.Peer,address,async(held,token)=>
            {
                lock(gate)
                {
                    if(closing||entry.Retired)return PeerUnpairNotification.NotAttempted;
                    entry.ReadyToSend=true;entry.Ready.TrySetResult(true);
                }
                var revoked=await entry.Notice.Task.WaitAsync(token).ConfigureAwait(false);
                if(revoked is null)return PeerUnpairNotification.NotAttempted;
                return await held.Send(revoked).ConfigureAwait(false)==PeerUnpairDelivery.Confirmed?
                    PeerUnpairNotification.Confirmed:PeerUnpairNotification.Unconfirmed;
            },lifetime.Token).ConfigureAwait(false);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {lock(gate)return entry.Notified?PeerUnpairNotification.Unconfirmed:PeerUnpairNotification.NotAttempted;}
        finally
        {
            lock(gate)
            {
                entry.Retired=true;entry.Ready.TrySetResult(false);
                if(prepared.TryGetValue(entry.Peer,out var current)&&ReferenceEquals(current,entry))prepared.Remove(entry.Peer);
                // Keep it in workers until its actual Task completes, not merely this finally.
            }
        }
    }
    internal Task StopAsync()
    {
        Task[] active;
        lock(gate)
        {
            if(closing)return stopped.Task;closing=true;
            active=workers.Select(e=>(Task)e.Work).ToArray();
            foreach(var entry in workers){entry.Retired=true;entry.Notice.TrySetResult(null);}
            prepared.Clear();
        }
        _=StopCore(active);return stopped.Task;
    }
    private async Task StopCore(Task[] active)
    {
        Exception? failure=null;
        try
        {
            try{await stopping.CancelAsync().ConfigureAwait(false);}catch(Exception ex){failure=ex;}
            try{await Task.WhenAll(active).ConfigureAwait(false);}catch(Exception ex){failure??=ex;}
            lock(gate)workers.Clear();
            stopping.Dispose();
            if(failure is null)stopped.TrySetResult();else stopped.TrySetException(failure);
        }
        catch(Exception ex){stopped.TrySetException(ex);}
    }
    public ValueTask DisposeAsync()=>new(StopAsync());
}
