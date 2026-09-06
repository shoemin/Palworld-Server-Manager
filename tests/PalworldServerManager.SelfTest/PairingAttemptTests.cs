using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class PairingAttemptTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Pairing lifecycle assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (CryptographicException) { return; } catch (ObjectDisposedException) { return; }
        throw new Exception("Expected pairing lifecycle refusal.");
    }
    private sealed class Clock : TimeProvider
    {
        private long stamp;
        internal DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => stamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        internal void Advance(double seconds) => stamp += (long)(seconds * 1000);
        internal Action? Tick;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Tick = () => callback(state); return new TimerStub(); }
        private sealed class TimerStub : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    // Lifetime oracle only; actual cryptographic guarantees use Native below and #44a probes.
    private sealed class Factory : IPairingKeyExchangeFactory
    {
        internal readonly List<Exchange> Exchanges = [];
        internal Action? OnStart;
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken cancellationToken = default)
        {
            Check(role == PairingRole.Responder && code.Length == 10 && code.All(b => b >= '0' && b <= '9') && nonce.Length == 32);
            OnStart?.Invoke(); var e = new Exchange(); Exchanges.Add(e); return e;
        }
    }
    private sealed class Exchange : IPairingKeyExchange
    {
        internal int Disposals, Calls;
        internal Action? OnVerify;
        public PairingExchangeState State { get; private set; } = PairingExchangeState.Created;
        public byte[] InitialMessage => new byte[65];
        public byte[] ReceivePeerMessage(byte[] message, CancellationToken cancellationToken = default)
        { Calls++; if (message.Length != 65) throw new CryptographicException(); State = PairingExchangeState.AwaitingConfirmation; return []; }
        public byte[] ConfirmPeer(byte[] confirmation, CancellationToken cancellationToken = default)
        { Calls++; if (confirmation.Length != 32) throw new CryptographicException(); State = PairingExchangeState.Confirmed; return new byte[32]; }
        public byte[] CreateIdentityBinding(Guid id, byte[] key, CancellationToken cancellationToken = default) => [1];
        public VerifiedPairingIdentity VerifyIdentityBinding(byte[] message, CancellationToken cancellationToken = default)
        { OnVerify?.Invoke(); return new(Guid.NewGuid(), [1]); }
        public void Dispose() { Disposals++; State = PairingExchangeState.Disposed; }
    }
    public static Task Lifecycle()
    {
        var clock = new Clock(); var factory = new Factory(); var outcomes = new List<(Guid, PairingAttemptOutcome)>();
        using var host = new PairingAttemptCoordinator(factory, (id, outcome) => outcomes.Add((id, outcome)), clock);
        using var invitation = host.CreateInvitation();
        var secret = invitation.Code.CopyBytes();
        try { Check(!JsonSerializer.Serialize(invitation).Contains(Encoding.ASCII.GetString(secret))); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        var address = IPAddress.Parse("192.0.2.1");
        var first = host.Begin(invitation.Id, address); var nonce = first.SessionNonce;
        first.SessionNonce[0] ^= 1; Check(nonce.SequenceEqual(first.SessionNonce));
        Parallel.For(0, 8, _ => Reject(() => host.Begin(invitation.Id, address)));
        Reject(() => first.ConfirmPeer([])); var calls = factory.Exchanges[0].Calls;
        for (var i = 0; i < 15; i++) Reject(() => first.ConfirmPeer(new byte[32]));
        Check(factory.Exchanges[0].Calls == calls && factory.Exchanges[0].Disposals == 1 && outcomes.Count == 1);
        Reject(() => host.Begin(invitation.Id, IPAddress.Parse("::ffff:192.0.2.1")));
        clock.Advance(1);
        var second = host.Begin(invitation.Id, address); Check(!second.SessionNonce.SequenceEqual(nonce)); second.Disconnect(); second.Disconnect();
        Check(outcomes.Count == 2);
        using var another = host.CreateInvitation(); Reject(() => host.Begin(another.Id, address));
        // Distinct sources cannot evade the code's global ten-failure budget.
        for (var i = 2; i < 10; i++) host.Begin(invitation.Id, IPAddress.Parse("192.0.2." + (i + 1))).Disconnect();
        Check(outcomes.Count == 10); Reject(() => host.Begin(invitation.Id, IPAddress.Parse("192.0.2.90")));
        Check(factory.Exchanges.Count == 10);
        return Task.CompletedTask;
    }
    public static Task ExpiryAndCleanup()
    {
        var clock = new Clock(); var factory = new Factory(); var outcomes = new List<PairingAttemptOutcome>();
        var host = new PairingAttemptCoordinator(factory, (_, outcome) => outcomes.Add(outcome), clock);
        using var invitation = host.CreateInvitation();
        var attempt = host.Begin(invitation.Id, IPAddress.Loopback); attempt.ReceivePeerMessage(new byte[65]); attempt.ConfirmPeer(new byte[32]);
        Check(attempt.Phase == HostPairingPhase.PAKEAuthenticated);
        clock.Utc = clock.Utc.AddYears(-1); clock.Advance(300); clock.Tick!();
        Check(attempt.Phase == HostPairingPhase.Expired && factory.Exchanges[0].Disposals == 1);
        Reject(() => attempt.CreateIdentityBinding(Guid.NewGuid(), [1])); Reject(() => host.Begin(invitation.Id, IPAddress.Loopback));
        clock.Tick!(); Check(outcomes.Count == 1);
        using var next = host.CreateInvitation(); var pending = host.Begin(next.Id, IPAddress.Parse("192.0.2.1"));
        host.Dispose(); host.Dispose(); clock.Tick!();
        Check(factory.Exchanges.All(e => e.Disposals == 1)); Reject(() => pending.InitialMessage.ToString());
        using var restarted = new PairingAttemptCoordinator(factory, (_, _) => { }, clock);
        Reject(() => restarted.Begin(next.Id, IPAddress.Loopback));
        using var midFlight = restarted.CreateInvitation();
        var late = restarted.Begin(midFlight.Id, IPAddress.Parse("192.0.2.2"));
        factory.Exchanges.Last().OnVerify = () => clock.Advance(300);
        Reject(() => late.VerifyIdentityBinding([1])); Check(late.Phase == HostPairingPhase.Expired);
        // Expiry during expensive native creation must dispose its unregistered result too.
        using var creating = restarted.CreateInvitation(); factory.OnStart = () => clock.Advance(300);
        Reject(() => restarted.Begin(creating.Id, IPAddress.Parse("192.0.2.3")));
        Check(factory.Exchanges.Last().Disposals == 1);
        return Task.CompletedTask;
    }
    public static Task BoundsAndCancellation()
    {
        var clock = new Clock(); var factory = new Factory();
        using var host = new PairingAttemptCoordinator(factory, (_, _) => throw new Exception("Audit sink unavailable"), clock);
        var invitations = Enumerable.Range(0, 16).Select(_ => host.CreateInvitation()).ToArray();
        try
        {
            Reject(() => host.CreateInvitation());
            host.CancelInvitation(invitations[0].Id); using var replacement = host.CreateInvitation();
            using var cancellation = new CancellationTokenSource();
            factory.OnStart = cancellation.Cancel;
            try { host.Begin(replacement.Id, IPAddress.Loopback, cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Check(factory.Exchanges.Single().Disposals == 1); factory.OnStart = null; clock.Advance(1);
            var attempt = host.Begin(replacement.Id, IPAddress.Loopback);
            try { attempt.ReceivePeerMessage(new byte[65], cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Reject(() => attempt.ConfirmPeer(new byte[32])); Check(factory.Exchanges.Last().Disposals == 1);
            // Fill bounded source state; unexpired penalties cannot be evicted by address churn.
            for (var i = 0; i < 255; i++) Reject(() => host.Begin(Guid.NewGuid(), new IPAddress(new byte[] { 198, 51, 100, (byte)i })));
            Reject(() => host.Begin(replacement.Id, IPAddress.Parse("203.0.113.1")));
            clock.Advance(601); host.Sweep(); using var fresh = host.CreateInvitation();
            host.Begin(fresh.Id, IPAddress.Parse("203.0.113.1")).Disconnect();
        }
        finally { foreach (var invitation in invitations) invitation.Dispose(); }
        return Task.CompletedTask;
    }
    public static void Native(string path)
    {
        using var provider = new WindowsSpake2Provider(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var clock = new Clock(); var outcomes = new List<PairingAttemptOutcome>();
        using var host = new PairingAttemptCoordinator(provider, (_, outcome) => outcomes.Add(outcome), clock);
        using var invitation = host.CreateInvitation();
        var responder = host.Begin(invitation.Id, IPAddress.Loopback); var code = invitation.Code.CopyBytes();
        try
        {
            using var initiator = provider.Start(PairingRole.Initiator, code, responder.SessionNonce);
            var ca = initiator.ReceivePeerMessage(responder.InitialMessage); Check(responder.ReceivePeerMessage(initiator.InitialMessage).Length == 0);
            initiator.ConfirmPeer(responder.ConfirmPeer(ca));
            using var ka = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var kb = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var ida = Guid.NewGuid(); var idb = Guid.NewGuid();
            var ba = initiator.CreateIdentityBinding(ida, ka.ExportSubjectPublicKeyInfo());
            var bb = responder.CreateIdentityBinding(idb, kb.ExportSubjectPublicKeyInfo());
            Check(initiator.VerifyIdentityBinding(bb).HostId == idb);
            Check(responder.VerifyIdentityBinding(ba).HostId == ida);
            Check(outcomes.SequenceEqual(new[] { PairingAttemptOutcome.IdentityVerified }));
            Reject(() => responder.VerifyIdentityBinding(ba)); clock.Advance(31);
            Reject(() => host.Begin(invitation.Id, IPAddress.Loopback));
            using var wrongInvitation = host.CreateInvitation(); var wrong = host.Begin(wrongInvitation.Id, IPAddress.Parse("192.0.2.1"));
            var wrongCode = wrongInvitation.Code.CopyBytes();
            wrongCode[0] = wrongCode[0] == (byte)'9' ? (byte)'0' : (byte)(wrongCode[0] + 1);
            using var attacker = provider.Start(PairingRole.Initiator, wrongCode, wrong.SessionNonce);
            CryptographicOperations.ZeroMemory(wrongCode);
            var bad = attacker.ReceivePeerMessage(wrong.InitialMessage); wrong.ReceivePeerMessage(attacker.InitialMessage);
            Reject(() => wrong.ConfirmPeer(bad)); Reject(() => wrong.CreateIdentityBinding(Guid.NewGuid(), ka.ExportSubjectPublicKeyInfo()));
            Check(outcomes.Count == 2 && outcomes[1] == PairingAttemptOutcome.Failed);
        }
        finally { CryptographicOperations.ZeroMemory(code); }
        Console.WriteLine("PASS actual native Host pairing coordinator: reciprocal binding, single-use code, wrong-code rejection and terminal cleanup.");
    }
}
