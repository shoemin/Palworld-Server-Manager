using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Host;

namespace PalworldServerManager.SelfTest;

internal static partial class WindowsPeerReceiptCrashQualification
{
    // Fixed, bounded public control protocol between the harness and its exact owned child.
    // A new instance nonce plus the retained Process handle distinguishes process lifetimes,
    // even if Windows happens to reuse a numerical PID. No code/key/private DTO is present.
    private sealed record CommandFrame(string Action, Uri Address);
    private sealed record Report(string Kind, int Pid, Guid Instance, Guid Host, string Pin, string Key,
        Uri Address, string? CurrentPeerPin, Guid? PendingRotation);
    private static async Task<string> Line(TextReader reader, CancellationToken ct)
    {
        var line = new StringBuilder(); var one = new char[1];
        while (line.Length <= 4096)
        {
            if (await reader.ReadAsync(one.AsMemory(), ct) == 0) throw new EndOfStreamException("Fixture control ended.");
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append(one[0]);
        }
        throw new InvalidDataException("Fixture control exceeds bound.");
    }
    private sealed class Child : IDisposable
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
            info.ArgumentList.Add("--receipt-process-host"); info.ArgumentList.Add(Path.Combine(config.Root, ConfigName));
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
            var result = JsonSerializer.Deserialize<Report>(await Line(process.StandardOutput, ct)) ?? throw new InvalidDataException("Missing report.");
            Check(!process.HasExited && result.Pid == process.Id && result.Host == config.Host && result.Instance != Guid.Empty &&
                (instance is null || instance == result.Instance), "Report does not match owned child lifetime.");
            instance ??= result.Instance; RequireLoopback(result.Address); return result;
        }
        internal async Task<Report> Command(string action, Uri address, CancellationToken ct)
        {
            RequireLoopback(address); await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new CommandFrame(action, address)).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct); return await Read(ct);
        }
        internal async Task Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.HasExited, "Termination did not finish."); await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        }
        internal async Task Stop(CancellationToken ct)
        {
            // EOF requests graceful stop. Unlike the crash path, require successful process exit.
            process.StandardInput.Close(); await process.WaitForExitAsync(ct);
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
            using var lease = Lease(config); var host = new Host(config); host.RequireFixture();
            await using var owner = host.Create(); await owner.StartAsync(ct); var instance = Guid.NewGuid();
            async Task Send(string kind)
            {
                var credential = await host.Credential(ct); var peer = host.Peers.Read(config.Peer)!;
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new Report(kind, Environment.ProcessId, instance, config.Host,
                    credential.Pin, credential.Key, owner.Endpoints!.Value.Peer, peer.CurrentFingerprint, peer.PendingRotationId)).AsMemory(), ct);
                await Console.Out.FlushAsync(ct);
            }
            await Send("ready");
            while (true)
            {
                string line;
                try { line = await Line(Console.In, ct); }
                catch (EndOfStreamException) { break; }
                var command = JsonSerializer.Deserialize<CommandFrame>(line) ?? throw new InvalidDataException("Missing fixture command.");
                RequireLoopback(command.Address);
                switch (command.Action)
                {
                    case "activate":
                        await owner.ActivateAsync(config.Peer, command.Address, ct); host.RequireNoGrants(); await Send("activated"); break;
                    case "receipt":
                        Check(await owner.ConfirmRotationAsync(config.Peer, command.Address, ct) == PeerRotationReceiptExchange.Confirmed,
                            "Receipt RPC did not confirm."); host.RequireNoGrants(); await Send("confirmed"); break;
                    default: throw new InvalidDataException("Unknown fixture command.");
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
