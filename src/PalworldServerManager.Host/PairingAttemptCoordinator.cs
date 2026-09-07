using System.Net;
using System.Security.Cryptography;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum PairingAttemptOutcome { Failed, Expired, Cancelled, IdentityVerified }
internal enum HostPairingPhase { Created, PAKEAuthenticated, IdentityVerified, Failed, Expired, Disposed }
internal sealed record PairingInvitation(Guid Id, DateTimeOffset ExpiresUtc, RedactedSecret Code) : IDisposable
{
    public void Dispose() => Code.Dispose();
}

// Host-internal responder lifecycle. The future RPC composition must authorize local creation
// and supply the actual transport source address, never a request's claimed address.
// No listener, persistence, private Host key, PeerBound/Active state or grant is exposed here.
internal sealed class PairingAttemptCoordinator : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SourceLifetime = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly IPairingKeyExchangeFactory factory;
    private readonly TimeProvider time;
    private readonly Action<Guid, PairingAttemptOutcome> report;
    private readonly Dictionary<Guid, InvitationState> invitations = [];
    private readonly Dictionary<string, SourceState> sources = [];
    private readonly ITimer timer;
    private bool disposed;

    internal sealed class InvitationState(Guid id, long issued, RedactedSecret code)
    {
        internal readonly Guid Id = id;
        internal readonly long Issued = issued;
        internal readonly RedactedSecret Code = code;
        internal int Failures;
        internal Attempt? Active;
    }
    internal sealed class SourceState(long now)
    {
        internal long Last = now;
        internal int Failures;
        internal TimeSpan Delay;
        internal bool Active;
    }

    internal PairingAttemptCoordinator(IPairingKeyExchangeFactory factory,
        Action<Guid, PairingAttemptOutcome> report, TimeProvider? timeProvider = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.report = report ?? throw new ArgumentNullException(nameof(report)); time = timeProvider ?? TimeProvider.System;
        timer = time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    internal PairingInvitation CreateInvitation()
    {
        lock (gate)
        {
            CheckAlive(); SweepCore();
            if (invitations.Count >= 16) throw Refused();
            var bytes = new byte[10];
            try
            {
                for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)('0' + RandomNumberGenerator.GetInt32(10));
                var id = Guid.NewGuid();
                invitations.Add(id, new(id, time.GetTimestamp(), new RedactedSecret(bytes)));
                return new(id, time.GetUtcNow() + Lifetime, new RedactedSecret(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal Attempt Begin(Guid invitationId, IPAddress trustedSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trustedSource);
        lock (gate)
        {
            CheckAlive(); SweepCore(); ct.ThrowIfCancellationRequested();
            // Normalize IPv4-mapped IPv6; changing address syntax cannot reset backoff.
            var sourceKey = (trustedSource.IsIPv4MappedToIPv6 ? trustedSource.MapToIPv4() : trustedSource).ToString();
            if (!sources.TryGetValue(sourceKey, out var source))
            {
                if (sources.Count >= 256) throw Refused();
                source = new(time.GetTimestamp()); sources.Add(sourceKey, source);
            }
            if (source.Active || time.GetElapsedTime(source.Last) < source.Delay) throw Refused();
            if (!invitations.TryGetValue(invitationId, out var invitation) || invitation.Failures >= 10 || invitation.Active is not null)
            { Penalize(source); throw Refused(); }
            var code = invitation.Code.CopyBytes();
            var attemptId = Guid.NewGuid();
            IPairingKeyExchange? exchange = null;
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(32);
                exchange = factory.Start(PairingRole.Responder, code, nonce, ct);
                ct.ThrowIfCancellationRequested();
                if (time.GetElapsedTime(invitation.Issued) >= Lifetime)
                { Remove(invitation, PairingAttemptOutcome.Expired); throw Refused(); }
                var attempt = new Attempt(this, invitation, source, exchange, nonce, attemptId);
                invitation.Active = attempt; source.Active = true;
                return attempt;
            }
            catch
            {
                try { exchange?.Dispose(); } finally { Penalize(source); Report(attemptId, PairingAttemptOutcome.Failed); }
                throw;
            }
            finally { CryptographicOperations.ZeroMemory(code); }
        }
    }

    internal void CancelInvitation(Guid invitationId)
    {
        lock (gate)
        {
            CheckAlive();
            if (invitations.TryGetValue(invitationId, out var invitation)) Remove(invitation, PairingAttemptOutcome.Cancelled);
        }
    }
    internal void Sweep() { lock (gate) { if (!disposed) SweepCore(); } }
    private void SweepCore()
    {
        foreach (var invitation in invitations.Values.ToArray())
            if (time.GetElapsedTime(invitation.Issued) >= Lifetime) Remove(invitation, PairingAttemptOutcome.Expired);
        foreach (var source in sources.Where(p => !p.Value.Active && time.GetElapsedTime(p.Value.Last) >= SourceLifetime).Select(p => p.Key).ToArray())
            sources.Remove(source);
    }
    private void Remove(InvitationState invitation, PairingAttemptOutcome outcome)
    {
        invitations.Remove(invitation.Id); invitation.Code.Dispose();
        if (invitation.Active is { } attempt) Finish(attempt, outcome);
        else Report(invitation.Id, outcome);
    }
    private void Penalize(SourceState source)
    {
        source.Last = time.GetTimestamp(); source.Failures = Math.Min(source.Failures + 1, 6);
        source.Delay = TimeSpan.FromSeconds(Math.Min(30, 1 << (source.Failures - 1)));
    }
    private void Finish(Attempt attempt, PairingAttemptOutcome outcome)
    {
        if (attempt.ended) return;
        attempt.ended = true;
        try { attempt.exchange.Dispose(); } catch { /* Continue terminal bookkeeping even on provider cleanup failure. */ }
        attempt.invitation.Active = null; attempt.source.Active = false;
        attempt.phase = outcome switch {
            PairingAttemptOutcome.IdentityVerified => HostPairingPhase.IdentityVerified,
            PairingAttemptOutcome.Expired => HostPairingPhase.Expired,
            PairingAttemptOutcome.Cancelled => HostPairingPhase.Disposed,
            _ => HostPairingPhase.Failed
        };
        if (outcome == PairingAttemptOutcome.IdentityVerified)
        {
            invitations.Remove(attempt.invitation.Id); attempt.invitation.Code.Dispose();
            attempt.source.Last = time.GetTimestamp(); attempt.source.Delay = TimeSpan.FromSeconds(1);
        }
        else
        {
            Penalize(attempt.source);
            if (++attempt.invitation.Failures >= 10) attempt.invitation.Code.Dispose();
        }
        // A broken audit consumer cannot retain cryptographic state or revive an attempt.
        Report(attempt.Id, outcome);
    }
    private void Report(Guid id, PairingAttemptOutcome outcome) { try { report(id, outcome); } catch { } }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    private static CryptographicException Refused() => new("Pairing attempt rejected.");
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            foreach (var invitation in invitations.Values.ToArray()) Remove(invitation, PairingAttemptOutcome.Cancelled);
            sources.Clear(); timer.Dispose();
        }
    }

    internal sealed class Attempt
    {
        private readonly PairingAttemptCoordinator owner;
        internal readonly InvitationState invitation;
        internal readonly SourceState source;
        internal readonly IPairingKeyExchange exchange;
        private readonly byte[] nonce;
        internal bool ended;
        internal HostPairingPhase phase = HostPairingPhase.Created;
        internal Guid Id { get; }
        internal Attempt(PairingAttemptCoordinator owner, InvitationState invitation, SourceState source, IPairingKeyExchange exchange, byte[] nonce, Guid id)
        { this.owner = owner; this.invitation = invitation; this.source = source; this.exchange = exchange; this.nonce = nonce; Id = id; }
        internal HostPairingPhase Phase { get { lock (owner.gate) return phase; } }
        internal byte[] SessionNonce => Run(() => nonce.ToArray());
        internal byte[] InitialMessage => Run(() => exchange.InitialMessage);
        internal byte[] ReceivePeerMessage(byte[] message, CancellationToken ct = default) => Run(() => exchange.ReceivePeerMessage(message, ct), ct);
        internal byte[] ConfirmPeer(byte[] message, CancellationToken ct = default) => Run(() => {
            var reply = exchange.ConfirmPeer(message, ct); phase = HostPairingPhase.PAKEAuthenticated; return reply;
        }, ct);
        internal byte[] CreateIdentityBinding(Guid hostId, byte[] credential, CancellationToken ct = default)
            => Run(() => exchange.CreateIdentityBinding(hostId, credential, ct), ct);
        internal VerifiedPairingIdentity VerifyIdentityBinding(byte[] message, CancellationToken ct = default)
            => Run(() => {
                var peer = exchange.VerifyIdentityBinding(message, ct);
                ct.ThrowIfCancellationRequested();
                CheckDeadline();
                owner.Finish(this, PairingAttemptOutcome.IdentityVerified); return peer;
            }, ct);
        private T Run<T>(Func<T> action, CancellationToken ct = default)
        {
            lock (owner.gate)
            {
                owner.CheckAlive(); owner.SweepCore();
                if (ended) throw Refused();
                try
                {
                    ct.ThrowIfCancellationRequested(); var value = action(); ct.ThrowIfCancellationRequested();
                    if (!ended) CheckDeadline();
                    return value;
                }
                catch (OperationCanceledException) { owner.Finish(this, PairingAttemptOutcome.Failed); throw; }
                catch { owner.Finish(this, PairingAttemptOutcome.Failed); throw Refused(); }
            }
        }
        private void CheckDeadline()
        {
            if (owner.time.GetElapsedTime(invitation.Issued) < Lifetime) return;
            owner.Remove(invitation, PairingAttemptOutcome.Expired); throw Refused();
        }
        // Transport must call on disconnect; the timer is a bounded fallback, not connection ownership.
        internal void Disconnect()
        {
            lock (owner.gate)
            {
                if (!owner.disposed) owner.SweepCore();
                if (!ended) owner.Finish(this, PairingAttemptOutcome.Failed);
            }
        }
    }
}
