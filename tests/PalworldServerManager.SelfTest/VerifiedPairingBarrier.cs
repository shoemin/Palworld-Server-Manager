using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

// Qualification only: every byte and proof comes from the actual selected provider.
// The verified result cannot return to the production persistence caller in this process.
internal sealed class VerifiedPairingBarrier(IPairingKeyExchangeFactory provider) : IPairingKeyExchangeFactory
{
    private Func<VerifiedPairingIdentity, CancellationToken, Task>? signal;
    internal void Arm(Func<VerifiedPairingIdentity, CancellationToken, Task> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Interlocked.CompareExchange(ref signal, value, null) is not null) throw new InvalidOperationException("Barrier already armed.");
    }
    public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
    {
        if (role != PairingRole.Initiator || signal is null) throw new InvalidOperationException("Only the armed initiator may use this barrier.");
        return new Exchange(provider.Start(role, code, nonce, ct), signal);
    }
    private sealed class Exchange(IPairingKeyExchange inner, Func<VerifiedPairingIdentity, CancellationToken, Task> signal) : IPairingKeyExchange
    {
        public PairingExchangeState State => inner.State;
        public byte[] InitialMessage => inner.InitialMessage;
        public byte[] ReceivePeerMessage(byte[] message, CancellationToken ct = default) => inner.ReceivePeerMessage(message, ct);
        public byte[] ConfirmPeer(byte[] confirmation, CancellationToken ct = default) => inner.ConfirmPeer(confirmation, ct);
        public byte[] CreateIdentityBinding(Guid host, byte[] credential, CancellationToken ct = default) => inner.CreateIdentityBinding(host, credential, ct);
        public VerifiedPairingIdentity VerifyIdentityBinding(byte[] message, CancellationToken ct = default)
        {
            var verified = inner.VerifyIdentityBinding(message, ct);
            signal(verified, ct).GetAwaiter().GetResult();
            // Parent kills this exact live process after reading the public phase. Cancellation
            // may fail the exchange, but can never release a result to Runtime.Store.
            Task.Delay(Timeout.Infinite, ct).GetAwaiter().GetResult();
            throw new InvalidOperationException("Unreachable verification barrier.");
        }
        public void Dispose() => inner.Dispose();
    }
}
