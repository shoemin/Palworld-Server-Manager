using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using ActorRef = PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability = PalworldServerManager.Core.Authorization.HostCapability;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private static void InstallRecoveredTestKey(Fixture local, string reference, string pin)
    {
        // Caller has fully stopped the generation. The fixture holds the actual Host lease;
        // native protected material/offline CLI are separately qualified, not simulated here.
        local.State.State.Execute($"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint) VALUES ('{reference}','HostTlsV1','{DateTimeOffset.UtcNow:O}','{pin}');");
        new HostCredentialStateRepository(local.State.State.Database, local.State.State.HostId)
            .ReplaceOffline(reference, MachineCredentialRecoveryReason.CredentialLoss);
    }
    private static async Task RefuseRecoveryExchange(Task call)
    {
        try { await call; }
        catch (RpcException e) when (e.StatusCode == StatusCode.Unavailable || RecoveryAuthenticationFailure(e)) { return; }
        catch (AuthenticationException) { return; }
        throw new Exception("Native own recovery: unapproved current key was accepted.");
    }
    private static bool RecoveryAuthenticationFailure(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is AuthenticationException) return true;
        return false;
    }
    private static Guid CurrentRepairCandidate(Fixture local, Fixture remote)
    {
        using var command = local.State.State.Writer.CreateCommand();
        command.CommandText = $"SELECT ReplacementId FROM PendingCredentialReplacements WHERE PeerHostId='{remote.State.State.HostId:D}' AND InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;";
        return Guid.Parse((string)command.ExecuteScalar()!);
    }
    internal static async Task NativeSimultaneousOwnRecovery(IPairingKeyExchangeFactory provider)
    {
        using var nextA = new PeerTlsTests.Certificate(); using var nextB = new PeerTlsTests.Certificate();
        // Reverse disposal order: stop both generations before disposing their new certificates,
        // including a failure before the normal end of this scenario.
        await using var a = new Fixture(); await using var b = new Fixture();
        var ga = RepairGrants(a); var gb = RepairGrants(b);
        await a.Start(provider, ga.CreateDefaultActivationHook()); await b.Start(provider, gb.CreateDefaultActivationHook());
        var oldA = a.CurrentPin; var oldB = b.CurrentPin;
        using var retainedOldA = new System.Security.Cryptography.X509Certificates.X509Certificate2(a.State.Certificate.Value);
        using (var localA = a.Client()) using (var localB = b.Client())
        {
            await NegotiateActivation(localA, true); await NegotiateActivation(localB, true);
            await localA.Authenticate(a.State.State.HostId, a.Principal, a.Key);
            await localB.Authenticate(b.State.State.HostId, b.Principal, b.Key);
            using var authA = a.Authentication(); using var authB = b.Authentication();
            ga.ConfigureDefaults(authA.GetCurrentPrincipal().MutationActor, ga.Read().Revision,
                new([new(HostCapability.CreateServer, new(false, false))], []));
            gb.ConfigureDefaults(authB.GetCurrentPrincipal().MutationActor, gb.Read().Revision,
                new([new(HostCapability.CreateServer, new(false, false))], []));
            var invitation = await Create(localB);
            var bound = await Pair(localA, b.Generation.Endpoints!.Value.Pairing, invitation.Code);
            RepairCheck(bound.LocalResult == PeerPairingResult.PeerBound && bound.RemoteResult == PeerPairingResult.PeerBound, "own recovery initial native PAKE");
            await Activate(localA, b.State.State.HostId, b.Generation.Endpoints.Value.Peer);
        }
        var oldRootA = ga.Read().HostGrants.Single().GrantId; var oldRootB = gb.Read().HostGrants.Single().GrantId;
        var oldProofA = new PeerGrantMutationActor(a.State.State.HostId, b.State.State.HostId, oldB, oldA, RepairIncarnation(a, b));
        var oldProofB = new PeerGrantMutationActor(b.State.State.HostId, a.State.State.HostId, oldA, oldB, RepairIncarnation(b, a));
        await a.Generation.StopAsync(); await b.Generation.StopAsync();
        RepairCheck(a.State.Certificate.Value.Handle == IntPtr.Zero && b.State.Certificate.Value.Handle == IntPtr.Zero, "old generations completely released");
        var newA = WindowsPeerTls.PublicFingerprint(nextA.Value); var newB = WindowsPeerTls.PublicFingerprint(nextB.Value);
        RepairCheck(newA != oldA && newB != oldB, "actual fresh machine key pairs");
        InstallRecoveredTestKey(a, "native-own-a", newA); InstallRecoveredTestKey(b, "native-own-b", newB);
        RepairCheck(a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired &&
            b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired, "both offline recoveries independently require repair");
        RepairRefuse(() => ga.RequireRemoteHostCapability(oldProofA, HostCapability.CreateServer, a.State.State.HostId));
        RepairRefuse(() => gb.RequireRemoteHostCapability(oldProofB, HostCapability.CreateServer, b.State.State.HostId));
        await a.Start(provider, ga.CreateDefaultActivationHook(), nextA.Value);
        await b.Start(provider, gb.CreateDefaultActivationHook(), nextB.Value);
        using var repairedA = a.Client(); using var repairedB = b.Client();
        await NegotiateActivation(repairedA, true); await NegotiateActivation(repairedB, true);
        await repairedA.Authenticate(a.State.State.HostId, a.Principal, a.Key);
        await repairedB.Authenticate(b.State.State.HostId, b.Principal, b.Key);
        await Refused(Activate(repairedA, b.State.State.HostId, b.Generation.Endpoints!.Value.Peer), StatusCode.Unavailable);
        var freshInvitation = await Create(repairedB);
        var fresh = await Pair(repairedA, b.Generation.Endpoints.Value.Pairing, freshInvitation.Code);
        RepairCheck(fresh.LocalResult == PeerPairingResult.ReplacementRequired && fresh.RemoteResult == PeerPairingResult.ReplacementRequired,
            "both fresh current keys require explicit Owner approval");
        var approvalA = Guid.Parse(fresh.LocalReplacementId); var approvalB = CurrentRepairCandidate(b, a);
        using var ownerA = a.Authentication(); using var ownerB = b.Authentication();
        // These two canonical commands use separately signed local proof fixtures. The
        // real pairing/activation RPCs above do not stand in for public approval commands.
        ga.ApprovePeerReplacement(ownerA.GetCurrentPrincipal().MutationActor, ga.Read().Revision, approvalA);
        RepairCheck(a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired &&
            b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired, "first approval clears neither recovery flag");
        await RefuseRecoveryExchange(a.Generation.ConfirmRecoveryAsync(b.State.State.HostId, b.Generation.Endpoints.Value.Peer));
        RepairCheck(RepairEvents(a, "PeerRecoveryCompletionConfirmed") == 0 && RepairEvents(b, "PeerRecoveryCompletionReceived") == 0,
            "independently unapproved actual current sender key cannot complete");
        gb.ApprovePeerReplacement(ownerB.GetCurrentPrincipal().MutationActor, gb.Read().Revision, approvalB);
        var oldSender = WindowsHostComposition.CreatePeerRecoveryCompletionClient(
            new(a.State.State.Database, a.State.State.HostId, ga.CreateDefaultActivationHook()), retainedOldA);
        await RefuseRecoveryExchange(oldSender.ConfirmAsync(b.State.State.HostId, b.Generation.Endpoints.Value.Peer));
        RepairCheck(RepairEvents(a, "PeerRecoveryCompletionConfirmed") == 0 && RepairEvents(b, "PeerRecoveryCompletionReceived") == 0,
            "retained old private key cannot acknowledge its replacement");
        RepairCheck(a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired &&
            b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired, "both approvals still preserve independent recovery");
        var approvedIncA = RepairIncarnation(a, b); var approvedIncB = RepairIncarnation(b, a);
        RepairRefuse(() => ga.RequireRemoteHostCapability(new(a.State.State.HostId, b.State.State.HostId, newB, newA, approvedIncA),
            HostCapability.CreateServer, a.State.State.HostId));
        RepairCheck(await a.Generation.ConfirmRecoveryAsync(b.State.State.HostId, b.Generation.Endpoints.Value.Peer) == PeerRecoveryCompletionExchange.Confirmed,
            "A current key sends exact original completion");
        RepairCheck(a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired &&
            !b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired, "only receiving B clears recovery");
        RepairCheck(RepairIncarnation(b, a) != approvedIncB && RepairEvents(a, "PeerRecoveryCompletionConfirmed") == 1 &&
            RepairEvents(b, "PeerRecoveryCompletionConfirmed") == 0, "B marker carries to new incarnation before its send");
        RepairCheck(await b.Generation.ConfirmRecoveryAsync(a.State.State.HostId, a.Generation.Endpoints!.Value.Peer) == PeerRecoveryCompletionExchange.Confirmed,
            "B fresh incarnation sends its original approval");
        RepairCheck(!a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired &&
            !b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired && RepairIncarnation(a, b) != approvedIncA,
            "both recovery flags cleared only after actual exact acknowledgments");
        await Activate(repairedA, b.State.State.HostId, b.Generation.Endpoints.Value.Peer);
        await Activate(repairedB, a.State.State.HostId, a.Generation.Endpoints.Value.Peer);
        foreach (var (local, remote, grants, oldRoot, approval) in new[] { (a, b, ga, oldRootA, approvalA), (b, a, gb, oldRootB, approvalB) })
        {
            var current = grants.Read().HostGrants.Single(g => g.InvalidatedUtc is null);
            RepairCheck(current.GrantId != oldRoot && current.DerivedFromGrantId is null &&
                current.GrantedByActor == ActorRef.LocalPrincipal(local.Principal) &&
                grants.Read().HostGrants.Single(g => g.GrantId == oldRoot).InvalidatedUtc is not null, "fresh Owner root and permanently invalid old root");
            grants.RequireRemoteHostCapability(new(local.State.State.HostId, remote.State.State.HostId, remote.CurrentPin, local.CurrentPin,
                RepairIncarnation(local, remote)), HostCapability.CreateServer, local.State.State.HostId);
            RepairCheck(RepairEvents(local, "PeerCredentialReplacementApproved") == 1 && RepairEvents(local, "PeerRecoveryCompletionReceived") == 1 &&
                RepairEvents(local, "PeerRecoveryCompletionConfirmed") == 1, "one approval and completion audit per side");
            RepairCheck(HostDatabase.QueryScalarLong(local.State.State.Writer,
                $"SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active' AND LocalPrincipalId='{local.Principal:D}';") == 1,
                "same local Owner survives both credential generations");
            RepairCheck(HostDatabase.QueryScalarLong(local.State.State.Writer,
                $"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ReplacementId='{approval:D}' AND CurrentIncarnation={RepairIncarnation(local, remote)} AND ConfirmedUtc IS NOT NULL AND InvalidatedUtc IS NULL;") == 1,
                "original approval persists across incoming recovery clear");
        }
        RepairRefuse(() => ga.RequireRemoteHostCapability(oldProofA, HostCapability.CreateServer, a.State.State.HostId));
        RepairRefuse(() => gb.RequireRemoteHostCapability(oldProofB, HostCapability.CreateServer, b.State.State.HostId));
        RepairCheck(await a.Generation.ConfirmRecoveryAsync(b.State.State.HostId, b.Generation.Endpoints.Value.Peer) == PeerRecoveryCompletionExchange.NoPending,
            "confirmed original marker remains read only after incarnation carry");
        Console.WriteLine("PASS genuine native simultaneous own-key recovery: stopped old generations, fresh keys and PAKE, independent Owner approvals and exact two-sided recovery clears.");
    }
}
