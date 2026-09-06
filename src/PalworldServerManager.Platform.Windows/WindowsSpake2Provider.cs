using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

/// Loads only an explicitly selected, integrity-checked Host component. No global DLL search.
/// Composition must supply a build-pinned hash and a protected installation path.
public sealed class WindowsSpake2Provider : IPairingKeyExchangeFactory, IDisposable
{
    private readonly object sync = new();
    private readonly nint library;
    private readonly Create create;
    private readonly Step step;
    private readonly Close close;
    private readonly HashSet<Exchange> exchanges = [];
    // Failed constructors must never leave a finalizer able to free an absent/already-freed DLL.
    private bool disposed = true;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Abi();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Create(byte role, byte[] code, nuint codeLength, byte[] nonce, nuint nonceLength, [Out] byte[] share, nuint shareLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(ulong handle, uint operation, byte[] input, nuint inputLength, [Out] byte[] output, nuint outputLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(ulong handle);

    public WindowsSpake2Provider(string absoluteLibraryPath, string expectedSha256)
    {
        if (!Path.IsPathFullyQualified(absoluteLibraryPath) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new ArgumentException("A Windows x64 component at an absolute path is required.");
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException) { throw new ArgumentException("Invalid component digest."); }
        if (expected.Length != 32) throw new ArgumentException("Invalid component digest.");
        // Deny file writes/deletion while checking and loading the same path.
        using var file = new FileStream(absoluteLibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(file)))
            throw new CryptographicException("Pairing component integrity check failed.");
        library = NativeLibrary.Load(absoluteLibraryPath);
        try
        {
            if (Marshal.GetDelegateForFunctionPointer<Abi>(NativeLibrary.GetExport(library, "psm_pake_abi"))() != 1)
                throw new CryptographicException("Unsupported pairing component ABI.");
            create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(library, "psm_pake_create"));
            step = Marshal.GetDelegateForFunctionPointer<Step>(NativeLibrary.GetExport(library, "psm_pake_step"));
            close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "psm_pake_close"));
            disposed = false;
        }
        catch { NativeLibrary.Free(library); throw; }
    }

    public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] sessionNonce, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code); ArgumentNullException.ThrowIfNull(sessionNonce);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (role is not (PairingRole.Initiator or PairingRole.Responder) || code.Length != 10 ||
                code.Any(value => value is < (byte)'0' or > (byte)'9') || sessionNonce.Length != 32)
                throw new ArgumentException("Invalid pairing profile input.");
            var privateCode = code.ToArray(); var nonce = sessionNonce.ToArray(); var share = new byte[65];
            try
            {
                var handle = create((byte)role, privateCode, (nuint)privateCode.Length, nonce, 32, share, 65);
                if (handle == 0) throw new CryptographicException("Pairing exchange could not be initialized.");
                if (cancellationToken.IsCancellationRequested) { close(handle); cancellationToken.ThrowIfCancellationRequested(); }
                var exchange = new Exchange(this, handle, share, role); exchanges.Add(exchange); return exchange;
            }
            finally { CryptographicOperations.ZeroMemory(privateCode); }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            foreach (var exchange in exchanges.ToArray()) exchange.Dispose();
            disposed = true; NativeLibrary.Free(library);
        }
        GC.SuppressFinalize(this);
    }
    ~WindowsSpake2Provider() { Dispose(); }

    private sealed class Exchange(WindowsSpake2Provider provider, ulong handle, byte[] share, PairingRole role) : IPairingKeyExchange
    {
        private ulong nativeHandle = handle;
        private PairingExchangeState state = PairingExchangeState.Created;
        private byte[]? ownBinding;
        private VerifiedPairingIdentity? verifiedPeer;
        public PairingExchangeState State { get { lock (provider.sync) return state; } }
        public byte[] InitialMessage { get { lock (provider.sync) { EnsureLive(); return share.ToArray(); } } }

        public byte[] ReceivePeerMessage(byte[] message, CancellationToken cancellationToken = default)
            => Run(cancellationToken, () => {
                Require(PairingExchangeState.Created);
                if (message.Length != 65 || message[0] != 4) throw Failure();
                var mac = Call(0, message, role == PairingRole.Initiator ? 32 : 0); state = PairingExchangeState.AwaitingConfirmation; return mac;
            });
        public byte[] ConfirmPeer(byte[] confirmation, CancellationToken cancellationToken = default)
            => Run(cancellationToken, () => {
                Require(PairingExchangeState.AwaitingConfirmation);
                if (confirmation.Length != 32) throw Failure();
                var mac = Call(1, confirmation, role == PairingRole.Responder ? 32 : 0); state = PairingExchangeState.Confirmed; return mac;
            });
        public byte[] CreateIdentityBinding(Guid hostId, byte[] publicCredential, CancellationToken cancellationToken = default)
            => Run(cancellationToken, () => {
                Require(PairingExchangeState.Confirmed);
                var credential = publicCredential.ToArray(); ValidateCredential(credential);
                if (hostId == Guid.Empty) throw Failure();
                var payload = new byte[20 + credential.Length];
                hostId.TryWriteBytes(payload.AsSpan(0, 16), bigEndian: true, out _);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), (uint)credential.Length);
                credential.CopyTo(payload, 20);
                if (verifiedPeer?.HostId == hostId) throw Failure();
                var mac = Call(2, payload, 32);
                ownBinding = payload; return payload.Concat(mac).ToArray();
            });
        public VerifiedPairingIdentity VerifyIdentityBinding(byte[] message, CancellationToken cancellationToken = default)
            => Run(cancellationToken, () => {
                Require(PairingExchangeState.Confirmed);
                if (ownBinding is null) throw Failure();
                if (message.Length is < 53 or > 1076) throw Failure();
                var copy = message.ToArray();
                Call(3, copy, 0);
                var length = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(16, 4));
                if (length != copy.Length - 52) throw Failure();
                var id = new Guid(copy.AsSpan(0, 16), bigEndian: true);
                if (id == Guid.Empty || (ownBinding is not null && id == new Guid(ownBinding.AsSpan(0,16), bigEndian: true))) throw Failure();
                var credential = copy.AsSpan(20, (int)length).ToArray(); ValidateCredential(credential);
                verifiedPeer = new VerifiedPairingIdentity(id, credential); return verifiedPeer;
            });

        private T Run<T>(CancellationToken token, Func<T> action)
        {
            lock (provider.sync)
            {
                EnsureLive();
                try { token.ThrowIfCancellationRequested(); var result = action(); token.ThrowIfCancellationRequested(); return result; }
                catch (OperationCanceledException) { Fail(); throw; }
                catch { Fail(); throw Failure(); }
            }
        }
        private byte[] Call(uint operation, byte[] input, int outputLength)
        {
            // Never marshal a caller-owned mutable buffer directly into native code.
            var copy = input.ToArray(); var output = new byte[outputLength];
            if (provider.step(nativeHandle, operation, copy, (nuint)copy.Length, output, (nuint)output.Length) != 0) throw Failure();
            return output;
        }
        private static void ValidateCredential(byte[] bytes)
        {
            if (bytes.Length is < 1 or > 1024) throw Failure();
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !CryptographicOperations.FixedTimeEquals(key.ExportSubjectPublicKeyInfo(), bytes)) throw Failure();
        }
        private void Require(PairingExchangeState expected) { if (state != expected) throw Failure(); }
        private void EnsureLive()
        {
            if (state is PairingExchangeState.Disposed || provider.disposed) throw new ObjectDisposedException(nameof(IPairingKeyExchange));
            if (state is PairingExchangeState.Failed) throw Failure();
        }
        private void Fail() { provider.close(nativeHandle); nativeHandle = 0; state = PairingExchangeState.Failed; provider.exchanges.Remove(this); }
        private static CryptographicException Failure() => new("Pairing exchange rejected.");
        public void Dispose()
        {
            lock (provider.sync)
            {
                if (state == PairingExchangeState.Disposed) return;
                if (!provider.disposed) provider.close(nativeHandle);
                nativeHandle = 0; state = PairingExchangeState.Disposed; provider.exchanges.Remove(this);
            }
        }
    }
}
