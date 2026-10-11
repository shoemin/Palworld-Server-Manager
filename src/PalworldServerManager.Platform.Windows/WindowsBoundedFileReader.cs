using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

// Every component is opened relative to a retained directory handle. No path reopen
// after validation, no create disposition, no backup intent, and no privilege retry.
public sealed class WindowsBoundedFileReader : IBoundedFileReader
{
    public const int MaximumSupportedBytes = 4_194_308;
    private readonly Action? opened;
    public WindowsBoundedFileReader() { }
    internal WindowsBoundedFileReader(Action opened) { this.opened = opened; }

    public byte[] Read(string root, IReadOnlyList<string> relativeComponents, int maximumBytes, CancellationToken ct = default)
    {
        if (maximumBytes is < 0 or > MaximumSupportedBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ct.ThrowIfCancellationRequested();
        var parts = Components(root, relativeComponents);
        // Drive-qualified spelling alone also permits mapped network drives.
        if (GetDriveTypeW(root[..3]) is not (2 or 3 or 5 or 6)) throw Failure(FileReadFailure.InvalidPath);
        var handles = new List<SafeFileHandle>(); byte[]? bytes = null;
        try
        {
            var drive = CreateFileW(@"\\?\" + root[..3], 0x1000A0, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            handles.Add(drive); RequireHandle(drive); CheckObject(drive, directory: true);
            foreach (var part in parts[..^1])
            {
                ct.ThrowIfCancellationRequested();
                var next = OpenRelative(handles[^1], part, directory: true);
                handles.Add(next); CheckObject(next, directory: true);
            }
            ct.ThrowIfCancellationRequested();
            var file = OpenRelative(handles[^1], parts[^1], directory: false);
            handles.Add(file); var before = CheckObject(file, directory: false);
            var length = ((ulong)before.SizeHigh << 32) | before.SizeLow;
            if (length > (ulong)maximumBytes) throw Failure(FileReadFailure.TooLarge);
            opened?.Invoke(); ct.ThrowIfCancellationRequested();
            bytes = new byte[(int)length];
            using (var stream = new FileStream(file, FileAccess.Read, 4096, isAsync: false))
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                    if (read == 0) throw Failure(FileReadFailure.Changed);
                    offset += read;
                }
                if (stream.ReadByte() != -1) throw Failure(FileReadFailure.Changed);
                var after = CheckObject(file, directory: false);
                if (after.SizeHigh != before.SizeHigh || after.SizeLow != before.SizeLow ||
                    after.Volume != before.Volume || after.IndexHigh != before.IndexHigh || after.IndexLow != before.IndexLow)
                    throw Failure(FileReadFailure.Changed);
                ct.ThrowIfCancellationRequested();
            }
            var result = bytes; bytes = null; return result;
        }
        catch (BoundedFileReadException) { throw; }
        catch (UnauthorizedAccessException) { throw Failure(FileReadFailure.AccessDenied); }
        catch (IOException ex) { throw NativeFailure(ex.HResult & 0xFFFF); }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        }
    }

    private static string[] Components(string root, IReadOnlyList<string> relative)
    {
        if (root is null || root.Length is < 3 or > 32700 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\' ||
            relative is null || relative.Count is < 1 or > 128) throw Failure(FileReadFailure.InvalidPath);
        // Copy caller-owned inputs before IO; caller mutation cannot redirect later components.
        var parts = new List<string>();
        if (root.Length > 3) parts.AddRange(root[3..].Split('\\'));
        for (var i = 0; i < relative.Count; i++) parts.Add(relative[i]);
        if (parts.Count > 128 || parts.Any(p => !ValidComponent(p))) throw Failure(FileReadFailure.InvalidPath);
        return parts.ToArray();
    }

    private static bool ValidComponent(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 || value is "." or ".." || value[^1] is '.' or ' ' ||
            value.Any(c => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')) return false;
        var stem = value.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return false;
        return !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'));
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string component, bool directory)
    {
        var text = Marshal.StringToHGlobalUni(component);
        var name = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(component.Length * 2)), MaximumLength = checked((ushort)((component.Length + 1) * 2)), Buffer = text }, name, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = name, Attributes = 0x40 };
            // Type-neutral no-follow open, then verify type on that exact handle. FILE_OPEN only.
            var status = NtCreateFile(out var file, directory ? 0x1000A0u : 0x80100000u, ref attributes, out _, IntPtr.Zero, 0, 1, 1,
                0x00200020, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0) { file.Dispose(); throw NativeFailure(unchecked((int)RtlNtStatusToDosError(status))); }
            return file;
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(text); }
    }

    private static FileInformation CheckObject(SafeFileHandle file, bool directory)
    {
        if (GetFileType(file) != 1) throw Failure(FileReadFailure.UnsafeObject);
        if (!GetFileInformationByHandle(file, out var info)) throw NativeFailure(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw Failure(FileReadFailure.UnsafeObject);
        return info;
    }
    private static void RequireHandle(SafeFileHandle file)
    { if (file.IsInvalid) throw NativeFailure(Marshal.GetLastWin32Error()); }
    private static BoundedFileReadException Failure(FileReadFailure kind) => new(kind);
    private static BoundedFileReadException NativeFailure(int error) => Failure(error switch
    {
        2 or 3 => FileReadFailure.NotFound,
        5 => FileReadFailure.AccessDenied,
        32 or 33 => FileReadFailure.Busy,
        1920 or 4390 or 4392 or 4393 or 4394 => FileReadFailure.UnsafeObject,
        _ => FileReadFailure.Unavailable
    });
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal int Length; internal IntPtr Root, Name; internal uint Attributes; internal IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { internal uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("ntdll.dll", ExactSpelling = true)] private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
