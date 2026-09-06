using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Host.Cli;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static partial class WindowsIntegration
{
    private sealed record ProductRequest(string[] Arguments, string? Input, int ExpectedExit, string? ExpectedError, string ForbiddenPath);
    private sealed record ProductResult(string Output, Guid? Principal, string? PublicKey);

    // Called inside an actual non-admin logon. Only protected per-user files carry command
    // results; the shared RunUser result contains PASS or a fixed failure, never bearer output.
    private static async Task ProductClientProbe(string clientExecutable, string requestPath)
    {
        var request = JsonSerializer.Deserialize<ProductRequest>(await File.ReadAllTextAsync(requestPath))!;
        try { using var denied = File.OpenRead(request.ForbiddenPath); throw new Exception("Other user's delivery file was readable."); }
        catch (UnauthorizedAccessException) { }
        try { using var denied = File.OpenRead(Path.Combine(OfflineHostLocation.Product().HostRoot, "host.db")); throw new Exception("Ordinary user read authoritative Host state."); }
        catch (UnauthorizedAccessException) { }
        var start = new ProcessStartInfo(clientExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            if (request.Input is not null) await process.StandardInput.WriteLineAsync(request.Input);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
        }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw new Exception("Shipped client did not finish within its deadline."); }
        var reply = await output; var diagnostic = (await error).Trim();
        Check(process.ExitCode == request.ExpectedExit, "Shipped client exit did not match expected result for " + request.Arguments[0] + ".");
        Check(diagnostic == (request.ExpectedError ?? ""), "Shipped client error classification did not match expected result.");
        if (request.ExpectedExit != 0) Check(reply.Length == 0, "Failed client emitted a success or bearer result.");
        if (request.Input is not null) Check(!reply.Contains(request.Input) && !diagnostic.Contains(request.Input), "Completion leaked enrollment input.");
        var current = await new WindowsLocalPrincipalCredentialStore(new WindowsLocalPrincipalCryptography()).LoadAsync();
        try
        {
            var result = new ProductResult(reply, current?.LocalPrincipalId, current is null ? null : Convert.ToBase64String(current.KeyPair.PublicKey));
            await File.WriteAllTextAsync(requestPath + ".result", JsonSerializer.Serialize(result));
        }
        finally { if (current is not null) CryptographicOperations.ZeroMemory(current.KeyPair.PrivateKey); }
    }

    private static async Task ProductionClientSuite(string binaries, string userA, string userB, string password,
        string sidA, string sidB, string shared, Guid hostId, Guid bootstrapTicket, WindowsHostPlatform platform,
        Func<int, string[], Task<string>> offline)
    {
        var location = OfflineHostLocation.Product();
        var root = Path.GetDirectoryName(binaries)!;
        var source = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "PalworldServerManager.Client.Cli", "bin", "Release", "net8.0-windows"));
        Check(File.Exists(Path.Combine(source, "PalworldServerManager.Client.Cli.exe")), "Shipped ordinary client build output is required; no test-client substitution.");
        var clientBinaries = Path.Combine(root, "Ordinary Client Binaries"); CopyDirectory(source, clientBinaries);
        var client = Path.Combine(clientBinaries, "PalworldServerManager.Client.Cli.exe");
        var helper = Path.Combine(binaries, "PalworldServerManager.SelfTest.exe");
        string Delivery(string name, string sid)
        {
            var path = Path.Combine(root, name); Check(!Directory.Exists(path), "Delivery fixture already exists.");
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null); acl.SetOwner(admin);
            foreach (var trustee in new[] { admin, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(sid) })
                acl.AddAccessRule(new FileSystemAccessRule(trustee, trustee == admin || trustee.IsWellKnown(WellKnownSidType.LocalSystemSid) ? FileSystemRights.FullControl : FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl); File.WriteAllText(Path.Combine(path, "recipient-only.txt"), "access probe"); return path;
        }
        var deliveryA = Delivery("Client A Delivery", sidA); var deliveryB = Delivery("Client B Delivery", sidB);
        Native.AddMember(location.ActivationGroup, userA); Native.AddMember(location.ActivationGroup, userB);
        ProductResult Invoke(bool a, string[] args, int expected = 0, string? input = null, string? error = null)
        {
            var path = Path.Combine(a ? deliveryA : deliveryB, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new ProductRequest(args, input, expected, error,
                Path.Combine(a ? deliveryB : deliveryA, "recipient-only.txt"))));
            try
            {
                RunUser(helper, a ? userA : userB, password, "product-client", client, path, location.HostRoot, shared);
                return JsonSerializer.Deserialize<ProductResult>(File.ReadAllText(path + ".result"))!;
            }
            finally { File.Delete(path + ".result"); File.Delete(path); }
        }
        const string refused = "Local request refused or failed: Unauthenticated.";
        const string unenrolled = "Local principal or ceremony authentication failed.";
        (Guid Id, bool Owner) Identity(ProductResult result)
        {
            using var json = JsonDocument.Parse(result.Output);
            var id = json.RootElement.GetProperty("localPrincipalId").GetGuid();
            Check(result.Principal == id && result.PublicKey is not null, "Shipped client result preceded durable per-user binding.");
            return (id, json.RootElement.GetProperty("isOwner").GetBoolean());
        }
        List<(Guid Id, string Sid, string? Key, bool Owner, string State)> Principals()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(location.HostRoot, "host.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State FROM LocalPrincipals;";
            using var rows = command.ExecuteReader(); var result = new List<(Guid, string, string?, bool, string)>();
            while (rows.Read()) result.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetBoolean(3), rows.GetString(4)));
            return result;
        }
        void Owner(Guid expected)
        { var rows = Principals(); Check(rows.Count(row => row.Owner && row.State == "Active") == 1 && rows.Single(row => row.Owner && row.State == "Active").Id == expected, "Actual ceremony violated persisted Owner identity/cardinality."); }
        void HandoffDeleted(Guid ticket, string sid)
        { Check(!Directory.EnumerateFiles(Path.Combine(location.HandoffRoot, sid)).Any(path => Path.GetFileName(path) == ticket.ToString("N") + ".bin"), "Confirmed shipped client left its protected handoff."); }
        async Task<Guid> Prepare(params string[] args)
        {
            await platform.StopAsync(); AssertLockAvailable(location.MutexName);
            using var json = JsonDocument.Parse(await offline(0, args));
            Check(json.RootElement.GetProperty("hostId").GetGuid() == hostId, "Offline recovery changed semantic HostId.");
            return json.RootElement.GetProperty("ticketId").GetGuid();
        }

        // SCM is stopped at entry. A group member activates the real executable but gains no identity.
        Invoke(false, ["identity"], 1, error: unenrolled);
        Check(Principals().Count == 0, "Transport eligibility created a principal.");
        Invoke(false, ["complete-handoff", "--ticket", bootstrapTicket.ToString("D")], 1, error: "Invalid local command or security data.");
        var ownerA = Invoke(true, ["complete-handoff", "--ticket", bootstrapTicket.ToString("D")]); var aIdentity = Identity(ownerA);
        Check(aIdentity.Owner, "Intended user did not become Owner."); Owner(aIdentity.Id); HandoffDeleted(bootstrapTicket, sidA);
        Check(Identity(Invoke(true, ["identity"])) == aIdentity, "Fresh ordinary-client process did not retain Owner.");
        Invoke(false, ["identity"], 1, error: unenrolled);
        await offline(1, ["rotate-owner"]); // live Host owns the lease
        Invoke(true, ["revoke", "--principal", aIdentity.Id.ToString("D")], 1, error: refused); Owner(aIdentity.Id);

        var deliveredCodes = new List<string>();
        ProductResult EnrollB()
        {
            var invitation = Invoke(true, ["invite", "--user-sid", sidB]);
            using var json = JsonDocument.Parse(invitation.Output);
            var ticket = json.RootElement.GetProperty("ticketId").GetGuid(); var code = json.RootElement.GetProperty("code").GetString()!;
            deliveredCodes.Add(code);
            return Invoke(false, ["complete-enrollment", "--ticket", ticket.ToString("D")], input: code);
        }
        var principalB = EnrollB(); var bIdentity = Identity(principalB);
        Check(!bIdentity.Owner && bIdentity.Id != aIdentity.Id && principalB.PublicKey != ownerA.PublicKey, "Two actual users did not receive distinct credentials/identities.");
        var rows = Principals();
        Check(rows.Single(row => row.Sid == sidA).Key == ownerA.PublicKey && rows.Single(row => row.Sid == sidB).Key == principalB.PublicKey, "Host did not persist exact client public verifiers.");
        Invoke(false, ["invite", "--user-sid", sidA], 1, error: refused);
        Invoke(false, ["revoke", "--principal", aIdentity.Id.ToString("D")], 1, error: refused); Owner(aIdentity.Id);
        Invoke(true, ["revoke", "--principal", bIdentity.Id.ToString("D")]);
        await platform.StopAsync();
        Invoke(false, ["identity"], 1, error: refused); // actual restart through group-eligible revoked client
        Check(Principals().Single(row => row.Id == bIdentity.Id) is { State: "Revoked", Key: null }, "Revocation did not survive restart.");
        var reenrolledB = EnrollB(); Check(Identity(reenrolledB) == bIdentity && reenrolledB.PublicKey != principalB.PublicKey, "Explicit re-enrollment did not preserve identity and refresh key.");
        Console.WriteLine("PASS integration: shipped ordinary CLI, two real user credentials, unregistered refusal, intended-user bootstrap, Owner-only enrollment/removal and durable re-enrollment");

        var rotation = await Prepare("rotate-owner");
        var rotatedA = Invoke(true, ["complete-handoff", "--ticket", rotation.ToString("D")]);
        Check(Identity(rotatedA) == aIdentity && rotatedA.PublicKey != ownerA.PublicKey, "Owner rotation failed to replace key while preserving identity.");
        Owner(aIdentity.Id); HandoffDeleted(rotation, sidA);
        var rehome = await Prepare("rehome-owner", "--owner-sid", sidB);
        var rehomedB = Invoke(false, ["complete-handoff", "--ticket", rehome.ToString("D")]);
        Check(Identity(rehomedB) == (bIdentity.Id, true) && rehomedB.PublicKey == reenrolledB.PublicKey, "Active-target re-home failed to preserve existing key/identity.");
        Owner(bIdentity.Id); HandoffDeleted(rehome, sidB);
        Invoke(true, ["identity"], 1, error: refused);
        var returnHome = await Prepare("rehome-owner", "--owner-sid", sidA);
        var returnedA = Invoke(true, ["complete-handoff", "--ticket", returnHome.ToString("D")]);
        Check(Identity(returnedA) == aIdentity && returnedA.PublicKey != rotatedA.PublicKey, "Revoked-target re-home failed to refresh key and preserve identity.");
        Owner(aIdentity.Id); HandoffDeleted(returnHome, sidA); Invoke(false, ["identity"], 1, error: refused);
        await platform.StopAsync(); Invoke(true, ["identity"]); Owner(aIdentity.Id);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(location.HostRoot, "host.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT EventKind,Summary FROM AuditEvents;";
            using var reader = command.ExecuteReader(); var kinds = new List<string>();
            while (reader.Read())
            {
                kinds.Add(reader.GetString(0)); var summary = reader.GetString(1);
                Check(!deliveredCodes.Any(code => summary.Contains(code)) && !summary.Contains("PRIVATE KEY"), "Audit leaked security material.");
            }
            foreach (var kind in new[] { "OwnerBootstrapPrepared", "OwnerBootstrapCompleted", "LocalPrincipalEnrollmentPrepared", "LocalPrincipalEnrollmentCompleted", "LocalPrincipalRevoked", "OwnerCredentialRotationPrepared", "OwnerCredentialRotationCompleted", "OwnerRehomePrepared", "OwnerRehomeCompleted" })
                Check(kinds.Contains(kind), "Actual security audit omitted " + kind + ".");
        }
        using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PalworldServerManager.Host.WindowsHostComposition.PipeName,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(5000); Native.Check(ProductionGetPipePid(pipe.SafePipeHandle, out var pid));
            await ProductionAssertNoTcp(pid);
        }
        await platform.StopAsync(); AssertLockAvailable(location.MutexName);
        Console.WriteLine("PASS integration: shipped offline/ordinary Owner rotation, active/revoked target re-home, persisted single Owner, old-Owner denial, protected artifact deletion and explicit recovery audits");
    }
}
