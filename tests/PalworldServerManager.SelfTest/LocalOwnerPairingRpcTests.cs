using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

internal static class LocalOwnerPairingRpcTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Local pairing RPC assertion failed."); }
    private static async Task Refused<T>(Task<T> action, StatusCode status)
    { try { await action; } catch (RpcException ex) when (ex.StatusCode == status) { return; } throw new Exception("Expected local pairing refusal: " + status); }
    private sealed class RefusingFactory : IPairingKeyExchangeFactory
    {
        internal int Starts;
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
        { Interlocked.Increment(ref Starts); throw new CryptographicException("Fixture cannot produce proof."); }
    }
    private sealed class Fixture(bool owner = true) : IAsyncDisposable
    {
        internal readonly PeerSecurityRpcTests.Fixture State = new();
        internal readonly LocalPrincipalKeyPair Key = new WindowsLocalPrincipalCryptography().Generate();
        internal readonly HostGenerationDiscoveryTests.Probe Probe = new();
        internal readonly string Pipe = "PSMLocalPairRpc" + Guid.NewGuid().ToString("N");
        internal Guid Principal;
        internal HostNetworkGeneration Generation = null!;
        internal string Native { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
        internal async Task Start(IPairingKeyExchangeFactory factory)
        {
            State.State.Time.Now = DateTimeOffset.UtcNow;
            Principal = owner ? State.State.OwnerId : Guid.NewGuid(); var key = Convert.ToBase64String(Key.PublicKey);
            if (owner) State.State.Execute($"UPDATE LocalPrincipals SET OsPrincipalRef='{Native}',PublicVerificationKey='{key}' WHERE IsOwner=1;");
            else State.State.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{Principal:D}','{Native}','{key}',0,'Active','fixture');");
            using var identity = WindowsIdentity.GetCurrent();
            Generation = await WindowsHostComposition.CreateNetworkGenerationAsync(State.State.Database, State.State.HostId, new LocalEnrollmentTests.Store(new byte[32]),
                identity.User!, identity.User!, State.Certificate.Value, Pipe, new(IPAddress.Loopback,0), new(IPAddress.Loopback,0), factory, State.Runtime.Hook,
                discoveryFactory: Probe.CreateAsync);
        }
        internal LocalSecurityRpcTests.Client Client() => new(State.State.HostId, Pipe, new LocalSecurityRpcTests.Reader(LocalHostTrustAnchor.Parse(JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = 1, hostId = State.State.HostId, currentHostCredentialFingerprint = State.Pin, pendingHostCredentialFingerprint = (string?)null, pendingRotationId = (Guid?)null }))));
        internal async Task<LocalSecurityRpcTests.Client> Authorized()
        {
            var client = Client();
            try { await Negotiate(client); await client.Authenticate(State.State.HostId, Principal, Key); return client; }
            catch { client.Dispose(); throw; }
        }
        internal LocalPrincipalConnectionAuthentication Authentication()
        {
            var connection = new LocalPrincipalConnectionAuthentication(new(State.State.Database), State.State.HostId, Native, _ => { });
            connection.Authenticate(new WindowsLocalPrincipalCryptography().Sign(new(Principal,Key), State.State.HostId, connection.IssueChallenge(Principal)));
            return connection;
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Generation is not null) await Generation.StopAsync(); }
            finally { CryptographicOperations.ZeroMemory(Key.PrivateKey); await State.DisposeAsync(); }
        }
    }
    private static Task<LocalHandshakeReply> Negotiate(LocalSecurityRpcTests.Client client, bool security = true)
    {
        var hello = new Handshake { Protocol = new() { Major = 1, Minor = 8 }, ProductVersion = "display-only" };
        hello.Capabilities.Add(FeatureCapability.LocalOwnerPairing);
        if (security) hello.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);
        return client.Call<Handshake,LocalHandshakeReply>("Negotiate",hello);
    }
    private static Task<LocalPairingInvitation> Create(LocalSecurityRpcTests.Client client) => client.Call<LocalEmpty,LocalPairingInvitation>("CreatePairingInvitation",new());
    private static Task<LocalEmpty> Cancel(LocalSecurityRpcTests.Client client,string id) => client.Call<LocalPairingInvitationRequest,LocalEmpty>("CancelPairingInvitation",new() { InvitationId = id });
    private static Task<LocalPairingDiscoveryReply> Discover(LocalSecurityRpcTests.Client client,uint offset = 0,uint limit = 0)
        => client.Call<LocalPairingDiscoveryRequest,LocalPairingDiscoveryReply>("DiscoverPairingHosts",new() { Offset=offset,Limit=limit });
    private static Task<LocalPairHostReply> Pair(LocalSecurityRpcTests.Client client, Uri address, ByteString code)
        => client.Call<LocalPairHostRequest,LocalPairHostReply>("PairHost",new() { ReachableHost=address.Host,PairingPort=(uint)address.Port,Code=code });
    public static async Task CapabilityAndNativeIdentity()
    {
        await using (var local = new LocalSecurityRpcTests.Fixture(true))
        {
            await local.Start(); using var client = local.Connect(); var reply = await Negotiate(client);
            Check(!reply.Handshake.Capabilities.Contains(FeatureCapability.LocalOwnerPairing));
            await Refused(Create(client),StatusCode.FailedPrecondition);
        }
        var factory = new RefusingFactory(); await using var f = new Fixture(); await f.Start(factory);
        using (var fresh = f.Client()) await Refused(Create(fresh),StatusCode.FailedPrecondition);
        using (var legacy = f.Client()) { await legacy.Negotiate(); await Refused(Create(legacy),StatusCode.FailedPrecondition); }
        using (var missing = f.Client()) { await Negotiate(missing,false); await Refused(Create(missing),StatusCode.FailedPrecondition); }
        using (var anonymous = f.Client()) { var reply=await Negotiate(anonymous); Check(reply.Handshake.Capabilities.Contains(FeatureCapability.LocalOwnerPairing)); await Refused(Create(anonymous),StatusCode.Unauthenticated); }
        using (var owner = await f.Authorized()) { var invitation=await Create(owner); await Cancel(owner,invitation.InvitationId); }
        await using var user = new Fixture(false); await user.Start(factory); using var unauthorized = await user.Authorized();
        await Refused(Create(unauthorized),StatusCode.Unauthenticated); await Refused(Discover(unauthorized),StatusCode.Unauthenticated);
        await Refused(Cancel(unauthorized,Guid.NewGuid().ToString("D")),StatusCode.Unauthenticated);
        await Refused(Pair(unauthorized,new("https://127.0.0.1:1"),ByteString.CopyFromUtf8("1234567890")),StatusCode.Unauthenticated);
        Check(factory.Starts==0 && f.State.State.Count("TrustedManagers")==0 && user.State.State.Count("TrustedManagers")==0);
    }
    public static async Task DiscoveryAndRequestBounds()
    {
        var factory=new RefusingFactory(); await using var f=new Fixture(); await f.Start(factory); using var client=await f.Authorized();
        for (var i=0;i<256;i++) await f.Probe.Receiver.Emit(HostDiscoveryCodec.Encode(new(Guid.NewGuid(),uint.MaxValue,uint.MaxValue,65535,65535)));
        var seen=new HashSet<string>(); uint offset=0;
        do
        {
            var page=await Discover(client,offset); Check(page.Hosts.Count is >0 and <=32 && page.CalculateSize()<LocalSecurityRpcService.MaximumMessageBytes);
            foreach(var hint in page.Hosts) Check(seen.Add(hint.ClaimedHostId) && hint.ReachableHost=="192.0.2.15" && hint.AdvertisedProtocol.Major==uint.MaxValue);
            offset=page.NextOffset;
        } while(offset!=0);
        Check(seen.Count==256 && (await Discover(client,256)).Hosts.Count==0);
        await Refused(Discover(client,257),StatusCode.InvalidArgument); await Refused(Discover(client,0,33),StatusCode.InvalidArgument);
        await Refused(Cancel(client,"not-an-id"),StatusCode.InvalidArgument);
        foreach(var request in new[] {
            new LocalPairHostRequest { ReachableHost="127.0.0.1",PairingPort=0,Code=ByteString.CopyFromUtf8("1234567890") },
            new LocalPairHostRequest { ReachableHost="127.0.0.1",PairingPort=65536,Code=ByteString.CopyFromUtf8("1234567890") },
            new LocalPairHostRequest { ReachableHost="https://example.test",PairingPort=1234,Code=ByteString.CopyFromUtf8("1234567890") },
            new LocalPairHostRequest { ReachableHost="127.0.0.1",PairingPort=1234,Code=ByteString.CopyFromUtf8("123456789x") },
            new LocalPairHostRequest { ReachableHost="127.0.0.1",PairingPort=1234,Code=ByteString.CopyFrom(new byte[11]) } })
            await Refused(client.Call<LocalPairHostRequest,LocalPairHostReply>("PairHost",request),StatusCode.InvalidArgument);
        var invitation=await Create(client); Check(invitation.Code.Length==10 && invitation.Code.All(v=>v>=(byte)'0'&&v<=(byte)'9'));
        await Cancel(client,invitation.InvitationId);
        try { await Pair(client,f.Generation.Endpoints!.Value.Pairing,invitation.Code); throw new Exception("Cancelled invitation accepted."); }
        catch(RpcException ex) { Check(ex.StatusCode==StatusCode.Unavailable && ex.Status.Detail=="Peer pairing did not complete." && ex.Trailers.All(item=>item.Key is "date" or "server")); }
        Check(factory.Starts==0 && f.State.State.Count("TrustedManagers")==0 && f.State.State.Count("HostCapabilityGrants")==0);
    }
    public static async Task StaleOwnerAndResponseConstruction()
    {
        var factory=new RefusingFactory(); await using var f=new Fixture(); await f.Start(factory); using var client=await f.Authorized();
        using(var authentication=f.Authentication())
        {
            PairingInvitation? unreturned=null; var failure=new IOException("fixture response construction failure");
            try { await f.Generation.CreateInvitationForOwnerAsync<LocalEmpty>(authentication, invitation => { unreturned=invitation; throw failure; }); throw new Exception("Missing construction failure."); }
            catch(IOException ex) { Check(ReferenceEquals(ex,failure)); }
            Check(unreturned is not null);
            try { unreturned!.Code.CopyBytes(); throw new Exception("Unreturned secret was retained."); } catch(ObjectDisposedException) { }
        }
        await Refused(Pair(client,f.Generation.Endpoints!.Value.Pairing,ByteString.CopyFromUtf8("1234567890")),StatusCode.Unavailable);
        Check(factory.Starts==0);
        f.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Refused(Create(client),StatusCode.Unauthenticated); await Refused(Discover(client),StatusCode.Unauthenticated);
        await Refused(Pair(client,new("https://127.0.0.1:1"),ByteString.CopyFromUtf8("1234567890")),StatusCode.Unauthenticated);
        Check(f.State.State.Count("TrustedManagers")==0 && f.State.State.Count("HostCapabilityGrants")==0);
    }
    internal static async Task Native(IPairingKeyExchangeFactory provider)
    {
        await using var a=new Fixture(); await using var b=new Fixture(); await a.Start(provider); await b.Start(provider);
        using var localA=await a.Authorized(); using var localB=await b.Authorized();
        var invitation=await Create(localB);
        var wrong=invitation.Code.ToByteArray(); wrong[0]=wrong[0]==(byte)'9'?(byte)'0':(byte)(wrong[0]+1);
        try { await Refused(Pair(localA,b.Generation.Endpoints!.Value.Pairing,ByteString.CopyFrom(wrong)),StatusCode.Unavailable); }
        finally { CryptographicOperations.ZeroMemory(wrong); }
        Check(a.State.State.Count("TrustedManagers")==0 && b.State.State.Count("TrustedManagers")==0);
        await Task.Delay(1100); // Retry after source cooldown; the wrong proof did not create trust.
        var result=await Pair(localA,b.Generation.Endpoints!.Value.Pairing,invitation.Code);
        Check(result.VerifiedPeerHostId==b.State.State.HostId.ToString("D") && result.LocalResult==PeerPairingResult.PeerBound && result.RemoteResult==PeerPairingResult.PeerBound);
        Check(result.LocalReplacementId=="" && DateTimeOffset.TryParse(result.LocalExpiresUtc,out _));
        Check(a.State.State.Repository.Read(b.State.State.HostId)!.CurrentFingerprint==b.State.Pin && b.State.State.Repository.Read(a.State.State.HostId)!.CurrentFingerprint==a.State.Pin);
        Check(a.State.State.Count("HostCapabilityGrants")==0 && a.State.State.Count("ServerCapabilityGrants")==0
            && b.State.State.Count("HostCapabilityGrants")==0 && b.State.State.Count("ServerCapabilityGrants")==0);
        await Task.Delay(1100); var retry=await Create(localB);
        var resumed=await Pair(localA,b.Generation.Endpoints!.Value.Pairing,retry.Code);
        Check(resumed.LocalResult==PeerPairingResult.Resumed && resumed.RemoteResult==PeerPairingResult.Resumed && resumed.LocalExpiresUtc==result.LocalExpiresUtc);
        Console.WriteLine("PASS actual local Owner RPC through named-pipe TLS/native identity and genuine two-Host PAKE: verified PeerBound and zero grants.");
    }
}
