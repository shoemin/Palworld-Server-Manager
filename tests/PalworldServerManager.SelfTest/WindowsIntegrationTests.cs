using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// PRIVILEGED Windows integration harness for #41.
///
/// These create REAL machine-global resources (a Windows service, a local group, temporary local
/// users), so they are deliberately NOT part of the ordinary self-test suite: `./scripts/build.ps1`
/// must stay safe for an ordinary developer to run. They are invoked explicitly via
/// `scripts/windows-integration.ps1` and wired into CI.
///
/// Every resource is uniquely named per run, created inside try, and removed inside finally.
/// Cleanup failure is reported as a FAILURE rather than swallowed - a leaked service or local user
/// on a shared machine is a real problem, not a cosmetic one.
/// </summary>
public static class WindowsIntegrationTests
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static string ServiceName => $"PSMTestHost{RunId}";
    private static string GroupName => $"PSMTestGrp{RunId}";

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new Exception($"Expected condition to hold: {what}");
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string RunPowerShell(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
        {
            throw new Exception($"PowerShell failed ({process.ExitCode}): {script}\n{stderr}");
        }

        return stdout.Trim();
    }

    /// <summary>
    /// Runs the whole privileged suite, always cleaning up. Returns a human-readable transcript.
    /// </summary>
    public static async Task<string> RunAllAsync()
    {
        if (!IsElevated())
        {
            throw new InvalidOperationException(
                "The Windows integration harness requires an elevated process. It is intentionally not skipped silently.");
        }

        var log = new List<string>();
        var lifecycle = new WindowsHostServiceLifecycle(ServiceName);
        var hostExe = ResolveHostExecutable();
        var cleanupErrors = new List<string>();

        var userA = $"psmtA{RunId[..6]}";
        var userB = $"psmtB{RunId[..6]}";
        var password = $"Pw!{Guid.NewGuid():N}Aa1";

        try
        {
            // 2. uniquely named temporary activation group
            RunPowerShell($"New-LocalGroup -Name '{GroupName}' -Description 'PSM #41 integration test' | Out-Null");
            log.Add($"[2] created temporary activation group {GroupName}");

            // 1 + 3. temporary service, product provisioning, dedicated virtual account
            await lifecycle.InstallAsync(new HostServiceInstallOptions(
                ExecutablePath: hostExe,
                Arguments: null,
                StartMode: HostServiceStartMode.Manual,
                ActivationGroupName: GroupName));
            log.Add($"[1,3] installed service {ServiceName} under {lifecycle.ServiceAccountName}");

            // 5. default start type is Manual/Demand (boot-start OFF)
            var status = await lifecycle.QueryStatusAsync();
            Equal(HostServiceStartMode.Manual, status.StartMode, "[5] default start type is Manual/Demand");
            log.Add("[5] default start type Manual verified");

            // 7. the service is configured to run under the intended NT SERVICE virtual account
            var account = RunPowerShell(
                $"(Get-CimInstance Win32_Service -Filter \"Name='{ServiceName}'\").StartName");
            Equal($@"NT SERVICE\{ServiceName}", account, "[7] service runs under the dedicated virtual account");
            log.Add($"[7] service account verified: {account}");

            // 4. exact service DACL read-back
            VerifyServiceDacl(log);

            // 6. boot-start toggle changes ONLY the start type
            await lifecycle.SetBootStartEnabledAsync(true);
            Equal(HostServiceStartMode.Automatic, (await lifecycle.QueryStatusAsync()).StartMode, "[6] boot-start on -> Automatic");
            await lifecycle.SetBootStartEnabledAsync(false);
            Equal(HostServiceStartMode.Manual, (await lifecycle.QueryStatusAsync()).StartMode, "[6] boot-start off -> Manual");
            log.Add("[6] boot-start toggle verified");

            // 12 + 13. authorized vs unauthorized NON-ADMIN identities
            RunPowerShell($"$p = ConvertTo-SecureString '{password}' -AsPlainText -Force; New-LocalUser -Name '{userA}' -Password $p -AccountNeverExpires:$true | Out-Null");
            RunPowerShell($"$p = ConvertTo-SecureString '{password}' -AsPlainText -Force; New-LocalUser -Name '{userB}' -Password $p -AccountNeverExpires:$true | Out-Null");
            RunPowerShell($"Add-LocalGroupMember -Group '{GroupName}' -Member '{userA}'");
            log.Add($"[12,13] created non-admin users {userA} (in group) and {userB} (not in group)");

            // Both users are confirmed NON-admin.
            foreach (var user in new[] { userA, userB })
            {
                var isAdmin = RunPowerShell(
                    $"[bool](Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '*\\{user}' }})");
                Equal("False", isAdmin, $"[12,13] {user} must not be an Administrator");
            }
            log.Add("[12,13] both test identities confirmed non-admin");

            // 8-11. exclusivity lock behavior around the real service lifetime
            await VerifyExclusivityLockAsync(lifecycle, log);

            log.Add("NOTE: items 12/13 start-attempt and 14 cross-user DPAPI require running a helper "
                  + "process as each temporary user (CreateProcessWithLogonW); the identities and group "
                  + "membership above are provisioned and verified here.");
        }
        finally
        {
            // 15. always remove every temporary resource; cleanup failure FAILS the run.
            TryCleanup(() => lifecycle.StopAsync().GetAwaiter().GetResult(), "stop service", cleanupErrors);
            TryCleanup(() => lifecycle.UninstallAsync().GetAwaiter().GetResult(), "uninstall service", cleanupErrors);
            TryCleanup(() => RunPowerShell($"Remove-LocalGroup -Name '{GroupName}' -ErrorAction SilentlyContinue"), "remove group", cleanupErrors);
            TryCleanup(() => RunPowerShell($"Remove-LocalUser -Name '{userA}' -ErrorAction SilentlyContinue"), "remove userA", cleanupErrors);
            TryCleanup(() => RunPowerShell($"Remove-LocalUser -Name '{userB}' -ErrorAction SilentlyContinue"), "remove userB", cleanupErrors);
        }

        if (cleanupErrors.Count > 0)
        {
            throw new Exception("[15] cleanup FAILED - temporary resources may remain: " + string.Join("; ", cleanupErrors));
        }

        log.Add("[15] all temporary services/groups/users removed");
        return string.Join(Environment.NewLine, log);
    }

    private static void TryCleanup(Action action, string what, List<string> errors)
    {
        try { action(); }
        catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
    }

    /// <summary>Item 4: read the live DACL back and assert the exact delegated rights.</summary>
    private static void VerifyServiceDacl(List<string> log)
    {
        var sddl = RunPowerShell($"(sc.exe sdshow {ServiceName}) -join ''").Trim();
        True(sddl.StartsWith("D:", StringComparison.Ordinal), "[4] a DACL SDDL was returned");

        var groupSid = ((NTAccount)new NTAccount(Environment.MachineName, GroupName))
            .Translate(typeof(SecurityIdentifier)).Value;

        var descriptor = new RawSecurityDescriptor(sddl);
        var mask = ServiceDaclBuilder.FindActivationGroupMask(descriptor, new SecurityIdentifier(groupSid))
            ?? throw new Exception("[4] the activation group has no ACE on the live service");

        Equal(ServiceDaclBuilder.ActivationGroupAccessMask, mask,
            "[4] live DACL grants the activation group exactly SERVICE_START|SERVICE_QUERY_STATUS");

        foreach (var forbidden in ServiceDaclBuilder.ForbiddenForActivationGroup)
        {
            True((mask & forbidden) == 0, $"[4] activation group must not hold right 0x{forbidden:X} on the live service");
        }

        log.Add($"[4] live service DACL verified: activation-group mask 0x{mask:X}");
    }

    /// <summary>Items 8-11: the #40 exclusivity lock across a real service lifetime.</summary>
    private static async Task VerifyExclusivityLockAsync(WindowsHostServiceLifecycle lifecycle, List<string> log)
    {
        await lifecycle.StartAsync();
        Equal(HostServiceState.Running, (await lifecycle.QueryStatusAsync()).State, "[8] service reached Running");

        // 8 + 9: while the Host workload runs it holds the machine-wide lock, so a second
        // contender (a would-be second Host, or Host.Cli) is denied.
        using (var contender = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(2)))
        {
            True(contender is null, "[8,9] a second exclusivity-lock contender is denied while the Host runs");
        }

        log.Add("[8,9] lock held by the running service; second contender denied");

        // 10: a normal stop releases it.
        await lifecycle.StopAsync();
        using (var afterStop = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5)))
        {
            True(afterStop is not null, "[10] normal stop releases the exclusivity lock");
        }

        log.Add("[10] normal stop released the lock");

        // 11: abnormal termination must leave it reacquirable (proven in-process by #40's own
        // cross-process abandonment test; re-asserted here against the real service identity).
        await lifecycle.StartAsync();
        RunPowerShell($"Stop-Process -Name '{Path.GetFileNameWithoutExtension(ResolveHostExecutable())}' -Force -ErrorAction SilentlyContinue");
        await Task.Delay(2000);
        using (var afterKill = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(10)))
        {
            True(afterKill is not null, "[11] the lock is reacquirable after abnormal termination");
        }

        log.Add("[11] lock reacquirable after abnormal termination");
    }

    private static string ResolveHostExecutable()
    {
        // The built Host apphost, located relative to the test output.
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..",
            "src", "PalworldServerManager.Host", "bin", "Release", "net8.0-windows", "PalworldServerManager.Host.exe"));

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"The Host executable was not found at '{candidate}'. Build the solution in Release before running the integration harness.");
        }

        return candidate;
    }
}
