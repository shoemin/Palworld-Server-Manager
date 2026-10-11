using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class WindowsProcessAttachmentTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Process attachment assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected attachment refusal."); }
    private sealed class Rig : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PSMProcessAttachment" + Guid.NewGuid().ToString("N")));
        internal readonly Process Child;
        internal readonly WindowsProcessInspector Inspector = new();
        internal ObservedServerProcess Identity => Inspector.Capture().Processes.Single(p => p.ProcessId == Child.Id);
        internal Rig(int seconds = 60, int exitCode = 0)
        {
            try { Child = SyntheticPalServerHarness.Start(root, seconds, exitCode); }
            catch { DeleteFixtureDirectory(); throw; }
        }
        public void Dispose()
        {
            try { if (!Child.HasExited) Child.Kill(entireProcessTree: true); Check(Child.WaitForExit(10000)); }
            finally { Child.Dispose(); }
            DeleteFixtureDirectory();
        }
        private void DeleteFixtureDirectory()
        {
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "PSMProcessAttachment", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Synthetic cleanup refused.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static Task ExactIdentityRequiredAndDisposalIsReadOnly()
    {
        using var r = new Rig(); var identity = r.Identity;
        Check(r.Inspector.Attach(new(identity.ProcessId, identity.CreatedUtc.AddTicks(1), identity.ImagePath)).State == ProcessAttachmentState.Changed);
        var wrongPath = Path.Combine(Path.GetDirectoryName(identity.ImagePath)!, "different", "PalServer.exe");
        Check(r.Inspector.Attach(new(identity.ProcessId, identity.CreatedUtc, wrongPath)).State == ProcessAttachmentState.Changed);
        Check(r.Inspector.Attach(new(identity.ProcessId, identity.CreatedUtc, identity.ImagePath.ToUpperInvariant())).State == ProcessAttachmentState.Changed);
        var result = r.Inspector.Attach(identity); Check(result.State == ProcessAttachmentState.Attached && result.Lease is not null);
        using var lease = result.Lease!; Check(ReferenceEquals(lease.Identity, identity));
        Check(lease.ReadExit().State == ObservedProcessExitState.Running && lease.ReadExit().ExitCode is null);
        lease.Dispose(); lease.Dispose(); Reject<ObjectDisposedException>(() => lease.ReadExit());
        Check(!r.Child.HasExited); // Disposing an observation lease never stops the game.
        using var second = r.Inspector.Attach(identity).Lease!; Check(second.ReadExit().State == ObservedProcessExitState.Running);
        return Task.CompletedTask;
    }

    public static async Task NaturalExitCodesIncludingStillActiveAreCaptured()
    {
        foreach (var code in new[] { 37, 259, -1 })
        {
            using var r = new Rig(1, code); using var lease = r.Inspector.Attach(r.Identity).Lease!;
            await r.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var exit = lease.ReadExit(); Check(exit.State == ObservedProcessExitState.Exited && exit.ExitCode == code);
            Check(lease.ReadExit().ExitCode == code); // Retained handle remains readable after exit.
        }
    }

    public static Task PermissionFailureIsUnknownWithoutLosingExistingHandle()
    {
        using var r = new Rig(); var identity = r.Identity;
        using var retained = r.Inspector.Attach(identity).Lease!;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        // Only our synthetic child's process object changes. Existing owned handles retain
        // their rights, so cleanup never needs to reopen an inaccessible PID or elevate.
        var sddl = "D:P(D;;0x00101000;;;WD)(A;;GA;;;" + sid + ")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var security, out _))
            throw new Exception("Synthetic descriptor setup failed.");
        try { if (!SetKernelObjectSecurity(r.Child.SafeHandle, 4, security)) throw new Exception("Synthetic process permission setup failed."); }
        finally { LocalFree(security); }
        var denied = r.Inspector.Attach(identity); Check(denied.State == ProcessAttachmentState.Unknown && denied.Lease is null);
        Check(retained.ReadExit().State == ObservedProcessExitState.Running);
        r.Child.Kill(entireProcessTree: false); Check(r.Child.WaitForExit(10000));
        Check(retained.ReadExit().State == ObservedProcessExitState.Exited);
        return Task.CompletedTask;
    }

    public static Task CancellationMalformedAndMissingIdentityRefuse()
    {
        var inspector = new WindowsProcessInspector(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var time = DateTimeOffset.UtcNow;
        Reject<ArgumentNullException>(() => inspector.Attach(null!));
        Reject<ArgumentException>(() => inspector.Attach(new(12, time, "PalServer.exe")));
        Reject<ArgumentException>(() => inspector.Attach(new(12, time, @"C:\fixture\other.exe")));
        Reject<OperationCanceledException>(() => inspector.Attach(new(12, time, @"C:\fixture\PalServer.exe"), cancel.Token));
        var missing = inspector.Attach(new(int.MaxValue, time, @"C:\fixture\PalServer.exe"));
        Check(missing.State == ProcessAttachmentState.Gone && missing.Lease is null);
        return Task.CompletedTask;
    }

    public static Task ConcurrentReadAndDisposeCannotUseFreedHandle()
    {
        using var r = new Rig(); var identity = r.Identity;
        using var self = Process.GetCurrentProcess();
        using (var warmup = r.Inspector.Attach(identity).Lease!) warmup.ReadExit();
        var before = self.HandleCount;
        for (var i = 0; i < 100; i++)
        {
            var lease = r.Inspector.Attach(identity).Lease!;
            Parallel.Invoke(() =>
            {
                for (var j = 0; j < 10; j++)
                    try { Check(lease.ReadExit().State == ObservedProcessExitState.Running); }
                    catch (ObjectDisposedException) { }
            }, lease.Dispose, lease.Dispose);
            Reject<ObjectDisposedException>(() => lease.ReadExit());
        }
        self.Refresh(); Check(self.HandleCount <= before + 24 && !r.Child.HasExited);
        return Task.CompletedTask;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string value, uint revision, out IntPtr security, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKernelObjectSecurity(SafeProcessHandle process, uint information, IntPtr security);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
