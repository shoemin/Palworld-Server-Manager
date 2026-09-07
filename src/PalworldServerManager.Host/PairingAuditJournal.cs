using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// Only bounded public terminal records can await retry. Native session ownership remains elsewhere.
internal sealed class PairingAuditJournal : IDisposable
{
    private readonly object gate = new();
    private readonly PeerTrustRepository repository;
    private readonly TimeProvider time;
    private readonly Dictionary<Guid, (PairingTerminalOutcome Outcome, DateTimeOffset Occurred)> pending = [];
    private readonly ITimer timer;
    private bool faulted, overflow, disposed;
    internal bool Faulted { get { lock (gate) return faulted; } }
    internal int PendingCount { get { lock (gate) return pending.Count; } }
    internal PairingAuditJournal(PeerTrustRepository repository, TimeProvider? timeProvider = null)
    {
        this.repository = repository; time = timeProvider ?? TimeProvider.System;
        Maintain(); // Expired durable trust is cleaned before new pairing is admitted.
        timer = time.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    internal void RequireHealthy()
    { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (faulted) throw new InvalidOperationException("Pairing audit storage is unavailable."); } }
    internal T WithHealthyStorage<T>(Func<T> operation)
    { lock (gate) { RequireHealthy(); return operation(); } }
    internal void Record(Guid id, PairingTerminalOutcome outcome)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (id == Guid.Empty || outcome is not (PairingTerminalOutcome.Failed or PairingTerminalOutcome.Expired)) throw new ArgumentException();
            // Runtime has sixteen transport slots plus at most sixteen unused invitations.
            // A fault immediately closes admission; this cap also guards an incorrect caller.
            if (!pending.ContainsKey(id) && pending.Count >= 64) { overflow = faulted = true; throw new InvalidOperationException("Pairing audit retry capacity exhausted."); }
            pending.TryAdd(id, (outcome, time.GetUtcNow())); FlushCore();
        }
    }
    private void FlushCore()
    {
        try
        {
            foreach (var entry in pending.ToArray())
            { repository.RecordPairingTerminal(entry.Key, entry.Value.Outcome, entry.Value.Occurred); pending.Remove(entry.Key); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { faulted = true; }
    }
    private void Tick()
    {
        if (!Monitor.TryEnter(gate)) return;
        try { Maintain(); } finally { Monitor.Exit(gate); }
    }
    internal void Maintain()
    {
        lock (gate)
        {
            if (disposed) return;
            FlushCore();
            try { repository.MaintainPendingPairingTrust(); faulted = overflow || pending.Count != 0; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { faulted = true; }
        }
    }
    public void Dispose()
    {
        timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        lock (gate)
        {
            if (disposed) return; Maintain(); disposed = true;
            if (faulted) throw new InvalidOperationException("Pairing audit storage remains unavailable at shutdown.");
        }
    }
}
