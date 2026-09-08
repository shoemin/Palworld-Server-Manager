using System.Security.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using HostCapability = PalworldServerManager.Core.Authorization.HostCapability;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;
using ServerRef = PalworldServerManager.Core.Authorization.ServerRef;

namespace PalworldServerManager.Host;

// Trusted internal Host dispatch, not a wire endpoint. Future RPC handlers must negotiate
// their own operation features. The callback is synchronous Host code for one canonical
// action/atomic preset, not an arbitrary client callback or a multi-operation transaction.
internal sealed class LocalPermissionDispatcher(LocalSecurityRpcRuntime runtime, GrantPolicyRepository repository)
{
    internal async Task<T> Invoke<T>(HttpContext context, Func<LocalPermissionCall,T> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action); PermissionDispatchChannel.Require(context);
        var connection = context.Features.Get<LocalSecurityRpcConnection>();
        if (connection is null || !connection.BelongsTo(runtime)) throw new AuthenticationException("Local connection refused.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, context.RequestAborted);
        return await connection.Invoke(runtime.NativePrincipal(context), session =>
        {
            if (session.Protocol is null) throw new InvalidOperationException("Negotiate this connection first.");
            session.Protocol.Require(FeatureCapability.LocalPrincipalSecurity);
            if (!runtime.IsInitialized()) throw new AuthenticationException("Initialized Host required.");
            using var call = new LocalPermissionCall(repository, session.Authentication.GetCurrentPrincipal().MutationActor, cancellation.Token);
            return Task.FromResult(action(call));
        }, cancellation.Token).ConfigureAwait(false);
    }
}

internal sealed class PeerPermissionDispatcher(PeerSecurityRpcRuntime runtime, GrantPolicyRepository repository)
{
    internal async Task<T> Invoke<T>(HttpContext context, Func<PeerPermissionCall,T> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action); PermissionDispatchChannel.Require(context);
        var connection = context.Features.Get<PeerSecurityRpcConnection>();
        if (connection is null || !connection.BelongsTo(runtime)) throw new AuthenticationException("Peer connection refused.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, context.RequestAborted);
        return await connection.Invoke(session =>
        {
            if (session.Protocol is null) throw new InvalidOperationException("Negotiate this connection first.");
            session.Protocol.Require(FeatureCapability.PeerTrustActivation);
            var proof = new PeerGrantMutationActor(runtime.HostId, session.PeerId, session.PeerFingerprint, session.LocalFingerprint, session.PeerIncarnation);
            runtime.Authentication.AuthenticateOrdinary(proof, cancellation.Token);
            using var call = new PeerPermissionCall(repository, proof, cancellation.Token);
            return action(call);
        }, cancellation.Token).ConfigureAwait(false);
    }
}

internal static class PermissionDispatchChannel
{
    internal static void Require(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.IsHttps || context.Request.Protocol != "HTTP/2") throw new AuthenticationException("Protected Host channel required.");
    }
}

// These facades never expose actor evidence, Owner flags, policy snapshots or the repository.
// A captured facade cannot outlive its callback or move to another thread. Every action
// still revalidates current proof/policy inside its own canonical repository transaction.
internal abstract class PermissionCall(CancellationToken ct) : IDisposable
{
    private readonly int thread = Environment.CurrentManagedThreadId;
    private int disposed;
    protected CancellationToken Cancellation => ct;
    protected void Guard()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Permission call belongs to its synchronous Host callback.");
        ct.ThrowIfCancellationRequested();
    }
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}

