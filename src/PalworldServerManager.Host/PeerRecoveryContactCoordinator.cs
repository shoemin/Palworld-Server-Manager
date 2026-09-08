using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum PeerRecoveryContactOutcome { NotScheduled=1,AttemptFinished=2 }
internal sealed record PeerRecoveryContact(Guid Peer,Uri Address,PeerTlsConnectionIdentity Identity);

// Transient signals from trusted, completed TLS + semantic negotiation only. This is
// not authority, a success receipt, an address discovery service or an offline queue.
internal sealed class PeerRecoveryContactCoordinator : IAsyncDisposable
{
    internal const int MaximumWorkers=32;
    internal static readonly TimeSpan MaximumLifetime=TimeSpan.FromSeconds(30);
    private readonly Guid host;
    internal Guid HostId=>host;
    private readonly Func<PeerRecoveryContact,CancellationToken,Task> attempt;
    private readonly TimeProvider clock;
    private readonly object gate=new();
    private readonly Dictionary<Guid,Entry> peers=[];
    private readonly HashSet<Entry> workers=[];
    private readonly CancellationTokenSource stopping=new();
    private readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closing;
    private sealed class Entry(PeerRecoveryContact contact)
    {
        internal PeerRecoveryContact Contact=contact;
        internal bool Pending=true;
        internal readonly TaskCompletionSource Start=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<PeerRecoveryContactOutcome> Work=null!;
    }
    internal PeerRecoveryContactCoordinator(Guid host,Func<PeerRecoveryContact,CancellationToken,Task> attempt,TimeProvider? clock=null)
    {
        if(host==Guid.Empty)throw new ArgumentException("Host identity required.");
        this.host=host;this.attempt=attempt??throw new ArgumentNullException(nameof(attempt));this.clock=clock??TimeProvider.System;
    }
    internal Task<PeerRecoveryContactOutcome> Notify(PeerRecoveryContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);ArgumentNullException.ThrowIfNull(contact.Address);ArgumentNullException.ThrowIfNull(contact.Identity);
        var address=contact.Address;
        if(contact.Peer==Guid.Empty||contact.Peer==host||!address.IsAbsoluteUri||address.Scheme!="https"||address.UserInfo.Length!=0||
            address.AbsolutePath!="/"||address.Query.Length!=0||address.Fragment.Length!=0)throw new ArgumentException("Known peer HTTPS contact required.");
        lock(gate)
        {
            if(closing)return Task.FromResult(PeerRecoveryContactOutcome.NotScheduled);
            workers.RemoveWhere(e=>e.Work.IsCompleted);
            if(peers.TryGetValue(contact.Peer,out var current))
            {current.Contact=contact;current.Pending=true;return current.Work;}
            if(workers.Count>=MaximumWorkers)return Task.FromResult(PeerRecoveryContactOutcome.NotScheduled);
            var entry=new Entry(contact);
            // Never run transport/persistence/callback cleanup inline or inherit a user's context.
            if(ExecutionContext.IsFlowSuppressed())entry.Work=Task.Run(()=>Run(entry));
            else using(ExecutionContext.SuppressFlow())entry.Work=Task.Run(()=>Run(entry));
            peers.Add(contact.Peer,entry);workers.Add(entry);entry.Start.TrySetResult();return entry.Work;
        }
    }
    private async Task<PeerRecoveryContactOutcome> Run(Entry entry)
    {
        await entry.Start.Task.ConfigureAwait(false);
        try
        {
            using var limit=new CancellationTokenSource(MaximumLifetime,clock);
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token,limit.Token);
            while(true)
            {
                PeerRecoveryContact contact;
                lock(gate)
                {
                    if(closing||lifetime.IsCancellationRequested||!entry.Pending)
                    {Remove(entry);return PeerRecoveryContactOutcome.AttemptFinished;}
                    contact=entry.Contact;entry.Pending=false;
                }
                try{await attempt(contact,lifetime.Token).ConfigureAwait(false);}
                catch(Exception ex)when(ex is not OutOfMemoryException){} // Durable marker, not this result, determines retry.
                // Only another real contact may request a next pass; the overall deadline stays fixed.
            }
        }
        finally{lock(gate)Remove(entry);} // workers retains the actual Task through all cleanup.
    }
    private void Remove(Entry entry)
    {
        if(peers.TryGetValue(entry.Contact.Peer,out var current)&&ReferenceEquals(current,entry))peers.Remove(entry.Contact.Peer);
    }
    internal Task StopAsync()
    {
        Task[] active;
        lock(gate)
        {
            if(closing)return stopped.Task;closing=true;peers.Clear();active=workers.Select(e=>(Task)e.Work).ToArray();
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
            lock(gate)workers.Clear();stopping.Dispose();
            if(failure is null)stopped.TrySetResult();else stopped.TrySetException(failure);
        }
        catch(Exception ex){stopped.TrySetException(ex);}
    }
    public ValueTask DisposeAsync()=>new(StopAsync());
}
