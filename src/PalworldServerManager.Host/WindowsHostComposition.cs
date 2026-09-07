using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Connections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Windows;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// The single production Windows root. No command line, environment or RPC selects a different
// service, data root, endpoint, identity or private credential.
public static class WindowsHostComposition
{
    public const string PipeName = "PalworldServerManager.Host";
    public static WindowsHostServiceLifetime CreateLifetime() => new(WindowsHostPlatform.ProductServiceName,
        stop => new HostServiceWorker(RunAsync, () => Environment.Exit(1), stop));

    private static async Task RunAsync(CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero)
            ?? throw new InvalidOperationException("The authoritative Host lease is unavailable.");
        var platform = new WindowsHostPlatform();
        var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", WindowsHostPlatform.ProductServiceName).Translate(typeof(SecurityIdentifier));
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != serviceSid) throw new UnauthorizedAccessException("The configured service identity is required.");
        platform.ValidateProtectedDataRoot(serviceSid, stop);
        var database = new HostDatabase(new HostDataRoot(platform.GetHostDataRoot()));
        HostIdentityRecord host;
        using (var connection = database.OpenConnection())
        {
            try
            {
                HostSchemaMigrationRunner.Default().Migrate(connection);
                host = new HostIdentityRepository(database).EnsureHostIdentity(connection);
            }
            finally { SqliteConnection.ClearPool(connection); }
        }
        var hostId = Guid.ParseExact(host.HostId, "D");
        var state = new HostCredentialStateRepository(database, hostId);
        var store = new WindowsSecureCredentialStore(platform.GetHostDataRoot(), serviceSid);
        var native = new WindowsHostTlsCredentialCache(hostId, serviceSid, store);
        var publicRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PalworldServerManager", "PublicTrust");
        var publisher = new WindowsLocalHostTrustPublisher(publicRoot, serviceSid);
        var reconciler = new HostTrustReconciler(state.Read,
            (p, token) => publisher.PublishAsync(new(p.HostId, p.CurrentFingerprint, p.PendingFingerprint, p.PendingRotationId), token),
            native.ReconcileAsync, store.DeleteAsync, state.RecordRetired);
        await reconciler.ReconcileAsync(stop).ConfigureAwait(false);
        var snapshot = state.Read();
        var publication = HostTrustPlanning.Build(snapshot).Publication
            ?? throw new InvalidOperationException("Privileged machine bootstrap is required.");
        await new WindowsHostCredentialMaterial(store).ValidateAsync(snapshot.CurrentReference!, publication.CurrentFingerprint, stop).ConfigureAwait(false);
        var groupSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, WindowsHostPlatform.ProductActivationGroup).Translate(typeof(SecurityIdentifier));
        var rpc = new LocalSecurityRpcRuntime(database, hostId, store, WindowsLocalTlsEndpoint.ReadNativePrincipal, _ => { });
        var certificate = await native.LoadAsync(snapshot.CurrentReference!, stop).ConfigureAwait(false);
        await using var generation = await CreateLocalGenerationAsync(rpc, serviceSid, groupSid, certificate, PipeName, stop).ConfigureAwait(false);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            var canceled = Task.Delay(Timeout.Infinite, wait.Token);
            await Task.WhenAny(canceled, generation.ListenerStopped).ConfigureAwait(false);
        }
        finally { wait.Cancel(); await generation.StopAsync().ConfigureAwait(false); }
        // The generation has released listeners, work and its credential before lease disposal.
    }

    // These factories take ownership of certificate immediately, including any startup failure.
    internal static async Task<HostNetworkGeneration> CreateLocalGenerationAsync(LocalSecurityRpcRuntime rpc, SecurityIdentifier serviceSid,
        SecurityIdentifier groupSid, X509Certificate2 certificate, string pipe, CancellationToken ct = default)
    {
        var generation = new HostNetworkGeneration(certificate);
        try
        {
            generation.AddListener(BuildLocalApplication(rpc, serviceSid, groupSid, certificate, pipe, generation.BindConnection));
            await generation.StartAsync(ct).ConfigureAwait(false); return generation;
        }
        catch (Exception startup)
        {
            try { await generation.StopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Host generation startup and cleanup failed.", startup, cleanup); }
            throw;
        }
    }
    // Explicit trusted full-network composition. Installed RunAsync remains local-only until
    // authorized dispatch/configuration and the mandatory #45 activation hook are composed.
    internal static async Task<HostNetworkGeneration> CreateNetworkGenerationAsync(HostDatabase database, Guid hostId, ISecureCredentialStore store,
        SecurityIdentifier serviceSid, SecurityIdentifier groupSid, X509Certificate2 certificate, string pipe,
        System.Net.IPEndPoint peerEndpoint, System.Net.IPEndPoint pairingEndpoint, IPairingKeyExchangeFactory pairingFactory,
        IPeerActivationHook activationHook, CancellationToken ct = default, TimeProvider? time = null)
    {
        var generation = new HostNetworkGeneration(certificate);
        try
        {
            var state = new HostCredentialStateRepository(database, hostId).Read();
            var plan = HostTrustPlanning.Build(state);
            if (!state.Initialized || plan.Publication?.CurrentFingerprint != WindowsPeerTls.PublicFingerprint(certificate))
                throw new System.Security.Authentication.AuthenticationException("Current initialized Host credential is required.");
            var local = new LocalSecurityRpcRuntime(database, hostId, store, WindowsLocalTlsEndpoint.ReadNativePrincipal, _ => { }, time);
            var peer = new PeerSecurityRpcRuntime(database, hostId, activationHook, time);
            byte[] publicKey;
            using (var key = certificate.GetECDsaPublicKey()!) publicKey = key.ExportSubjectPublicKeyInfo();
            var pairing = new PeerPairingRpcRuntime(database, hostId, publicKey, pairingFactory, (_, _) => { }, time);
            generation.SetPeerWork(peer, pairing, new WindowsPeerHttpTransportFactory(certificate));
            generation.AddListener(BuildLocalApplication(local, serviceSid, groupSid, certificate, pipe, generation.BindConnection));
            var peerApp = BuildPeerApplication(peer, certificate, peerEndpoint, generation.BindConnection); generation.AddListener(peerApp);
            var pairingApp = BuildPairingApplication(pairing, certificate, pairingEndpoint, generation.BindConnection); generation.AddListener(pairingApp);
            await generation.StartAsync(ct).ConfigureAwait(false);
            generation.SetBoundEndpoints(new(peerApp.Urls.Single()), new(pairingApp.Urls.Single())); return generation;
        }
        catch (Exception startup)
        {
            try { await generation.StopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Host generation startup and cleanup failed.", startup, cleanup); }
            throw;
        }
    }

    // Trusted composition/test seam only. Empty hosting avoids loading environment/appsettings
    // before configuration can be constrained; it never enables development exception pages.
    public static WebApplication BuildPeerApplication(PeerSecurityRpcRuntime rpc, X509Certificate2 certificate, System.Net.IPEndPoint endpoint,
        Func<ConnectionDelegate, ConnectionDelegate>? transportMiddleware = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        { Args = [], ApplicationName = typeof(WindowsHostComposition).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrel(); builder.Services.AddRouting(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(rpc);
        builder.Services.AddGrpc(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes;
            options.MaxSendMessageSize = PeerSecurityRpcService.MaximumMessageBytes;
        });
        WindowsPeerEndpoint.Configure(builder.WebHost, endpoint, certificate, rpc.Repository.RecognizesTransportFingerprint,
            rpc.BindConnection(WindowsPeerTls.PublicFingerprint(certificate), WindowsPeerEndpoint.ReadRemoteFingerprint), transportMiddleware);
        var app = builder.Build(); app.MapGrpcService<PeerSecurityRpcService>(); return app;
    }
    internal static PeerActivationRpcClient CreatePeerActivationClient(PeerSecurityRpcRuntime rpc, X509Certificate2 certificate)
        => new(rpc, new WindowsPeerHttpTransportFactory(certificate));
    internal static PeerRotationStatusRpcClient CreatePeerRotationStatusClient(PeerSecurityRpcRuntime rpc, X509Certificate2 certificate)
        => new(rpc, new WindowsPeerHttpTransportFactory(certificate));
    internal static PeerRotationProposalRpcClient CreatePeerRotationProposalClient(PeerSecurityRpcRuntime rpc, X509Certificate2 certificate)
        => new(rpc, new WindowsPeerHttpTransportFactory(certificate));
    internal static PeerRotationReceiptRpcClient CreatePeerRotationReceiptClient(PeerSecurityRpcRuntime rpc, X509Certificate2 certificate)
        => new(rpc, new WindowsPeerHttpTransportFactory(certificate));
    internal static RoutineRotationAcceptanceCollector CreateRotationAcceptanceCollector(PeerSecurityRpcRuntime rpc, X509Certificate2 certificate)
        => new(rpc, new WindowsPeerHttpTransportFactory(certificate));
    public static WebApplication BuildPairingApplication(PeerPairingRpcRuntime rpc, X509Certificate2 certificate, System.Net.IPEndPoint endpoint,
        Func<ConnectionDelegate, ConnectionDelegate>? transportMiddleware = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        { Args = [], ApplicationName = typeof(WindowsHostComposition).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrel(); builder.Services.AddRouting(); builder.Logging.ClearProviders(); builder.Services.AddSingleton(rpc);
        builder.Services.AddGrpc(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaxReceiveMessageSize = PeerPairingRpcService.MaximumMessageBytes;
            options.MaxSendMessageSize = PeerPairingRpcService.MaximumMessageBytes;
        });
        // This separate first-contact endpoint maps ONLY the bounded PAKE stream. Accepting
        // a usable TLS key here is not trust; the MAC must bind that exact key before storage.
        WindowsPeerEndpoint.Configure(builder.WebHost, endpoint, certificate, _ => true,
            rpc.BindConnection(WindowsPeerTls.PublicFingerprint(certificate), WindowsPeerEndpoint.ReadRemoteFingerprint, WindowsPeerEndpoint.ReadSourceAddress), transportMiddleware);
        var app = builder.Build(); app.MapGrpcService<PeerPairingRpcService>(); return app;
    }
    internal static PeerPairingRpcClient CreatePeerPairingClient(PeerPairingRpcRuntime rpc, X509Certificate2 certificate)
        => new(rpc, new WindowsPeerHttpTransportFactory(certificate));

    public static WebApplication BuildLocalApplication(LocalSecurityRpcRuntime rpc, SecurityIdentifier serviceSid,
        SecurityIdentifier groupSid, X509Certificate2 certificate, string pipe,
        Func<ConnectionDelegate, ConnectionDelegate>? transportMiddleware = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        { Args = [], ApplicationName = typeof(WindowsHostComposition).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrel();
        builder.Services.AddRouting();
        builder.Logging.ClearProviders(); // request/secret/exception payloads never enter service diagnostics
        builder.Services.AddSingleton(rpc);
        builder.Services.AddGrpc(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaxReceiveMessageSize = LocalSecurityRpcService.MaximumMessageBytes;
            options.MaxSendMessageSize = LocalSecurityRpcService.MaximumMessageBytes;
        });
        WindowsLocalTlsEndpoint.Configure(builder.WebHost, pipe, serviceSid, groupSid, certificate, rpc.BindConnection, transportMiddleware);
        var app = builder.Build();
        app.MapGrpcService<LocalSecurityRpcService>();
        return app;
    }
}
