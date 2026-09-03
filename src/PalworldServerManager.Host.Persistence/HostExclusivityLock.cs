namespace PalworldServerManager.Host.Persistence;

// SS2 / SS5a: the single machine-wide writer-exclusivity primitive, owned by Host.Persistence
// and shared by Host and Host.Cli. Whoever holds it is provably the only writer - not "the only
// writer by convention" (PERSIST-001, HOST-001).
//
// WHY A DEDICATED OWNING THREAD:
// System.Threading.Mutex is thread-affine. Acquiring on one thread and releasing on an arbitrary
// async continuation throws ApplicationException ("Object synchronization method was called from
// an unsynchronized block of code") - verified empirically during #40 preflight. Host code is
// async throughout, so the mutex is confined to one dedicated thread that performs BOTH WaitOne
// and ReleaseMutex; async callers hold a logical lease and may await freely without their
// continuation thread identity ever mattering.
//
// CRASH BEHAVIOR: if a holder terminates without releasing, Windows marks the mutex abandoned.
// The next waiter may or may not observe AbandonedMutexException - preflight measured a case
// where it was NOT raised. Correctness therefore does not depend on it: when it is raised it is
// treated as a successful acquisition of the now-abandoned mutex, and when it is not, the
// ordinary success path already applies.
//
// Windows named mutex is the accepted primitive for Windows (SS2). The Linux flock-backed
// variant is deliberately not implemented here (LINUX-001).
public sealed class HostExclusivityLock : IDisposable
{
    // Global\ prefix makes this machine-wide rather than per-session, which is what "exactly one
    // Host per physical PC" requires.
    public const string DefaultMutexName = @"Global\PalworldServerManager.Host.Exclusivity";

    private readonly Thread _ownerThread;
    private readonly ManualResetEventSlim _releaseRequested;
    private readonly ManualResetEventSlim _released;
    private int _disposed;

    private HostExclusivityLock(
        Thread ownerThread,
        ManualResetEventSlim releaseRequested,
        ManualResetEventSlim released,
        string mutexName)
    {
        _ownerThread = ownerThread;
        _releaseRequested = releaseRequested;
        _released = released;
        MutexName = mutexName;
    }

    public string MutexName { get; }

    // Attempts to acquire the machine-wide lock. Returns null if another process holds it -
    // Host.Cli uses this to refuse immediately and clearly rather than risking a second writer,
    // and never falls back to any online mode (it has none).
    public static HostExclusivityLock? TryAcquire(TimeSpan timeout, string mutexName = DefaultMutexName)
    {
        var acquiredSignal = new ManualResetEventSlim(false);
        var releaseRequested = new ManualResetEventSlim(false);
        var released = new ManualResetEventSlim(false);
        var success = false;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, mutexName);

                try
                {
                    success = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    // A previous holder died without releasing. The mutex IS granted to us.
                    success = true;
                }

                acquiredSignal.Set();

                if (!success)
                {
                    return;
                }

                // Hold ownership on this thread for the entire lease.
                releaseRequested.Wait();

                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Defensive only: this thread is the owner, so this should not occur.
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                acquiredSignal.Set();
            }
            finally
            {
                mutex?.Dispose();
                released.Set();
            }
        })
        {
            IsBackground = true,
            Name = "PSM-Host-Exclusivity-Owner",
        };

        thread.Start();
        acquiredSignal.Wait();
        acquiredSignal.Dispose();

        if (failure is not null || !success)
        {
            // Wind the owner thread down deterministically before returning/throwing.
            releaseRequested.Set();
            released.Wait();
            thread.Join();
            releaseRequested.Dispose();
            released.Dispose();

            if (failure is not null)
            {
                throw new InvalidOperationException($"Failed to acquire the machine-wide Host exclusivity lock '{mutexName}'.", failure);
            }

            return null;
        }

        return new HostExclusivityLock(thread, releaseRequested, released, mutexName);
    }

    // Deterministic: signals the owning thread to release, then waits for it to actually finish
    // before returning, so a subsequent acquisition attempt never races a half-released lease.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _releaseRequested.Set();
        _released.Wait();
        _ownerThread.Join();

        _releaseRequested.Dispose();
        _released.Dispose();
    }
}
