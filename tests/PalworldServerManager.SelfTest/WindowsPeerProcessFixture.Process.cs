using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using Grpc.Core;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static partial class WindowsPeerProcessFixture
{
    // Fixed, bounded control protocol between the harness and its exact owned child.
    // A new instance nonce plus the retained Process handle distinguishes process lifetimes,
    // even if Windows happens to reuse a numerical PID. Reports are public. Command 3
    // receives ten code bytes on private stdin; command 4 returns ten on private stdout,
    // followed by a public cleanup acknowledgement. Neither code enters JSON/argv/config/logs.
    internal sealed record Report(string Kind, int Pid, Guid Instance, Guid Host, string Pin, string Key,
        Uri Address, string? CurrentPeerPin, Guid? PendingRotation, string? PeerState, DateTimeOffset? BindingExpiry, string? VerifiedPeerPin = null, Uri? PairingAddress = null, Guid? Invitation = null,
        RecoveryProcessState? Recovery = null);
    private static async Task<string> Line(Stream reader, CancellationToken ct)
    {
        // Never use StreamReader here: read-ahead could decode the next private code
        // response into its text buffer. Public JSON ends at LF; binary bytes follow it.
        var bytes = new byte[4096]; var one = new byte[1]; var count = 0;
        while (true)
        {
            if (await reader.ReadAsync(one, ct) == 0) throw new EndOfStreamException("Fixture report truncated or ended.");
            if (one[0] == (byte)'\n') return new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimEnd('\r');
            if (count == bytes.Length) throw new InvalidDataException("Fixture report exceeds bound.");
            bytes[count++] = one[0];
        }
    }
    internal sealed record Invitation(Report Metadata, RedactedSecret Code) : IDisposable
    { public void Dispose() => Code.Dispose(); }
    internal sealed class Child : IDisposable
    {
        private readonly Config config;
        private readonly Process process;
        private readonly Task stderr;
        private Guid? instance;
        internal Child(Config config)
        {
            this.config = config; var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing apphost.");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            info.ArgumentList.Add("--peer-process-host"); info.ArgumentList.Add(Path.Combine(config.Root, ConfigName));
            process = Process.Start(info) ?? throw new InvalidOperationException("Fixture process did not start.");
            stderr = DrainErrors();
        }
        private async Task DrainErrors()
        {
            // Drain continuously so diagnostics cannot deadlock redirected pipes. Do not retain
            // or forward arbitrary child output; any stderr is a qualification failure.
            var buffer = new char[256]; long count = 0; int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) != 0) count = Math.Min(4097, count + read);
            Check(count == 0, "Child reported an error.");
        }
        internal async Task<Report> Read(CancellationToken ct)
        {
            var result = JsonSerializer.Deserialize<Report>(await Line(process.StandardOutput.BaseStream, ct)) ?? throw new InvalidDataException("Missing report.");
            Check(!process.HasExited && result.Pid == process.Id && result.Host == config.Host && result.Instance != Guid.Empty &&
                (instance is null || instance == result.Instance), "Report does not match owned child lifetime.");
            instance ??= result.Instance; RequireLoopback(result.Address);
            RequireLoopback(result.PairingAddress ?? throw new InvalidDataException("Missing pairing address.")); return result;
        }
        internal async Task<Report> Command(string action, Uri address, CancellationToken ct)
        {
            var tag = action switch { "activate" => (byte)1, "receipt" => (byte)2, "refuse-activation" => (byte)5, "recovery-state" => (byte)6, _ => throw new ArgumentException("Unknown control action.") };
            await Send(tag, address, ct); return await Read(ct);
        }
        private async Task Send(byte tag, Uri address, CancellationToken ct)
        {
            RequireLoopback(address); var bytes = Encoding.UTF8.GetBytes(address.AbsoluteUri);
            Check(bytes.Length is > 0 and <= 512, "Oversized control address.");
            var header = new byte[3]; header[0] = tag; BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), (ushort)bytes.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, ct);
            await process.StandardInput.BaseStream.WriteAsync(bytes, ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
        }
        internal async Task<Report> Pair(Uri address, RedactedSecret code, CancellationToken ct)
        {
            var bytes = code.CopyBytes();
            try
            {
                Check(bytes.Length == 10 && bytes.All(b => b is >= (byte)'0' and <= (byte)'9'), "Invalid fixture code shape.");
                await Send(3, address, ct); await process.StandardInput.BaseStream.WriteAsync(bytes, ct);
                await process.StandardInput.BaseStream.FlushAsync(ct); return await Read(ct);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        internal async Task<Invitation> Invite(Uri ownAddress, CancellationToken ct)
        {
            await Send(4, ownAddress, ct); var report = await Read(ct);
            Check(report.Kind == "invitation" && report.Invitation is { } id && id != Guid.Empty, "Invalid invitation metadata.");
            var bytes = new byte[10];
            try
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, ct);
                Check(bytes.All(b => b is >= (byte)'0' and <= (byte)'9'), "Invalid private invitation code shape.");
                var ready = await Read(ct);
                Check(ready.Kind == "invitation-ready" && ready.Invitation == report.Invitation && ready.PairingAddress == report.PairingAddress,
                    "Invitation code cleanup acknowledgement changed.");
                return new(report, new RedactedSecret(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        internal async Task Kill(bool requireRunning = false)
        {
            if (requireRunning)
            {
                Check(!process.HasExited, "Crash target exited before explicit termination.");
                process.Kill(entireProcessTree: true);
            }
            else if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.HasExited, "Termination did not finish."); await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        }
        internal async Task Stop(CancellationToken ct)
        {
            // EOF requests graceful stop. Unlike the crash path, require successful process exit.
            process.StandardInput.BaseStream.Close(); await process.WaitForExitAsync(ct);
            Check(process.ExitCode == 0, "Graceful receiver exit failed."); await stderr.WaitAsync(ct);
        }
        public void Dispose() { Check(process.HasExited, "Cannot dispose a running child."); process.Dispose(); }
    }
    private static void RequireLoopback(Uri address) => Check(address.IsAbsoluteUri && address.Scheme == "https" &&
        address.Host == IPAddress.Loopback.ToString() && address.Port is > 0 and <= 65535 && address.AbsolutePath == "/" &&
        address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0, "Non-fixture control address.");
    internal static async Task<int> RunChild(string path)
    {
        // This is a SelfTest-only entry point, never a product Host launch/configuration route.
        try
        {
            Check(OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path) && Path.GetFileName(path) == ConfigName &&
                new FileInfo(path).Length is > 0 and <= 8192, "Invalid child fixture config.");
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing fixture config.");
            RequirePath(config); Check(Path.GetFullPath(path) == Path.Combine(config.Root, ConfigName), "Config/root mismatch.");
            using var identity = WindowsIdentity.GetCurrent(); Check(identity.User?.Value == config.Sid, "Child service identity changed.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55)); var ct = timeout.Token;
            // Console input can block synchronously. The independent watchdog also bounds an
            // orphaned child if the parent service itself terminates before closing control.
            using var watchdog = ct.Register(() => Environment.Exit(2));
            using var lease = Lease(config); var host = new FixtureHost(config); host.RequireFixture();
            Check(config.RecoveryFault is >=0 and <=6,"Unknown recovery fixture mode.");
            if(config.RecoveryFault!=0)return await RunRecoveryChild(host,ct);
            Check((config.NativePath is null) == (config.NativeHash is null), "Incomplete native fixture selection.");
            // Native provider lifetime encloses every generation/attempt. Null deliberately
            // supplies the refusing provider, including the post-PeerBound recovery process.
            using var provider = config.NativePath is null ? null : new WindowsSpake2Provider(config.NativePath, config.NativeHash!);
            Check(!(config.PauseBeforePeerBound && config.PauseResponderBeforePeerBound), "Ambiguous barrier role.");
            var barrier = config.PauseBeforePeerBound || config.PauseResponderBeforePeerBound
                ? new VerifiedPairingBarrier(provider ?? throw new InvalidDataException("A native barrier requires a provider."),
                    config.PauseResponderBeforePeerBound ? PairingRole.Responder : PairingRole.Initiator) : null;
            await using var owner = host.Create((IPairingKeyExchangeFactory?)barrier ?? provider); await owner.StartAsync(ct); var instance = Guid.NewGuid();
            async Task Send(string kind, string? verifiedPin = null, Guid? invitation = null)
            {
                var credential = await host.Credential(ct); var peer = host.Peers.Read(config.Peer);
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Report(kind, Environment.ProcessId, instance, config.Host,
                    credential.Pin, credential.Key, owner.Endpoints!.Value.Peer, peer?.CurrentFingerprint, peer?.PendingRotationId,
                    peer?.State, peer?.ExpiresUtc, verifiedPin, owner.Endpoints!.Value.Pairing, invitation)).AsMemory(), ct);
                await Console.Out.FlushAsync(ct);
            }
            barrier?.Arm(async (verified, token) =>
            {
                token.ThrowIfCancellationRequested();
                Check(verified.HostId == config.Peer, "Native barrier verified the wrong Host.");
                await Send("verified-before-store", Convert.ToHexString(SHA256.HashData(verified.PublicCredential)));
            });
            await Send("ready");
            using var input = Console.OpenStandardInput();
            while (true)
            {
                var tag = new byte[1]; if (await input.ReadAsync(tag, ct) == 0) break;
                Check(tag[0] is 1 or 2 or 3 or 4 or 5, "Unknown control action.");
                var size = new byte[2]; await input.ReadExactlyAsync(size, ct);
                var length = BinaryPrimitives.ReadUInt16BigEndian(size); Check(length is > 0 and <= 512, "Invalid control size.");
                var addressBytes = new byte[length]; await input.ReadExactlyAsync(addressBytes, ct);
                var address = new Uri(new UTF8Encoding(false, true).GetString(addressBytes)); RequireLoopback(address);
                switch (tag[0])
                {
                    case 1:
                        await owner.ActivateAsync(config.Peer, address, ct); host.RequireNoGrants(); await Send("activated"); break;
                    case 2:
                        Check(await owner.ConfirmRotationAsync(config.Peer, address, ct) == PeerRotationReceiptExchange.Confirmed,
                            "Receipt RPC did not confirm."); host.RequireNoGrants(); await Send("confirmed"); break;
                    case 4:
                        Check(provider is not null && address == owner.Endpoints!.Value.Peer, "Invitation requires the native fixture's own endpoint.");
                        Guid invitationId;
                        using (var invitation = await owner.CreateInvitationAsync(ct))
                        {
                            invitationId = invitation.Id; var privateCode = invitation.Code.CopyBytes();
                            try
                            {
                                Check(privateCode.Length == 10 && privateCode.All(b => b is >= (byte)'0' and <= (byte)'9'), "Invalid generated code shape.");
                                await Send("invitation", invitation: invitationId);
                                using var output = Console.OpenStandardOutput();
                                await output.WriteAsync(privateCode, ct); await output.FlushAsync(ct);
                            }
                            finally { CryptographicOperations.ZeroMemory(privateCode); }
                        }
                        // The coordinator still owns its actual live invitation. Only the
                        // transient handoff copies are gone before the parent may use its code.
                        await Send("invitation-ready", invitation: invitationId); break;
                    case 5:
                        Check(host.Peers.Read(config.Peer) is null, "Refusal probe requires an unbound peer.");
                        try { await owner.ActivateAsync(config.Peer, address, ct); throw new Exception("Unbound activation succeeded."); }
                        catch (RpcException error) when (error.StatusCode is StatusCode.Internal or StatusCode.Unavailable &&
                            error.Status.DebugException is HttpRequestException { InnerException: AuthenticationException }) { }
                        Check(host.Peers.Read(config.Peer) is null, "Refused activation created trust.");
                        host.RequireNoGrants(0); await Send("activation-refused"); break;
                    case 3:
                        Check(provider is not null, "Pairing is disabled in this recovery process.");
                        var bytes = new byte[10];
                        try
                        {
                            await input.ReadExactlyAsync(bytes, ct);
                            Check(bytes.All(b => b is >= (byte)'0' and <= (byte)'9'), "Invalid code shape.");
                            using var code = new RedactedSecret(bytes);
                            var result = await owner.PairAsync(address, code, ct);
                            Check(result.Local.PeerHostId == config.Peer && result.Local.Disposition == PeerBindingDisposition.PeerBoundCreated &&
                                result.Remote == PalworldServerManager.Contracts.Wire.PeerPairingResult.PeerBound, "Expected independently committed PeerBound.");
                            host.RequireNoGrants(0);
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                        await Send("paired"); // Secret copies are gone before the parent may terminate us.
                        break;
                }
            }
            await owner.StopAsync(); return 0;
        }
        catch (Exception error)
        {
            // Fixed diagnostic category only: never echo config/control or credential material.
            Console.Error.WriteLine("Receipt fixture failed: " + error.GetType().Name); return 1;
        }
    }
}
