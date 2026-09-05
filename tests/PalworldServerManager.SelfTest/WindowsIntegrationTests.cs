using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
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
/// ISOLATION: the temporary service is launched via the production Host executable's TEST-ONLY
/// "--integration-service" mode with a unique service name, a unique temporary data root, and a
/// unique exclusivity-mutex name - it never touches the real %ProgramData% Host root and never
/// acquires the real product mutex. That isolation is itself asserted below, not just assumed.
///
/// Every resource is uniquely named per run, created inside try, and removed inside finally.
/// Cleanup failure is reported as a FAILURE rather than swallowed - a leaked service, group, user,
/// or temporary directory on a shared machine is a real problem, not a cosmetic one.
/// </summary>
public static class WindowsIntegrationTests
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static string ServiceName => $"PSMTestHost{RunId}";
    private static string GroupName => $"PSMTestGrp{RunId}";
    private static string MutexName => $@"Global\PSMTest.Exclusivity.{RunId}";

    /// <summary>
    /// Storage-lifecycle-only fake, explicitly NOT a production cryptographic implementation -
    /// mirrors <c>LocalPrincipalCredentialStoreTests</c>'s own fake, reused here so the real
    /// cross-user DPAPI helper processes (#41 item 7) exercise the same storage code path.
    /// </summary>
    internal sealed class HarnessFakeKeyPairGenerator : ILocalPrincipalKeyPairGenerator
    {
        public LocalPrincipalKeyMaterial Generate() => new(
            AlgorithmId: "test-fake-not-production",
            PrivateKeyBlob: Encoding.UTF8.GetBytes($"FAKE-PRIVATE-{Guid.NewGuid():N}"),
            PublicKeyBlob: Encoding.UTF8.GetBytes($"FAKE-PUBLIC-{Guid.NewGuid():N}"));
    }

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

    private const int PowerShellTimeoutSeconds = 60;

    /// <summary>
    /// Runs a PowerShell script with a SAFE, secret-free diagnostic surface (SEC-001).
    ///
    /// Diagnostics (timeout/non-zero-exit messages) report <paramref name="operationDescription"/>
    /// - never the raw script text - so callers cannot accidentally leak a secret merely by
    /// interpolating it into a script string. A secret that MUST reach the child process (e.g. a
    /// generated temporary-user password) is passed via <paramref name="environment"/> - a
    /// per-call child-process environment variable, never command-line text - and the script
    /// references it as <c>$env:&lt;name&gt;</c>. The variable exists only for that one child
    /// process's lifetime; nothing is written to disk and this process's own environment is never
    /// touched. As defense in depth, any environment value is also redacted from stdout/stderr
    /// before either can appear in a returned value or an exception message.
    /// </summary>
    private static string RunPowerShell(string script, string operationDescription, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = BuildPowerShellStartInfo(script, environment);
        using var process = Process.Start(startInfo)!;

        // Read both streams CONCURRENTLY with the bounded wait below - reading either stream to
        // completion first risks the classic redirected-pipe deadlock (the child blocks trying to
        // write to a full stderr buffer while we are blocked reading stdout, or vice versa).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(PowerShellTimeoutSeconds * 1000))
        {
            // FAIL-CLOSED: this is a machine-state-mutating integration harness (creating/
            // deleting services, users, groups). A cleanup failure here must be surfaced loudly,
            // never swallowed - PowerShell could still be mutating machine state.
            Exception? cleanupFailure = null;
            try
            {
                KillExactProcessTreeAndConfirmTerminated(process, $"PowerShell timeout cleanup for '{operationDescription}'");
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            if (cleanupFailure is not null)
            {
                throw new System.TimeoutException(
                    $"PowerShell operation '{operationDescription}' did not complete within {PowerShellTimeoutSeconds}s AND cleanup could " +
                    $"not be confirmed ({cleanupFailure.Message}).",
                    cleanupFailure);
            }

            throw new System.TimeoutException(BuildTimeoutMessage(operationDescription, TimeSpan.FromSeconds(PowerShellTimeoutSeconds)));
        }

        // The parameterless WaitForExit() after a successful timed wait ensures redirected-stream
        // EOF has actually been observed, per documented .NET guidance for combining a bounded
        // wait with stream redirection.
        process.WaitForExit();

        var stdout = SanitizeSecrets(stdoutTask.GetAwaiter().GetResult(), environment);
        var stderr = SanitizeSecrets(stderrTask.GetAwaiter().GetResult(), environment);

        if (process.ExitCode != 0)
        {
            throw new Exception(BuildNonZeroExitMessage(operationDescription, process.ExitCode, stderr));
        }

        return stdout.Trim();
    }

    /// <summary>Builds the child process configuration - factored out so it is independently testable.</summary>
    internal static ProcessStartInfo BuildPowerShellStartInfo(string script, IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        return startInfo;
    }

    /// <summary>Redacts every value of <paramref name="environment"/> (e.g. a passed-through secret) from <paramref name="text"/>.</summary>
    internal static string SanitizeSecrets(string text, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return text;
        }

        foreach (var value in environment.Values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                text = text.Replace(value, "***REDACTED***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    internal static string BuildTimeoutMessage(string operationDescription, TimeSpan timeout)
        => $"PowerShell operation '{operationDescription}' did not complete within {timeout.TotalSeconds:0}s and was terminated (confirmed).";

    internal static string BuildNonZeroExitMessage(string operationDescription, int exitCode, string sanitizedStderr)
        => $"PowerShell operation '{operationDescription}' failed ({exitCode}). {sanitizedStderr}";

    /// <summary>
    /// Kills ONLY this exact process (and its exact child tree) - never by name - and does not
    /// return until termination is actually confirmed. Throws rather than swallowing any failure:
    /// a Kill() failure that is not simply "it already exited", a WaitForExit timeout, or
    /// HasExited still reading false afterward all surface as an explicit cleanup failure.
    /// </summary>
    private static void KillExactProcessTreeAndConfirmTerminated(Process process, string context)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Exited between our check and the Kill call - not a cleanup failure.
            return;
        }

        if (!process.WaitForExit(5000))
        {
            throw new System.TimeoutException($"{context}: termination was not confirmed within 5s after Kill().");
        }

        if (!process.HasExited)
        {
            throw new InvalidOperationException($"{context}: HasExited still reads false after a confirmed WaitForExit.");
        }
    }

    /// <summary>Runs the TEST-ONLY helper mode of this same executable under a specific user's token.</summary>
    private static string RunAsUser(string userName, string password, string arguments, string what)
    {
        var selfTestExe = Environment.ProcessPath!;
        var (exitCode, output) = ProcessAsUser.Run(userName, password, selfTestExe, arguments, TimeSpan.FromSeconds(90));
        if (exitCode != 0)
        {
            throw new Exception($"{what}: helper process for '{userName}' exited with code {exitCode}. Output: {output}");
        }

        return output;
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

        var dataRoot = Path.Combine(Path.GetTempPath(), $"psm-integration-root-{RunId}");
        var dpapiDir = Path.Combine(Path.GetTempPath(), $"psm-integration-dpapi-{RunId}");

        // ISOLATION baseline: snapshot the REAL production Host data root before doing anything,
        // so the isolation assertion below actually proves something rather than trivially seeing
        // "does not exist" both times on a machine that never had one.
        var productionRoot = new WindowsHostDataRootProvider().GetMachineWideHostDataRoot();
        var productionRootExistedBefore = Directory.Exists(productionRoot);
        var productionRootWriteTimeBefore = productionRootExistedBefore ? Directory.GetLastWriteTimeUtc(productionRoot) : (DateTime?)null;

        try
        {
            // 1 + 3: install the temporary service, TEST-ONLY isolated data root/mutex, and
            // production-path activation-group provisioning (InstallAsync creates GroupName
            // itself - the harness never pre-creates it).
            var integrationArguments =
                $"--integration-service --service-name {ServiceName} --data-root \"{dataRoot}\" --mutex-name {MutexName}";

            await lifecycle.InstallAsync(new HostServiceInstallOptions(
                ExecutablePath: hostExe,
                Arguments: integrationArguments,
                StartMode: HostServiceStartMode.Manual,
                ActivationGroupName: GroupName));
            log.Add($"[1,3] installed service {ServiceName} under {lifecycle.ServiceAccountName}, isolated data root {dataRoot}");

            // 1 (activation-group provisioning): InstallAsync itself created GroupName - confirm.
            var groupExists = RunPowerShell(
                $"[bool](Get-LocalGroup -Name '{GroupName}' -ErrorAction SilentlyContinue)",
                "query activation group existence");
            Equal("True", groupExists, "[1] InstallAsync must itself provision the activation group");
            log.Add($"[1] production provisioning created the activation group {GroupName}");

            // 5. default start type is Manual/Demand (boot-start OFF)
            var status = await lifecycle.QueryStatusAsync();
            Equal(HostServiceStartMode.Manual, status.StartMode, "[5] default start type is Manual/Demand");
            log.Add("[5] default start type Manual verified");

            // 7. the service is configured to run under the intended NT SERVICE virtual account
            var account = RunPowerShell(
                $"(Get-CimInstance Win32_Service -Filter \"Name='{ServiceName}'\").StartName",
                "query service account");
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

            // 12 + 13. authorized vs unauthorized NON-ADMIN identities. The generated password
            // is passed to PowerShell ONLY as a per-call child-process environment variable -
            // never interpolated into script/command text - so it can never appear in a timeout
            // or non-zero-exit diagnostic (SEC-001).
            var passwordEnvironment = new Dictionary<string, string> { ["PSM_TEST_PASSWORD"] = password };
            const string createUserScript =
                "$p = ConvertTo-SecureString $env:PSM_TEST_PASSWORD -AsPlainText -Force; " +
                "New-LocalUser -Name '{0}' -Password $p -AccountNeverExpires:$true | Out-Null";

            RunPowerShell(string.Format(createUserScript, userA), "create temporary non-admin user A", passwordEnvironment);
            RunPowerShell(string.Format(createUserScript, userB), "create temporary non-admin user B", passwordEnvironment);
            RunPowerShell($"Add-LocalGroupMember -Group '{GroupName}' -Member '{userA}'", "add user A to activation group");
            log.Add($"[12,13] created non-admin users {userA} (in group) and {userB} (not in group)");

            foreach (var user in new[] { userA, userB })
            {
                var isAdmin = RunPowerShell(
                    $"[bool](Get-LocalGroupMember -Group 'Administrators' -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '*\\{user}' }})",
                    $"query administrator membership for {user}");
                Equal("False", isAdmin, $"[12,13] {user} must not be an Administrator (sanity, elevated-process view)");
            }

            log.Add("[12,13] both test identities confirmed non-admin (elevated-process view)");

            // 2C. starting the service must be refused - and must not leave a hollow "Running"
            // process - while the exclusivity lock is already held.
            await VerifyStartupReadinessRefusesWhenLockAlreadyHeldAsync(lifecycle, log);

            // 8-11 + 4/5: exclusivity lock behavior around the real (isolated) service lifetime,
            // production-isolation proof, and PID-exact abnormal-termination coverage.
            await VerifyExclusivityLockAsync(lifecycle, log);

            // 12/13: the REAL authorization boundary, proven from inside each user's own token.
            await VerifyNonAdminActivationBoundaryAsync(lifecycle, userA, userB, password, log);

            // 14: real cross-user DPAPI isolation.
            await VerifyCrossUserDpapiIsolationAsync(dpapiDir, userA, userB, password, log);
        }
        finally
        {
            // 15. always remove every temporary resource; cleanup failure FAILS the run.
            TryCleanup(() => lifecycle.StopAsync().GetAwaiter().GetResult(), "stop service", cleanupErrors);
            TryCleanup(() => lifecycle.UninstallAsync().GetAwaiter().GetResult(), "uninstall service", cleanupErrors);
            TryCleanup(() => RunPowerShell($"Remove-LocalGroup -Name '{GroupName}' -ErrorAction SilentlyContinue", "remove temporary group"), "remove group", cleanupErrors);
            TryCleanup(() => RunPowerShell($"Remove-LocalUser -Name '{userA}' -ErrorAction SilentlyContinue", "remove temporary user A"), "remove userA", cleanupErrors);
            TryCleanup(() => RunPowerShell($"Remove-LocalUser -Name '{userB}' -ErrorAction SilentlyContinue", "remove temporary user B"), "remove userB", cleanupErrors);
            TryCleanup(() => { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }, "remove isolated data root", cleanupErrors);
            TryCleanup(() => { if (Directory.Exists(dpapiDir)) Directory.Delete(dpapiDir, recursive: true); }, "remove DPAPI test directory", cleanupErrors);
        }

        // ISOLATION assertion: the whole run above must not have touched the real production root.
        var productionRootExistsAfter = Directory.Exists(productionRoot);
        Equal(productionRootExistedBefore, productionRootExistsAfter, "[4] the run must not create/remove the production %ProgramData% Host root");
        if (productionRootExistedBefore)
        {
            Equal(productionRootWriteTimeBefore, Directory.GetLastWriteTimeUtc(productionRoot), "[4] the production Host root must not be modified");
        }

        log.Add("[4] production %ProgramData% Host root confirmed untouched for the whole run");

        if (cleanupErrors.Count > 0)
        {
            throw new Exception("[15] cleanup FAILED - temporary resources may remain: " + string.Join("; ", cleanupErrors));
        }

        log.Add("[15] all temporary services/groups/users/directories removed");
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
        var sddl = RunPowerShell($"(sc.exe sdshow {ServiceName}) -join ''", "read service DACL").Trim();
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

    /// <summary>Item 2C: refused start while the lock is held must not leave a hollow "Running" process.</summary>
    private static async Task VerifyStartupReadinessRefusesWhenLockAlreadyHeldAsync(WindowsHostServiceLifecycle lifecycle, List<string> log)
    {
        using var preHeld = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), MutexName);
        True(preHeld is not null, "sanity: the harness process holds the unique test mutex first");

        Exception? startFailure = null;
        try
        {
            await lifecycle.StartAsync();
        }
        catch (Exception ex)
        {
            startFailure = ex;
        }

        True(startFailure is not null, "[2C] starting the service must fail while the exclusivity lock is already held");

        var status = await lifecycle.QueryStatusAsync();
        True(status.State != HostServiceState.Running, "[2C] the service must not remain Running as a hollow process after a refused start");

        log.Add($"[2C] startup readiness correctly refused to start while the lock was held ({startFailure!.GetType().Name})");
    }

    /// <summary>Items 4/5/8-11: the #40 exclusivity lock, isolation, and PID-exact crash recovery around the real (isolated) service lifetime.</summary>
    private static async Task VerifyExclusivityLockAsync(WindowsHostServiceLifecycle lifecycle, List<string> log)
    {
        await lifecycle.StartAsync();
        Equal(HostServiceState.Running, (await lifecycle.QueryStatusAsync()).State, "[8] service reached Running");

        // 8 + 9: while the isolated Host workload runs it holds ITS OWN unique mutex, so a second
        // contender for that SAME mutex is denied.
        using (var contender = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(2), MutexName))
        {
            True(contender is null, "[8,9] a second exclusivity-lock contender is denied while the Host runs");
        }

        log.Add("[8,9] lock held by the running service; second contender denied");

        // 4 (isolation, mid-run): the PRODUCTION default mutex must remain completely free while
        // the isolated test service is running - proving it never touched the real mutex.
        using (var productionMutexProbe = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(2)))
        {
            True(productionMutexProbe is not null, "[4] the isolated test service must never acquire the production exclusivity mutex");
        }

        log.Add("[4] production exclusivity mutex confirmed free while the isolated service runs");

        // 10: a normal stop releases it.
        await lifecycle.StopAsync();
        using (var afterStop = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), MutexName))
        {
            True(afterStop is not null, "[10] normal stop releases the exclusivity lock");
        }

        log.Add("[10] normal stop released the lock");

        // 11: abnormal termination must leave the lock reacquirable. Restart, capture the EXACT
        // PID once, prove the ACTUAL running process token identity (item 3 - independent of the
        // StartName configuration evidence already checked above), then reuse that SAME PID for
        // termination - never re-querying by name.
        await lifecycle.StartAsync();

        var processId = int.Parse(RunPowerShell(
            $"(Get-CimInstance Win32_Service -Filter \"Name='{ServiceName}'\").ProcessId", "query temporary service process id"));
        True(processId > 0, "[5] resolved a real PID for the temporary test service before terminating it");

        var actualTokenSid = ProcessTokenInspector.GetProcessTokenUserSid(processId);
        var expectedSid = ServiceSecurityIdentifier.ForServiceName(ServiceName);
        Equal(expectedSid.Value, actualTokenSid.Value,
            "[token] the ACTUAL running process token SID must equal the derived per-service SID - this is runtime proof, independent of merely reading Win32_Service.StartName");
        log.Add($"[token] running process (PID {processId}) token SID confirmed to equal the derived per-service SID: {actualTokenSid.Value}");

        RunPowerShell($"Stop-Process -Id {processId} -Force", "terminate temporary service process");

        // Required ordering: (3) wait until SCM DEFINITIVELY reports not Running/StartPending -
        // never a silent deadline - THEN (4) prove the unique test mutex is reacquirable. Item 11
        // is logged as PASS only once BOTH have actually succeeded.
        await WaitUntilNotRunningAsync(lifecycle);

        using (var afterKill = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(10), MutexName))
        {
            True(afterKill is not null, "[11] the lock is reacquirable after abnormal termination");
        }

        log.Add($"[5,11] terminated exact PID {processId} (never by process name); SCM confirmed not-running, then the lock was reacquired");
    }

    /// <summary>
    /// Succeeds ONLY once SCM actually reports a non-Running/non-StartPending state - never
    /// silently returns after an unexpired deadline while the service might still be Running.
    /// </summary>
    private static async Task WaitUntilNotRunningAsync(WindowsHostServiceLifecycle lifecycle)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var lastObserved = HostServiceState.Other;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await lifecycle.QueryStatusAsync();
            lastObserved = status.State;
            if (status.State is not (HostServiceState.Running or HostServiceState.StartPending))
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new System.TimeoutException(
            $"Service did not leave Running/StartPending within the deadline; last observed state: {lastObserved}.");
    }

    /// <summary>
    /// Items 12/13: the REAL non-admin authorization boundary, executed under each temporary
    /// user's OWN token via CreateProcessWithLogonW - never inferred from group membership by the
    /// elevated harness process.
    /// </summary>
    private static async Task VerifyNonAdminActivationBoundaryAsync(
        WindowsHostServiceLifecycle lifecycle, string userA, string userB, string password, List<string> log)
    {
        // AUTHORIZED user (member of the activation group).
        Equal("NONADMIN", RunAsUser(userA, password, "--helper-nonadmin-check", "[12] userA token check"),
            "[12] the authorized user's own token must be non-admin");

        var startResult = RunAsUser(userA, password, $"--helper-activation {ServiceName}", "[12] userA activation");
        Equal("StartRequested", startResult, "[12] the authorized, group-member user must be able to request a start");

        var reachedRunning = false;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await lifecycle.QueryStatusAsync()).State == HostServiceState.Running)
            {
                reachedRunning = true;
                break;
            }

            await Task.Delay(500);
        }

        True(reachedRunning, "[12] the service must actually reach Running after the authorized user's start request");
        log.Add("[12] authorized non-admin user (real token) started the service via IHostActivation");

        // Even the AUTHORIZED user must be denied direct native rights beyond START/QUERY_STATUS.
        var rightsOutput = RunAsUser(userA, password, $"--helper-native-rights {ServiceName}", "[12] userA native rights probe");
        foreach (var line in rightsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            True(line.StartsWith("DENIED:", StringComparison.Ordinal),
                $"[12] the authorized user must be denied every right beyond START/QUERY_STATUS, got: {line}");
        }

        log.Add("[12] authorized non-admin user's real token confirmed denied STOP/CHANGE_CONFIG/DELETE");

        await lifecycle.StopAsync();
        await WaitUntilNotRunningAsync(lifecycle);

        // UNAUTHORIZED user (not a member of the activation group).
        Equal("NONADMIN", RunAsUser(userB, password, "--helper-nonadmin-check", "[13] userB token check"),
            "[13] the unauthorized user's own token must be non-admin");

        var deniedResult = RunAsUser(userB, password, $"--helper-activation {ServiceName}", "[13] userB activation");
        Equal("AccessDenied", deniedResult, "[13] a non-member user must be denied both query and start authority");

        var status = await lifecycle.QueryStatusAsync();
        True(status.State != HostServiceState.Running, "[13] the service must remain stopped after an unauthorized start attempt");
        log.Add("[13] unauthorized non-admin user's real token confirmed denied; service remained stopped");
    }

    /// <summary>
    /// Item 14: real cross-user DPAPI CurrentUser isolation, using two genuine Windows identities.
    /// The DPAPI test directory is explicitly shared (both users granted filesystem access) so a
    /// failure to load proves DPAPI identity isolation specifically, not mere file-access denial.
    /// </summary>
    private static async Task VerifyCrossUserDpapiIsolationAsync(string dpapiDir, string userA, string userB, string password, List<string> log)
    {
        Directory.CreateDirectory(dpapiDir);
        RunPowerShell($"icacls '{dpapiDir}' /grant '{userA}:(OI)(CI)F' '{userB}:(OI)(CI)F' | Out-Null",
            "grant DPAPI test directory access to both temporary users");

        var createResult = RunAsUser(userA, password, $"--helper-dpapi-create \"{dpapiDir}\"", "[14] userA DPAPI create");
        Equal("OK", createResult, "[14] userA must be able to create and bind its own credential");

        var sameUserLoad = RunAsUser(userA, password, $"--helper-dpapi-load \"{dpapiDir}\"", "[14] userA DPAPI reload");
        Equal("SUCCESS", sameUserLoad, "[14] userA must be able to reload its OWN credential (sanity)");

        var crossUserLoad = RunAsUser(userB, password, $"--helper-dpapi-load \"{dpapiDir}\"", "[14] userB DPAPI cross-user load");
        Equal("DPAPI_DENIED", crossUserLoad,
            "[14] userB has filesystem access to the same shared file but must be denied by DPAPI identity isolation specifically");

        await Task.CompletedTask;
        log.Add("[14] cross-user DPAPI isolation confirmed: same-user load succeeds, cross-user load is DPAPI-denied despite shared file access");
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