internal sealed class LocalPermissionCall(GrantPolicyRepository repository, LocalPrincipalMutationActor actor, CancellationToken ct) : PermissionCall(ct)
{
    internal long RequireHost(HostCapability capability, Guid target)
    { Guard(); return repository.RequireLocalHostCapability(actor, capability, target, Cancellation); }
    internal long RequireServer(ServerCapability capability, ServerRef target)
    { Guard(); return repository.RequireLocalServerCapability(actor, capability, target, Cancellation); }
    internal GrantMutationResult IssueHost(long revision, HostGrantRequest request)
    {
        Guard(); ArgumentNullException.ThrowIfNull(request);
        return repository.IssueHost(actor, revision, request.GrantId, request.Grantee, request.Capability, request.TargetHostId, request.Rights, request.SourceGrantId, Cancellation);
    }
    internal GrantMutationResult IssueServer(long revision, ServerGrantRequest request)
    {
        Guard(); ArgumentNullException.ThrowIfNull(request);
        return repository.IssueServer(actor, revision, request.GrantId, request.Grantee, request.Capability, request.Target, request.Rights, request.SourceGrantId, Cancellation);
    }
    internal PresetGrantResult ApplyPreset(long revision, RolePreset preset)
    { Guard(); return repository.ApplyPreset(actor, revision, preset, Cancellation); }
    internal DefaultGrantTemplateSnapshot ConfigureDefaults(long revision, DefaultGrantTemplate template)
    { Guard(); return repository.ConfigureDefaults(actor, revision, template, Cancellation); }
    internal HistoricalGrantReissueResult ReissueHistorical(long revision, Guid peer, IEnumerable<Guid> hosts, IEnumerable<Guid> servers)
    { Guard(); return repository.ReissueHistoricalPeerRoots(actor, revision, peer, hosts, servers, Cancellation); }
    internal GrantMutationResult InvalidateHost(long revision, Guid root)
    { Guard(); return repository.InvalidateHostSubtree(actor, revision, root, Cancellation); }
    internal GrantMutationResult InvalidateServer(long revision, Guid root)
    { Guard(); return repository.InvalidateServerSubtree(actor, revision, root, Cancellation); }
    internal PeerTrustRevocationResult RevokePeer(long revision, Guid peer, long incarnation)
    { Guard(); return repository.RevokeLocalPeerTrust(actor, revision, peer, incarnation, Cancellation); }
}

internal sealed class PeerPermissionCall(GrantPolicyRepository repository, PeerGrantMutationActor actor, CancellationToken ct) : PermissionCall(ct)
{
    internal long RequireHost(HostCapability capability, Guid target)
    { Guard(); return repository.RequireRemoteHostCapability(actor, capability, target, Cancellation); }
    internal long RequireServer(ServerCapability capability, ServerRef target)
    { Guard(); return repository.RequireRemoteServerCapability(actor, capability, target, Cancellation); }
    internal GrantMutationResult IssueHost(long revision, HostGrantRequest request)
    {
        Guard(); ArgumentNullException.ThrowIfNull(request);
        return repository.IssueRemoteHost(actor, revision, request.GrantId, request.Grantee, request.Capability, request.TargetHostId, request.Rights, request.SourceGrantId, Cancellation);
    }
    internal GrantMutationResult IssueServer(long revision, ServerGrantRequest request)
    {
        Guard(); ArgumentNullException.ThrowIfNull(request);
        return repository.IssueRemoteServer(actor, revision, request.GrantId, request.Grantee, request.Capability, request.Target, request.Rights, request.SourceGrantId, Cancellation);
    }
    internal PresetGrantResult ApplyPreset(long revision, RolePreset preset)
    { Guard(); return repository.ApplyRemotePreset(actor, revision, preset, Cancellation); }
    internal CreatorGrantResult CommitConfirmedCreation(long revision, ServerRef target, Action<SqliteConnection,SqliteTransaction> recordConfirmedCreation)
    { Guard(); return repository.CommitConfirmedRemoteCreation(actor, revision, target, recordConfirmedCreation, Cancellation); }
    internal PeerTrustRevocationResult RevokePeer(long revision, Guid peer, long incarnation)
    { Guard(); return repository.RevokeRemotePeerTrust(actor, revision, peer, incarnation, Cancellation); }
}
