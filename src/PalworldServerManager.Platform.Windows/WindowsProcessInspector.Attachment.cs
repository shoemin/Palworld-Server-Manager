using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed partial class WindowsProcessInspector
{
    public ProcessAttachment Attach(ObservedServerProcess expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Path.IsPathFullyQualified(expected.ImagePath) || !IsCandidateName(Path.GetFileName(expected.ImagePath)))
            throw new ArgumentException("Observed game process identity required.");
        ct.ThrowIfCancellationRequested();
        SafeProcessHandle? process = Native.OpenProcess(0x00101000, false, expected.ProcessId);
        try
        {
            if (process.IsInvalid)
                return Marshal.GetLastWin32Error() == 87 ? ProcessAttachment.Gone : ProcessAttachment.Unknown;
            if (!Native.GetProcessTimes(process, out var created, out _, out _, out _)) return GoneOrUnknown(process);
            DateTimeOffset createdUtc;
            try { createdUtc = new(DateTime.FromFileTimeUtc(created)); }
            catch (ArgumentException) { return ProcessAttachment.Unknown; }
            if (createdUtc != expected.CreatedUtc) return ProcessAttachment.Changed;
            var image = new StringBuilder(32768); var length = image.Capacity;
            if (!Native.QueryFullProcessImageNameW(process, 0, image, ref length) || length <= 0 || length >= image.Capacity)
                return GoneOrUnknown(process);
            if (!string.Equals(image.ToString(), expected.ImagePath, StringComparison.Ordinal)) return ProcessAttachment.Changed;
            ct.ThrowIfCancellationRequested();
            var result = ProcessAttachment.Attached(new RetainedProcess(expected, process));
            process = null; // Ownership transfers only after successful identity comparison/construction.
            return result;
        }
        finally { process?.Dispose(); }
    }

    private static ProcessAttachment GoneOrUnknown(SafeProcessHandle process)
        => Native.WaitForSingleObject(process, 0) == 0 ? ProcessAttachment.Gone : ProcessAttachment.Unknown;

    private sealed class RetainedProcess : IObservedProcessLease
    {
        private readonly object gate = new();
        private readonly SafeProcessHandle process;
        private bool disposed;
        public ObservedServerProcess Identity { get; }
        internal RetainedProcess(ObservedServerProcess identity, SafeProcessHandle process) { Identity = identity; this.process = process; }
        public ObservedProcessExit ReadExit()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(RetainedProcess));
                var state = Native.WaitForSingleObject(process, 0);
                if (state == 258) return ObservedProcessExit.Running;
                if (state != 0) return ObservedProcessExit.Unknown;
                // Signaled handle proves this process exited; 259 is now an actual exit code,
                // never a substitute for a liveness test. Missing code remains explicit.
                return ObservedProcessExit.Exited(Native.GetExitCodeProcess(process, out var code) ? unchecked((int)code) : null);
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; process.Dispose();
            }
        }
    }
}
