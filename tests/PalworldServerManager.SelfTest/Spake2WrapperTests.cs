using System.Security.Cryptography;
using System.Text;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class Spake2WrapperTests
{
    private static readonly byte[] Code = Encoding.ASCII.GetBytes("1234567890");
    private static byte[] Credential() { using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); return key.ExportSubjectPublicKeyInfo(); }
    private static void Check(bool value) { if (!value) throw new Exception("Pairing wrapper assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (CryptographicException) { return; } catch (ObjectDisposedException) { return; }
        throw new Exception("Expected pairing refusal.");
    }
    private static (IPairingKeyExchange A, IPairingKeyExchange B) Pair(WindowsSpake2Provider provider, bool confirm = true, byte[]? otherCode = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var a = provider.Start(PairingRole.Initiator, Code, nonce);
        var b = provider.Start(PairingRole.Responder, otherCode ?? Code, nonce);
        if (confirm)
        {
            var ca = a.ReceivePeerMessage(b.InitialMessage); Check(b.ReceivePeerMessage(a.InitialMessage).Length == 0);
            var cb = b.ConfirmPeer(ca); Check(cb.Length == 32); Check(a.ConfirmPeer(cb).Length == 0);
        }
        return (a, b);
    }
    public static void Run(string path, string? faultPath = null)
    {
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        using var provider = new WindowsSpake2Provider(path, digest);
        var tests = new (string Name, Action Run)[] {
            ("Confirmed reciprocal identity binding and immutable credential copies", () => {
                var (a,b)=Pair(provider); using(a) using(b) {
                    var ida=Guid.NewGuid(); var idb=Guid.NewGuid(); var keya=Credential(); var keyb=Credential();
                    var ba=a.CreateIdentityBinding(ida,keya); var bb=b.CreateIdentityBinding(idb,keyb);
                    var peer=b.VerifyIdentityBinding(ba); Check(peer.HostId==ida && peer.PublicCredential.SequenceEqual(keya));
                    peer.PublicCredential[0]=0; Check(peer.PublicCredential.SequenceEqual(keya));
                    Check(a.VerifyIdentityBinding(bb).HostId==idb);
                    Check(a.CreateIdentityBinding(ida,keya).SequenceEqual(ba)); Check(b.VerifyIdentityBinding(ba).HostId==ida);
                    Reject(()=>a.CreateIdentityBinding(Guid.NewGuid(),keya)); Check(a.State==PairingExchangeState.Failed);
                }
            }),
            ("No pre-confirmation binding or key-export API", () => {
                var(a,b)=Pair(provider,false); using(a) using(b) {
                    Reject(()=>a.CreateIdentityBinding(Guid.NewGuid(),Credential()));
                    Check(a.State==PairingExchangeState.Failed);
                    Check(!typeof(IPairingKeyExchange).GetMembers().Any(m=>m.Name.Contains("SessionKey")));
                }
            }),
            ("Failure is terminal; correct retry cannot revive exchange", () => {
                var(a,b)=Pair(provider,false); using(a) using(b) {
                    var ca=a.ReceivePeerMessage(b.InitialMessage); Check(b.ReceivePeerMessage(a.InitialMessage).Length==0);
                    var cb=b.ConfirmPeer(ca);
                    Reject(()=>a.ConfirmPeer(new byte[32])); Reject(()=>a.ConfirmPeer(cb));
                    Reject(()=>a.ReceivePeerMessage(b.InitialMessage)); Check(a.State==PairingExchangeState.Failed);
                    Check(b.State==PairingExchangeState.Confirmed);
                }
            }),
            ("Disposed sessions reject empty confirmation and repeated cleanup", () => {
                var(a,b)=Pair(provider,false); using(b) {
                    a.ReceivePeerMessage(b.InitialMessage); a.Dispose(); a.Dispose();
                    Reject(()=>a.ConfirmPeer([])); Reject(()=>a.CreateIdentityBinding(Guid.NewGuid(),Credential()));
                    Check(a.State==PairingExchangeState.Disposed);
                }
            }),
            ("Malformed confirmations terminate pending sessions", () => {
                foreach(var length in new[]{0,1,31,33,1024}) {
                    var(a,b)=Pair(provider,false); using(a) using(b) {
                        a.ReceivePeerMessage(b.InitialMessage); Reject(()=>a.ConfirmPeer(new byte[length]));
                        Check(a.State==PairingExchangeState.Failed);
                    }
                }
            }),
            ("Malformed points rejected in both roles", () => {
                foreach(var role in new[]{PairingRole.Initiator,PairingRole.Responder})
                foreach(var point in new[]{Array.Empty<byte>(),new byte[33],new byte[65],Enumerable.Repeat((byte)255,65).ToArray(),new byte[66]}) {
                    using var session=provider.Start(role,Code,RandomNumberGenerator.GetBytes(32));
                    Reject(()=>session.ReceivePeerMessage(point)); Check(session.State==PairingExchangeState.Failed);
                }
            }),
            ("Wrong password cannot confirm or bind", () => {
                var(a,b)=Pair(provider,false,Encoding.ASCII.GetBytes("1234567891")); using(a) using(b) {
                    var ca=a.ReceivePeerMessage(b.InitialMessage); Check(b.ReceivePeerMessage(a.InitialMessage).Length==0);
                    Reject(()=>b.ConfirmPeer(ca)); Reject(()=>a.ConfirmPeer(new byte[32]));
                    Reject(()=>a.CreateIdentityBinding(Guid.NewGuid(),Credential()));
                }
            }),
            ("Changed nonce and same-role reflection cannot confirm", () => {
                using var a=provider.Start(PairingRole.Initiator,Code,RandomNumberGenerator.GetBytes(32));
                using var b=provider.Start(PairingRole.Responder,Code,RandomNumberGenerator.GetBytes(32));
                var ca=a.ReceivePeerMessage(b.InitialMessage); Check(b.ReceivePeerMessage(a.InitialMessage).Length==0);
                Reject(()=>b.ConfirmPeer(ca)); Reject(()=>a.ConfirmPeer(new byte[32]));
                var(c,d)=Pair(provider,false); using(c) using(d) {
                    var cc=c.ReceivePeerMessage(d.InitialMessage); Reject(()=>c.ConfirmPeer(cc));
                }
            }),
            ("HostId, credential and MAC substitution fail before identity result", () => {
                foreach(var offset in new[]{0,20,60,142}) {
                    var(a,b)=Pair(provider); using(a) using(b) {
                        var binding=a.CreateIdentityBinding(Guid.NewGuid(),Credential());
                        b.CreateIdentityBinding(Guid.NewGuid(),Credential());
                        binding[Math.Min(offset,binding.Length-1)]^=1;
                        Reject(()=>b.VerifyIdentityBinding(binding)); Check(b.State==PairingExchangeState.Failed);
                    }
                }
            }),
            ("Binding reflection, replay and self identity rejected", () => {
                var(a,b)=Pair(provider); using(a) using(b) {
                    var id=Guid.NewGuid(); var binding=a.CreateIdentityBinding(id,Credential());
                    Reject(()=>a.VerifyIdentityBinding(binding));
                    var(c,d)=Pair(provider); using(c) using(d) {
                        d.CreateIdentityBinding(Guid.NewGuid(),Credential()); Reject(()=>d.VerifyIdentityBinding(binding));
                    }
                    b.CreateIdentityBinding(id,Credential()); Reject(()=>b.VerifyIdentityBinding(binding));
                }
            }),
            ("Peer identity cannot be returned before local identity is fixed", () => {
                var(a,b)=Pair(provider); using(a) using(b) {
                    var binding=a.CreateIdentityBinding(Guid.NewGuid(),Credential());
                    Reject(()=>b.VerifyIdentityBinding(binding)); Check(b.State==PairingExchangeState.Failed);
                }
            }),
            ("Malformed credentials, unknown roles and code profile rejected", () => {
                var(a,b)=Pair(provider); using(a) using(b) { Reject(()=>a.CreateIdentityBinding(Guid.NewGuid(),[1,2,3])); }
                try { using var _=provider.Start((PairingRole)99,Code,new byte[32]); throw new Exception("Invalid role accepted"); } catch(ArgumentException) { }
                try { using var _=provider.Start(PairingRole.Initiator,Encoding.ASCII.GetBytes("abcdefghij"),new byte[32]); throw new Exception("Invalid code accepted"); } catch(ArgumentException) { }
            }),
            ("Cancellation terminates a live exchange", () => {
                var(a,b)=Pair(provider,false); using(a) using(b) {
                    using var cancel=new CancellationTokenSource(); cancel.Cancel();
                    try { a.ReceivePeerMessage(b.InitialMessage,cancel.Token); throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) { }
                    Check(a.State==PairingExchangeState.Failed); Reject(()=>a.ConfirmPeer([]));
                }
            }),
            ("Concurrent operation and disposal never revive or use freed handle", () => {
                for(var i=0;i<8;i++) {
                    var(a,b)=Pair(provider); using(a) using(b) {
                        var id=Guid.NewGuid(); var key=Credential();
                        Parallel.Invoke(()=> { try { a.CreateIdentityBinding(id,key); } catch(ObjectDisposedException) { } },a.Dispose);
                        Check(a.State==PairingExchangeState.Disposed); Reject(()=>a.ConfirmPeer([]));
                    }
                }
            }),
            ("Resident session bound rejects new work and releases capacity", () => {
                var active=new List<IPairingKeyExchange>();
                try {
                    for(var i=0;i<128;i++) active.Add(provider.Start(PairingRole.Initiator,Code,RandomNumberGenerator.GetBytes(32)));
                    Reject(()=>provider.Start(PairingRole.Initiator,Code,RandomNumberGenerator.GetBytes(32)));
                    active[0].Dispose(); using var next=provider.Start(PairingRole.Initiator,Code,RandomNumberGenerator.GetBytes(32));
                    Check(next.State==PairingExchangeState.Created);
                } finally { foreach(var session in active) session.Dispose(); }
            }),
            ("Provider closes live sessions before unloading and failed constructor finalization is safe", () => {
                var owner=new WindowsSpake2Provider(path,digest); var(a,b)=Pair(owner,false);
                owner.Dispose(); owner.Dispose(); Reject(()=>a.ReceivePeerMessage(new byte[65])); a.Dispose(); b.Dispose();
                Reject(()=> { using var _=new WindowsSpake2Provider(path,new string('0',64)); });
                if (faultPath is not null) {
                    var faultDigest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(faultPath)));
                    Reject(()=> { using var _=new WindowsSpake2Provider(faultPath,faultDigest); });
                }
                GC.Collect(); GC.WaitForPendingFinalizers();
            })
        };
        foreach(var test in tests) { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
        Console.WriteLine($"{tests.Length}/{tests.Length} actual managed/native pairing wrapper tests passed.");
    }
}
