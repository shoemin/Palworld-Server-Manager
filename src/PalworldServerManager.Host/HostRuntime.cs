using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

/// <summary>
/// The bounded Host runtime for #41: acquire the machine-wide exclusivity lock, open and migrate
/// the #40 database, hold both for the service lifetime, and release both deterministically on
/// stop.
///
/// Deliberately absent: any IPC listener, TLS, authentication, Owner behavior, or server
/// operation. There is also NO second persistence path - all database access goes through #40's
/// Host.Persistence types.
///
/// No idle policy: SS2 says a desktop Host eventually idle-exits, but neither a timeout nor a
/// definition of "nothing needs it" is fixed anywhere yet, and the signals that would define it
/// (connected clients, running servers, in-flight operations) do not exist until #42/#46/#48.
/// This exposes only the cancellation seam that policy will later need.
/// </summary>
public sealed class HostRuntime
{
    private readonly IHostDataRootProvider _dataRootProvider;
    private readonly string _serviceAccountName;
    private readonly string _mutexName;

    public HostRuntime(
        IHostDataRootProvider dataRootProvider,
        string serviceAccountName,
        string mutexName = HostExclusivityLock.DefaultMutexName)
    {
        _dataRootProvider = dataRootProvider ?? throw new ArgumentNullException(nameof(dataRootProvider));
        _serviceAccountName = serviceAccountName;
        _mutexName = mutexName;
    }

    /// <summary>
    /// Runs until <paramref name="stopping"/> is signalled, then releases everything before
    /// returning. <paramref name="startupReady"/>, when supplied, is completed ONLY once the data
    /// root/ACL are in place, the exclusivity lock is held, and the database is open and migrated
    /// - never earlier. A caller (the SCM service lifetime) that blocks on it therefore never
    /// reports the service Running before the Host is actually authoritative, and never reports it
    /// Running after startup has already failed.
    /// </summary>
    public async Task RunAsync(CancellationToken stopping, TaskCompletionSource<bool>? startupReady = null)
    {
        try
        {
            var rootPath = _dataRootProvider.GetMachineWideHostDataRoot();
            _dataRootProvider.EnsureCreatedWithHostStateAcl(rootPath, _serviceAccountName);

            // HOST-001 / PERSIST-001: exactly one machine-wide Host may hold this. If another
            // Host or a privileged Host.Cli recovery operation holds it, refuse to start rather
            // than becoming a competing writer.
            using var exclusivity = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), _mutexName)
                ?? throw new InvalidOperationException(
                    "Another Manager Host or privileged recovery operation already holds the machine-wide exclusivity lock; refusing to start a competing instance.");

            var database = new HostDatabase(new HostDataRoot(rootPath));
            using var connection = database.OpenConnection();
            HostSchemaMigrationRunner.Default().Migrate(connection);

            // Startup is now actually complete - not merely "the launching Task hasn't faulted
            // yet". Only past this point may a caller waiting on startupReady treat the Host as
            // authoritative.
            startupReady?.TrySetResult(true);

            try
            {
                await Task.Delay(Timeout.Infinite, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ordinary service stop.
            }

            // connection and exclusivity are released by their using-scopes as this returns,
            // before the service lifetime reports the stop as complete.
        }
        catch (Exception ex) when (startupReady is not null)
        {
            // A startup failure: surface it to whoever is blocked waiting for readiness, then
            // rethrow so the ordinary caller (no readiness observer) sees it too.
            startupReady.TrySetException(ex);
            throw;
        }
    }
}
