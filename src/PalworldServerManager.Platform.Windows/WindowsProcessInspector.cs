using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

internal enum ProcessObservationState { Unknown, Gone, Observed }
internal sealed record NativeProcessObservation(ProcessObservationState State, ObservedServerProcess? Process = null);
internal interface IWindowsProcessObservationApi
{
    IReadOnlyList<int>? CandidateIds();
    NativeProcessObservation Observe(int processId);
}

public sealed class WindowsProcessInspector : IProcessInspector
{
    private readonly IWindowsProcessObservationApi api;
    public WindowsProcessInspector() : this(new NativeApi()) { }
    internal WindowsProcessInspector(IWindowsProcessObservationApi api) => this.api = api;

    public ServerProcessInspection Capture(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = api.CandidateIds();
        if (ids is null) { ct.ThrowIfCancellationRequested(); return new(false, []); }
        var complete = true; var seen = new HashSet<int>(); var results = new List<ObservedServerProcess>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (id <= 0 || !seen.Add(id)) { complete = false; continue; }
            var observation = api.Observe(id);
            if (observation.State == ProcessObservationState.Gone && observation.Process is null) continue;
            if (observation.State != ProcessObservationState.Observed || observation.Process is not { } process || process.ProcessId != id)
            { complete = false; continue; }
            // The PID may have been reused since enumeration. Recheck the image reached
            // through the pinned handle, rather than trusting the old process-name hint.
            if (!Path.IsPathFullyQualified(process.ImagePath)) { complete = false; continue; }
            if (IsCandidateName(Path.GetFileName(process.ImagePath))) results.Add(process);
        }
        ct.ThrowIfCancellationRequested();
        return new(complete, results);
    }

    internal static bool IsCandidateName(string name) => name.StartsWith("PalServer", StringComparison.OrdinalIgnoreCase);

    private sealed class NativeApi : IWindowsProcessObservationApi
    {
        public IReadOnlyList<int>? CandidateIds()
        {
            using var snapshot = Native.CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS only; never inherit.
            if (snapshot.IsInvalid) return null;
            var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>(), Name = "" };
            if (!Native.Process32FirstW(snapshot, ref entry))
                return Marshal.GetLastWin32Error() == 18 ? [] : null;
            var ids = new List<int>();
            do
            {
                if (IsCandidateName(entry.Name))
                {
                    if (entry.ProcessId is 0 or > int.MaxValue) return null;
                    ids.Add((int)entry.ProcessId);
                }
            } while (Native.Process32NextW(snapshot, ref entry));
            return Marshal.GetLastWin32Error() == 18 ? ids : null; // ERROR_NO_MORE_FILES only.
        }

        public NativeProcessObservation Observe(int processId)
        {
            using var process = Native.OpenProcess(0x00101000, false, processId); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (process.IsInvalid)
                return new(Marshal.GetLastWin32Error() == 87 ? ProcessObservationState.Gone : ProcessObservationState.Unknown);
            var state = Native.WaitForSingleObject(process, 0);
            if (state == 0) return new(ProcessObservationState.Gone);
            if (state != 258) return new(ProcessObservationState.Unknown);
            var image = new StringBuilder(32768); var length = image.Capacity;
            if (!Native.QueryFullProcessImageNameW(process, 0, image, ref length) ||
                !Native.GetProcessTimes(process, out var created, out _, out _, out _))
                return AfterFailure(process);
            state = Native.WaitForSingleObject(process, 0);
            if (state == 0) return new(ProcessObservationState.Gone);
            if (state != 258 || length <= 0 || length >= image.Capacity) return new(ProcessObservationState.Unknown);
            try
            {
                return new(ProcessObservationState.Observed, new(processId,
                    new DateTimeOffset(DateTime.FromFileTimeUtc(created)), image.ToString()));
            }
            catch (ArgumentException) { return new(ProcessObservationState.Unknown); }
        }

        private static NativeProcessObservation AfterFailure(SafeProcessHandle process)
            => new(Native.WaitForSingleObject(process, 0) == 0 ? ProcessObservationState.Gone : ProcessObservationState.Unknown);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry
        {
            internal uint Size, Usage, ProcessId;
            internal UIntPtr DefaultHeap;
            internal uint ModuleId, Threads, ParentProcessId;
            internal int BasePriority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        }
        internal sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SnapshotHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32FirstW(SnapshotHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32NextW(SnapshotHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
