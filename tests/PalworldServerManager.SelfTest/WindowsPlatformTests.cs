using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

// #41 Windows platform tests.
//
// ISOLATION: everything here uses temporary directories, in-memory fakes, or test-scoped
// registry/service abstractions. Nothing creates a real service, local group, or user, and
// nothing writes to the real HKCU Run key or the real machine-wide Host data root - so an
// ordinary developer running ./scripts/build.ps1 never mutates machine state.
public static class WindowsPlatformTests
{
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

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }

    // ------------------------------------------------- service binary path quoting (security)

    public static Task TestServiceBinaryPathQuotesPathsWithSpaces()
    {
        // An unquoted service path with spaces is a classic privilege-escalation vector: SCM
        // would try C:\Program.exe before "C:\Program Files\...\Host.exe".
        Equal("\"C:\\Program Files\\Palworld Server Manager\\Host.exe\"",
            ServiceBinaryPath.Build(@"C:\Program Files\Palworld Server Manager\Host.exe"),
            "path with spaces is quoted");

        Equal("\"C:\\Apps\\Host.exe\"", ServiceBinaryPath.Build(@"C:\Apps\Host.exe"),
            "path without spaces is quoted too");

        // Path and arguments stay separate internally and combine safely.
        Equal("\"C:\\Program Files\\PSM\\Host.exe\" --service",
            ServiceBinaryPath.Build(@"C:\Program Files\PSM\Host.exe", "--service"),
            "arguments follow the quoted path");

        // A quote in the path could terminate the quoted argument early.
        Throws<ArgumentException>(() => ServiceBinaryPath.Build("C:\\Bad\"Path\\Host.exe"),
            "an embedded quote must be rejected");
        Throws<ArgumentException>(() => ServiceBinaryPath.Build("  "),
            "an empty path must be rejected");

        // #41 never chooses an install directory - a relative (or drive-relative) path would
        // resolve against SCM's own working directory rather than any location the caller meant.
        Throws<ArgumentException>(() => ServiceBinaryPath.Build(@"Apps\Host.exe"),
            "a relative path must be rejected");
        Throws<ArgumentException>(() => ServiceBinaryPath.Build(@"\Apps\Host.exe"),
            "a drive-relative (rooted but not fully qualified) path must be rejected");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- service DACL

    private static RawSecurityDescriptor BaselineDescriptor()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var dacl = new RawAcl(GenericAcl.AclRevision, 2);
        dacl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0xF01FF), system, false, null));
        dacl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0xF01FF), admins, false, null));
        return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, null, null, null, dacl);
    }

    public static Task TestActivationGroupAceGrantsExactlyStartAndQueryStatus()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var updated = ServiceDaclBuilder.AddActivationGroupAce(BaselineDescriptor(), group);

        var mask = ServiceDaclBuilder.FindActivationGroupMask(updated, group)
            ?? throw new Exception("the activation group ACE is missing");

        // SERVICE_START (0x10) | SERVICE_QUERY_STATUS (0x04) == 0x14, and nothing else.
        Equal(0x14, mask, "activation group mask is exactly SERVICE_START|SERVICE_QUERY_STATUS");

        foreach (var forbidden in ServiceDaclBuilder.ForbiddenForActivationGroup)
        {
            True((mask & forbidden) == 0, $"activation group must not hold right 0x{forbidden:X}");
        }

        return Task.CompletedTask;
    }

    public static Task TestActivationGroupAcePreservesExistingAces()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var updated = ServiceDaclBuilder.AddActivationGroupAce(BaselineDescriptor(), group);

        // SYSTEM/Administrators maintenance rights survive untouched.
        Equal(unchecked((int)0xF01FF), ServiceDaclBuilder.FindActivationGroupMask(updated, system)!.Value, "SYSTEM ACE preserved");
        Equal(unchecked((int)0xF01FF), ServiceDaclBuilder.FindActivationGroupMask(updated, admins)!.Value, "Administrators ACE preserved");
        return Task.CompletedTask;
    }

    public static Task TestActivationGroupAceIsIdempotent()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var once = ServiceDaclBuilder.AddActivationGroupAce(BaselineDescriptor(), group);
        var twice = ServiceDaclBuilder.AddActivationGroupAce(once, group);

        var aceCount = ((RawAcl)twice.DiscretionaryAcl!).Count;
        Equal(3, aceCount, "re-provisioning must not accumulate duplicate ACEs");
        Equal(0x14, ServiceDaclBuilder.FindActivationGroupMask(twice, group)!.Value, "mask unchanged after re-apply");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- activation-group provisioning

    private sealed class FakeLocalGroupNative : ILocalGroupNative
    {
        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ExistsCalls { get; } = new();
        public List<string> CreateCalls { get; } = new();

        public bool GroupExists(string groupName)
        {
            ExistsCalls.Add(groupName);
            return _existing.Contains(groupName);
        }

        public void CreateGroup(string groupName)
        {
            CreateCalls.Add(groupName);
            _existing.Add(groupName);
        }
    }

    public static Task TestActivationGroupNameDefaultsToTheStableProductGroup()
    {
        Equal(WindowsHostServiceLifecycle.DefaultActivationGroupName,
            WindowsHostServiceLifecycle.ResolveActivationGroupName(new HostServiceInstallOptions("C:\\Host.exe")),
            "a null/omitted ActivationGroupName means the stable product default, not 'no group'");

        Equal("Explicit Test Group",
            WindowsHostServiceLifecycle.ResolveActivationGroupName(
                new HostServiceInstallOptions("C:\\Host.exe", ActivationGroupName: "Explicit Test Group")),
            "an explicit group name is used as-is");
        return Task.CompletedTask;
    }

    public static Task TestLocalGroupProvisionerCreatesOnlyWhenMissingAndNeverTouchesMembership()
    {
        var native = new FakeLocalGroupNative();
        var provisioner = new LocalGroupProvisioner(native);

        provisioner.EnsureExists("PalworldServerManager Users");
        Equal(1, native.CreateCalls.Count, "a missing group is created exactly once");

        // Idempotent: re-provisioning an already-existing group creates nothing further.
        provisioner.EnsureExists("PalworldServerManager Users");
        Equal(1, native.CreateCalls.Count, "re-provisioning an existing group does not create it again");
        Equal(2, native.ExistsCalls.Count, "existence is checked on every call");

        // A different, explicit group name is provisioned independently.
        provisioner.EnsureExists("Explicit Test Group");
        Equal(2, native.CreateCalls.Count, "a distinct explicit group name is provisioned on its own");

        // Structural: the seam this provisioner is built on has no membership operation AT ALL -
        // so "no member-add call occurs" is guaranteed by the interface shape, not just current
        // behavior.
        var memberishMembers = typeof(ILocalGroupNative).GetMembers()
            .Select(m => m.Name)
            .Where(n => n.Contains("Member", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Equal(0, memberishMembers.Count, $"ILocalGroupNative must expose no membership operation; found: {string.Join(", ", memberishMembers)}");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- startup readiness / deterministic shutdown

    public static async Task TestStartupReadyOnlyAfterInitializationActuallyCompletes()
    {
        var gate = new ManualResetEventSlim(false);
        var reachedReady = false;

        var lifetime = new HostServiceLifetime(async (ct, ready) =>
        {
            await Task.Run(() => gate.Wait(ct), ct);
            reachedReady = true;
            ready.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
        });

        var startTask = Task.Run(lifetime.Start);
        await Task.Delay(300);
        True(!startTask.IsCompleted, "Start() must not return before the runtime signals readiness");
        True(!reachedReady, "sanity: the gate has not been released yet");

        gate.Set();
        await startTask;
        True(reachedReady, "the runtime actually reached the readiness point before Start() returned");

        lifetime.StopAndWait();
        lifetime.Dispose();
    }

    public static Task TestStartupFailurePropagatesBeforeReadinessCanBeClaimed()
    {
        var lifetime = new HostServiceLifetime((ct, ready) => throw new InvalidOperationException("synthetic startup failure"));
        Throws<InvalidOperationException>(lifetime.Start, "an initialization failure must propagate out of Start(), not be swallowed");
        lifetime.Dispose();
        return Task.CompletedTask;
    }

    public static async Task TestStopAndWaitBlocksUntilSimulatedCleanupActuallyCompletes()
    {
        var releaseGate = new ManualResetEventSlim(false);
        var cleanupFinished = false;

        var lifetime = new HostServiceLifetime(async (ct, ready) =>
        {
            ready.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            await Task.Run(() => releaseGate.Wait());
            cleanupFinished = true;
        });

        lifetime.Start();

        var stopTask = Task.Run(lifetime.StopAndWait);
        await Task.Delay(300);
        True(!stopTask.IsCompleted, "StopAndWait() must not return before cleanup actually finishes");
        True(!cleanupFinished, "sanity: cleanup has not been released yet");

        releaseGate.Set();
        await stopTask;
        True(cleanupFinished, "StopAndWait() only returned once cleanup genuinely finished");

        lifetime.Dispose();
    }

    // ------------------------------------------------- ProcessAsUser timeout mechanism

    public static Task TestProcessAsUserTerminatesAHungChildAndConfirmsItIsActuallyGone()
    {
        var selfTestExe = Environment.ProcessPath!;
        var pidFile = Path.Combine(Path.GetTempPath(), "psm-hungchild-pid-" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            // "--harness-report-pid <file> 60 0" writes its OWN pid to <file>, then sleeps 60s -
            // deliberately far longer than the 3s timeout given here, proving the timeout
            // genuinely bounds the wait rather than blocking on ReadToEnd first. Reuses the SAME
            // WaitWithTimeout core that the real CreateProcessWithLogonW-based Run(...) path
            // uses, via the current-user test seam.
            Throws<TimeoutException>(
                () => ProcessAsUser.RunAsCurrentUserForTest(selfTestExe, $"--harness-report-pid \"{pidFile}\" 60 0", TimeSpan.FromSeconds(3)),
                "a deliberately hung child must be terminated and reported as a timeout, not hang the caller");

            // By the time WaitWithTimeout throws, it has already confirmed termination via
            // WaitForSingleObject - but independently re-verify the EXACT reported PID is
            // actually gone, never via a process-name check.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            True(File.Exists(pidFile), "the hung child must have reported its own PID before being terminated");
            var pid = int.Parse(File.ReadAllText(pidFile).Trim());
            True(!IsProcessAlive(pid), $"the EXACT hung child (PID {pid}) must be CONFIRMED terminated, not merely assumed");
        }
        finally
        {
            try { if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }
        }

        return Task.CompletedTask;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this PID exists any more.
            return false;
        }
    }

    public static Task TestProcessAsUserCapturesOutputAndExitCodeForAWellBehavedChild()
    {
        var selfTestExe = Environment.ProcessPath!;

        // "--harness 0 0" sleeps 0s and exits 0 immediately - the ordinary success path.
        var (exitCode, _) = ProcessAsUser.RunAsCurrentUserForTest(selfTestExe, "--harness 0 3", TimeSpan.FromSeconds(30));
        Equal(3, exitCode, "a well-behaved child's real exit code must be captured, not STILL_ACTIVE or a timeout");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- RunPowerShell secret-safety (SEC-001)

    /// <summary>
    /// Proves, without running PowerShell or touching any machine state, that a secret passed
    /// through the harness's stdin-redirection channel never reaches PowerShell command text,
    /// operation-description diagnostics, or a (simulated) failure message - even in the worst
    /// case where a command's own stderr happens to echo it back. Stdin (not an environment
    /// variable) is the transport specifically because a per-call environment variable broke
    /// Windows PowerShell's module autoload on the GitHub Actions runner - see the
    /// RunPowerShell doc-comment.
    /// </summary>
    public static Task TestRunPowerShellNeverLeaksASecretIntoCommandTextOrDiagnostics()
    {
        const string sentinelSecret = "SENTINEL-SECRET-VALUE-DO-NOT-LEAK";
        const string script =
            "$p = ConvertTo-SecureString ([Console]::In.ReadLine()) -AsPlainText -Force; New-LocalUser -Name 'x' -Password $p";
        const string operationDescription = "create temporary non-admin user A";

        // The script text itself references the secret only via stdin, never interpolates it.
        True(!script.Contains(sentinelSecret, StringComparison.Ordinal),
            "the PowerShell script text must reference the secret only via stdin, never interpolate it");

        var startInfo = WindowsIntegrationTests.BuildPowerShellStartInfo(script, redirectStandardInput: true);
        True(!string.Join(' ', startInfo.ArgumentList).Contains(sentinelSecret, StringComparison.Ordinal),
            "the constructed process argument list (the actual command line) must never contain the secret");
        True(startInfo.RedirectStandardInput, "the secret transport channel must be stdin redirection, never command text or an environment variable");
        True(!startInfo.EnvironmentVariables.ContainsKey("PSM_TEST_PASSWORD"),
            "no custom environment variable is ever used for secret transport - only stdin");

        True(!operationDescription.Contains(sentinelSecret, StringComparison.Ordinal),
            "sanity: the operation description itself carries no secret");

        var timeoutMessage = WindowsIntegrationTests.BuildTimeoutMessage(operationDescription, TimeSpan.FromSeconds(60));
        True(!timeoutMessage.Contains(sentinelSecret, StringComparison.Ordinal), "a timeout failure message must never contain the secret");
        True(timeoutMessage.Contains(operationDescription, StringComparison.Ordinal), "a timeout failure message must still identify which operation failed");

        // Worst case: simulate a command whose OWN stderr happens to echo the secret back (e.g. a
        // verbose/debug trace). The sanitizer must still redact it before any exception message
        // is built from it.
        var forcedStderr = $"some diagnostic text containing {sentinelSecret} by accident";
        var sanitizedStderr = WindowsIntegrationTests.SanitizeSecret(forcedStderr, sentinelSecret);
        True(!sanitizedStderr.Contains(sentinelSecret, StringComparison.Ordinal), "sanitized stderr must never contain the secret");
        True(sanitizedStderr.Contains("***REDACTED***", StringComparison.Ordinal), "the redaction marker must be present where the secret was");

        var failureMessage = WindowsIntegrationTests.BuildNonZeroExitMessage(operationDescription, 1, sanitizedStderr);
        True(!failureMessage.Contains(sentinelSecret, StringComparison.Ordinal), "a non-zero-exit failure message must never contain the secret");

        // Text with NO secret at all (or no secret supplied) must pass through unchanged.
        Equal("nothing sensitive here", WindowsIntegrationTests.SanitizeSecret("nothing sensitive here", sentinelSecret),
            "sanitization must not alter text that never contained the secret value");
        Equal("nothing sensitive here", WindowsIntegrationTests.SanitizeSecret("nothing sensitive here", null),
            "sanitization with no secret supplied at all must be a no-op");

        return Task.CompletedTask;
    }

    // ------------------------------------------------- boot start mapping

    public static Task TestBootStartMapsToServiceStartType()
    {
        // Desktop default is boot-start OFF -> demand/manual.
        Equal(3u, WindowsHostServiceLifecycle.ToNativeStartType(HostServiceStartMode.Manual), "Manual -> SERVICE_DEMAND_START");
        Equal(2u, WindowsHostServiceLifecycle.ToNativeStartType(HostServiceStartMode.Automatic), "Automatic -> SERVICE_AUTO_START");
        Equal(4u, WindowsHostServiceLifecycle.ToNativeStartType(HostServiceStartMode.Disabled), "Disabled -> SERVICE_DISABLED");
        return Task.CompletedTask;
    }

    public static Task TestDedicatedServiceAccountIsAPerServiceVirtualAccount()
    {
        var lifecycle = new WindowsHostServiceLifecycle("TestHostSvc");
        Equal(@"NT SERVICE\TestHostSvc", lifecycle.ServiceAccountName, "per-service virtual account");

        // Must not be one of the SHARED built-in service accounts - those would not be a
        // dedicated identity at all.
        foreach (var shared in new[] { "LocalSystem", @"NT AUTHORITY\LocalService", @"NT AUTHORITY\NetworkService" })
        {
            True(!string.Equals(lifecycle.ServiceAccountName, shared, StringComparison.OrdinalIgnoreCase),
                $"service identity must not be the shared {shared}");
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------- machine data root + ACL

    public static Task TestMachineWideHostDataRootIsUnderProgramData()
    {
        var provider = new WindowsHostDataRootProvider();
        var root = provider.GetMachineWideHostDataRoot();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        True(root.StartsWith(programData, StringComparison.OrdinalIgnoreCase), "root is under CommonApplicationData");
        True(root.EndsWith(Path.Combine("PalworldServerManager", "Host"), StringComparison.OrdinalIgnoreCase), "root ends with the product/Host layout");

        // Machine-wide, never a user profile - v0.4 used LocalApplicationData.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        True(!root.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase), "Host state is not in a user profile");
        return Task.CompletedTask;
    }

    public static Task TestServiceSidIsDerivedAndMatchesWindows()
    {
        // Ground truth captured from Windows' own `sc showsid`. Deriving rather than resolving by
        // name matters because NT SERVICE\<name> only resolves once the service exists - the
        // derivation lets the Host data directory and its ACL be created independently of service
        // creation order, and `sc showsid` computes the same value for a not-yet-created service.
        Equal("S-1-5-80-3951239711-1671533544-1416304335-3763227691-3930497994",
            ServiceSecurityIdentifier.ToSddl("Spooler"), "derived SID matches sc showsid for Spooler");

        Equal("S-1-5-80-2926812050-146808811-1615361383-2252931796-2031534707",
            ServiceSecurityIdentifier.ToSddl("PalworldServerManagerHost"),
            "derived SID matches sc showsid for the product service (which need not exist)");

        // Case-insensitive, as Windows treats service names.
        Equal(ServiceSecurityIdentifier.ToSddl("Spooler"), ServiceSecurityIdentifier.ToSddl("SPOOLER"),
            "service SID derivation is case-insensitive");

        // Round-trips into a real SecurityIdentifier.
        Equal("S-1-5-80-3951239711-1671533544-1416304335-3763227691-3930497994",
            ServiceSecurityIdentifier.ForServiceName("Spooler").Value, "SecurityIdentifier round-trip");
        return Task.CompletedTask;
    }

    public static Task TestHostStateAclGrantsServiceAndAdminsButNotTheActivationGroup()
    {
        // Built without touching the filesystem.
        var security = WindowsHostDataRootProvider.BuildHostStateSecurity(@"NT SERVICE\TestHostSvc");

        True(security.AreAccessRulesProtected, "inheritance is disabled so broader ProgramData rights are not inherited");

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        var sids = rules.Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToList();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

        True(sids.Contains(system), "SYSTEM has access");
        True(sids.Contains(admins), "Administrators have access");
        // The activation group (and ordinary Users generally) must NOT gain direct Host-state
        // access - start eligibility is not SQLite authority.
        True(!sids.Contains(users), "ordinary Users have no direct Host-state access");
        Equal(3, rules.Count, "exactly SYSTEM, Administrators, and the service account");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- IHostActivation

    private sealed class FakeServiceHandle : IServiceControlHandle
    {
        private readonly Func<HostServiceRunState> _status;
        private readonly Action _start;

        public FakeServiceHandle(Func<HostServiceRunState> status, Action start)
        {
            _status = status;
            _start = start;
        }

        public HostServiceRunState Status => _status();

        public void Start() => _start();

        public void Dispose() { }
    }

    public static async Task TestActivationIsIdempotentAndMapsFailureClasses()
    {
        // Already running -> no Start issued.
        var startCalls = 0;
        var running = new WindowsHostActivation("svc", _ => new FakeServiceHandle(() => HostServiceRunState.Running, () => startCalls++));
        Equal(HostActivationResult.AlreadyRunning, await running.RequestStartAsync(), "Running -> AlreadyRunning");
        Equal(0, startCalls, "no Start is issued for an already-running service");
        Equal(true, await running.IsHostRunningAsync(), "IsHostRunning true when Running");

        // StartPending -> non-error, still no second Start.
        var pending = new WindowsHostActivation("svc", _ => new FakeServiceHandle(() => HostServiceRunState.StartPending, () => startCalls++));
        Equal(HostActivationResult.StartRequested, await pending.RequestStartAsync(), "StartPending -> StartRequested");
        Equal(0, startCalls, "no Start is issued while a start is already pending");

        // Stopped -> exactly one Start.
        var stopped = new WindowsHostActivation("svc", _ => new FakeServiceHandle(() => HostServiceRunState.Stopped, () => startCalls++));
        Equal(HostActivationResult.StartRequested, await stopped.RequestStartAsync(), "Stopped -> StartRequested");
        Equal(1, startCalls, "exactly one Start issued");

        // Failure classes are distinguishable, and IsHostRunning stays false rather than throwing.
        var missing = new WindowsHostActivation("svc", _ => throw new InvalidOperationException("x",
            new System.ComponentModel.Win32Exception(1060)));
        Equal(HostActivationResult.ServiceNotInstalled, await missing.RequestStartAsync(), "1060 -> ServiceNotInstalled");
        Equal(false, await missing.IsHostRunningAsync(), "missing service reads as not running");

        var denied = new WindowsHostActivation("svc", _ => throw new InvalidOperationException("x",
            new System.ComponentModel.Win32Exception(5)));
        Equal(HostActivationResult.AccessDenied, await denied.RequestStartAsync(), "5 -> AccessDenied");

        var other = new WindowsHostActivation("svc", _ => new FakeServiceHandle(
            () => HostServiceRunState.Stopped, () => throw new InvalidOperationException("boom")));
        Equal(HostActivationResult.StartFailed, await other.RequestStartAsync(), "other failure -> StartFailed");
    }

    // ------------------------------------------------- login start

    private sealed class FakeRegistryStore : ILoginStartRegistryStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetValue(string valueName) => Values.TryGetValue(valueName, out var v) ? v : null;

        public void SetValue(string valueName, string value) => Values[valueName] = value;

        public void DeleteValue(string valueName) => Values.Remove(valueName);
    }

    public static async Task TestLoginStartUsesTestScopedStoreAndQuotesCommand()
    {
        var store = new FakeRegistryStore();
        var platform = new WindowsClientLoginStartPlatform(store, "TestValue");

        Equal(false, await platform.IsLoginStartEnabledAsync(), "disabled initially");

        // Caller supplies the stable launch command - #41 never resolves a packaged path itself.
        var command = WindowsClientLoginStartPlatform.BuildLaunchCommand(@"C:\Program Files\PSM\Client.exe", "--minimized");
        Equal("\"C:\\Program Files\\PSM\\Client.exe\" --minimized", command, "launch command quotes a spaced path");

        await platform.SetLoginStartAsync(true, command);
        Equal(true, await platform.IsLoginStartEnabledAsync(), "enabled after set");
        Equal(command, store.Values["TestValue"], "value written to the injected store");

        await platform.SetLoginStartAsync(false, command);
        Equal(false, await platform.IsLoginStartEnabledAsync(), "disabled after clear");
        True(!store.Values.ContainsKey("TestValue"), "value removed");

        Throws<ArgumentException>(
            () => WindowsClientLoginStartPlatform.BuildLaunchCommand("C:\\Bad\"Path.exe"),
            "an embedded quote must be rejected");
    }

    // ------------------------------------------------- shell integration

    private sealed class RecordingLauncher : IShellProcessLauncher
    {
        public List<string> Launched { get; } = new();

        public void LaunchFolder(string localDirectoryPath) => Launched.Add(localDirectoryPath);
    }

    public static async Task TestShellIntegrationOpensOnlyLocalDirectoriesAndNeverSpawns()
    {
        var launcher = new RecordingLauncher();
        var shell = new WindowsClientShellIntegration(launcher);

        var temp = Path.Combine(Path.GetTempPath(), "psm-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await shell.OpenLocalFolderAsync(temp);
            Equal(1, launcher.Launched.Count, "exactly one launch recorded");
            Equal(Path.GetFullPath(temp), launcher.Launched[0], "the exact local directory is passed through");

            // A remote/UNC path is never "locally openable".
            await AssertThrowsAsync<ArgumentException>(() => shell.OpenLocalFolderAsync(@"\\server\share\data"),
                "a UNC path must be rejected");
            await AssertThrowsAsync<DirectoryNotFoundException>(
                () => shell.OpenLocalFolderAsync(Path.Combine(temp, "does-not-exist")),
                "a missing directory must be rejected");

            Equal(1, launcher.Launched.Count, "no launch occurred for the rejected paths");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string what) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }
}
