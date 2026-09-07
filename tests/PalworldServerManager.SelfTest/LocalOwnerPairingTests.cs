using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using Grpc.Core;
using Grpc.Net.Client;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class LocalOwnerPairingTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Local Owner pairing assertion failed."); }
    private static async Task Denied<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    internal static LocalPrincipalMutationActor Actor(PeerTrustTests.Fixture f) => new(f.HostId, f.OwnerId, "native-owner", "fixture-public");
    public static async Task RepositoryBoundaries()
    {
        using var f = new PeerTrustTests.Fixture(); var repo = f.Repository; var owner = Actor(f);
        var user = Guid.NewGuid();
        f.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{user:D}','native-user','user-public',0,'Active','fixture');");
        foreach (var bad in new[] { owner with { HostId = Guid.NewGuid() }, owner with { LocalPrincipalId = Guid.Empty },
            owner with { OsPrincipalRef = "native-user" }, owner with { PublicVerificationKey = "changed" },
            new(f.HostId, user, "native-user", "user-public") })
        {
            await Denied<AuthenticationException>(() => { repo.AuthorizePairingOwner(bad); return Task.CompletedTask; });
            await Denied<AuthenticationException>(() => { repo.RecordOwnerVerifiedBinding(bad, f.PeerId, new('B',64), new('A',64)); return Task.CompletedTask; });
        }
        Check(f.Count("TrustedManagers") == 0); repo.AuthorizePairingOwner(owner);
        var first = repo.RecordOwnerVerifiedBinding(owner, f.PeerId, new('B',64), new('A',64));
        Check(first.Disposition == PeerBindingDisposition.PeerBoundCreated);
        Check(repo.RecordOwnerVerifiedBinding(owner, f.PeerId, new('B',64), new('A',64)).Disposition == PeerBindingDisposition.ResumePeerBound);
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        foreach (var pin in new[] { new string('B',64), new string('C',64) })
            await Denied<AuthenticationException>(() => { repo.RecordOwnerVerifiedBinding(owner, f.PeerId, pin, new('A',64)); return Task.CompletedTask; });
        Check(f.Count("PendingCredentialReplacements") == 0 && f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
        await Denied<ArgumentNullException>(() => { repo.AuthorizePairingOwner(null!); return Task.CompletedTask; });
        await Denied<ArgumentNullException>(() => { repo.RecordOwnerVerifiedBinding(null!, f.PeerId, new('B',64), new('A',64)); return Task.CompletedTask; });
        // Inbound code proof still has its separate trusted Host path.
        Check(repo.RecordVerifiedBinding(Guid.NewGuid(), new('D',64), new('A',64)).Disposition == PeerBindingDisposition.PeerBoundCreated);
        f.Execute("UPDATE HostIdentity SET HostBootstrapState='Uninitialized' WHERE Id=1;");
        await Denied<InvalidDataException>(() => { repo.AuthorizePairingOwner(owner with { PublicVerificationKey = "changed" }); return Task.CompletedTask; });
        f.Execute("UPDATE HostIdentity SET HostBootstrapState='Initialized' WHERE Id=1; UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL WHERE IsOwner=1;");
        await Denied<InvalidDataException>(() => { repo.AuthorizePairingOwner(owner); return Task.CompletedTask; });
    }
    public static async Task WriterQueueFreshness()
    {
        using var f = new PeerTrustTests.Fixture(); var owner = Actor(f); f.Repository.AuthorizePairingOwner(owner);
        using var tx = f.Writer.BeginTransaction(); using var command = f.Writer.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;"; command.ExecuteNonQuery();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() => { entered.SetResult(); f.Repository.RecordOwnerVerifiedBinding(owner, f.PeerId, new('B',64), new('A',64)); });
        try { await entered.Task; await Task.Delay(100); Check(!work.IsCompleted); }
        finally { tx.Commit(); }
        await Denied<AuthenticationException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));
        Check(f.Count("TrustedManagers") == 0 && f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
    }
    private sealed class RefusingFactory : IPairingKeyExchangeFactory
    {
        internal int Starts;
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
        { Interlocked.Increment(ref Starts); throw new CryptographicException("Fixture cannot produce proof."); }
    }
    private sealed class Clock : TimeProvider
    {
        internal Action? OnUtc;
        public override DateTimeOffset GetUtcNow() { Interlocked.Exchange(ref OnUtc, null)?.Invoke(); return DateTimeOffset.UtcNow; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
        private sealed class Timer : ITimer
        { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class Identity : IDisposable
    {
        private readonly LocalPrincipalKeyPair key = new WindowsLocalPrincipalCryptography().Generate();
        internal readonly LocalPrincipalConnectionAuthentication Connection;
        internal Identity(PeerTrustTests.Fixture f, bool owner = true, bool authenticate = true)
        {
            var id = owner ? f.OwnerId : Guid.NewGuid(); var native = owner ? "native-owner" : "native-user";
            var publicKey = Convert.ToBase64String(key.PublicKey);
            if (owner) f.Execute($"UPDATE LocalPrincipals SET PublicVerificationKey='{publicKey}' WHERE IsOwner=1;");
            else f.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{id:D}','{native}','{publicKey}',0,'Active','fixture');");
            Connection = new(new LocalPrincipalAuthenticationRepository(f.Database), f.HostId, native, _ => { });
            if (authenticate) Connection.Authenticate(new WindowsLocalPrincipalCryptography().Sign(new LocalPrincipalClientCredential(id,key), f.HostId, Connection.IssueChallenge(id)));
        }
        public void Dispose() { Connection.Dispose(); CryptographicOperations.ZeroMemory(key.PrivateKey); }
    }
    private static Task<HostNetworkGeneration> Start(PeerSecurityRpcTests.Fixture f, RefusingFactory factory, Clock clock, HostGenerationDiscoveryTests.Probe probe)
    {
        f.State.Time.Now = DateTimeOffset.UtcNow; using var identity = WindowsIdentity.GetCurrent();
        return WindowsHostComposition.CreateNetworkGenerationAsync(f.State.Database, f.State.HostId, new LocalEnrollmentTests.Store(new byte[32]),
            identity.User!, identity.User!, f.Certificate.Value, "PSMLocalPair" + Guid.NewGuid().ToString("N"),
            new(IPAddress.Loopback,0), new(IPAddress.Loopback,0), factory, f.Runtime.Hook, time: clock, discoveryFactory: probe.CreateAsync);
    }
    public static async Task AuthenticatedGenerationActions()
    {
        await using var f = new PeerSecurityRpcTests.Fixture(); var factory = new RefusingFactory(); var probe = new HostGenerationDiscoveryTests.Probe();
        using var owner = new Identity(f.State); using var user = new Identity(f.State, false);
        using var unauthenticated = new LocalPrincipalConnectionAuthentication(new(f.State.Database), f.State.HostId, "native-owner", _ => { });
        await using var generation = await Start(f, factory, new Clock(), probe);
        foreach (var connection in new[] { user.Connection, unauthenticated })
        {
            await Denied<AuthenticationException>(() => generation.CreateInvitationForOwnerAsync(connection));
            await Denied<AuthenticationException>(() => generation.DiscoverForOwnerAsync(connection));
            await Denied<AuthenticationException>(() => generation.CancelInvitationForOwnerAsync(connection, Guid.NewGuid()));
            using var code = new RedactedSecret("1234567890"u8.ToArray());
            await Denied<AuthenticationException>(() => generation.PairForOwnerAsync(connection, PalworldServerManager.Contracts.HostReachableAddress.Parse("127.0.0.1",1), code));
        }
        using var invitation = await generation.CreateInvitationForOwnerAsync(owner.Connection);
        var codeBytes = invitation.Code.CopyBytes();
        try { Check(codeBytes.Length == 10); } finally { CryptographicOperations.ZeroMemory(codeBytes); } await generation.CancelInvitationForOwnerAsync(owner.Connection, invitation.Id);
        Check((await generation.DiscoverForOwnerAsync(owner.Connection)).Count == 0);
        f.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Denied<AuthenticationException>(() => generation.CreateInvitationForOwnerAsync(owner.Connection));
        Check(factory.Starts == 0 && f.State.Count("TrustedManagers") == 0);
        await generation.StopAsync(); await Denied<InvalidOperationException>(() => generation.DiscoverForOwnerAsync(owner.Connection));
    }
    public static async Task UnreturnedInvitationCleanup()
    {
        foreach (var cancelRequest in new[] { false, true })
        {
            await using var f = new PeerSecurityRpcTests.Fixture(); await using var peer = new PeerSecurityRpcTests.Fixture();
            var factory = new RefusingFactory(); var clock = new Clock(); using var owner = new Identity(f.State);
            await using var generation = await Start(f, factory, clock, new HostGenerationDiscoveryTests.Probe());
            using var cancel = new CancellationTokenSource();
            clock.OnUtc = cancelRequest ? cancel.Cancel : () => f.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
            if (cancelRequest) await Denied<OperationCanceledException>(() => generation.CreateInvitationForOwnerAsync(owner.Connection, cancel.Token));
            else await Denied<AuthenticationException>(() => generation.CreateInvitationForOwnerAsync(owner.Connection));
            using var transport = new WindowsPeerHttpTransportFactory(peer.Certificate.Value).Create(_ => true);
            using var channel = GrpcChannel.ForAddress(generation.Endpoints!.Value.Pairing, new GrpcChannelOptions {
                HttpHandler = transport.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact });
            using var call = new PeerPairingProtocol.PeerPairingProtocolClient(channel).Pair(deadline: DateTime.UtcNow.AddSeconds(10));
            await call.RequestStream.WriteAsync(new PeerPairingFrame { Start = new() { Handshake = PeerPairingRpcRuntime.Hello(), UseAdvertisedInvitation = true } });
            try { await call.ResponseStream.MoveNext(CancellationToken.None); throw new Exception("Unreturned invitation remained advertised."); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { }
            Check(factory.Starts == 0 && f.State.Count("TrustedManagers") == 0);
        }
    }
}
