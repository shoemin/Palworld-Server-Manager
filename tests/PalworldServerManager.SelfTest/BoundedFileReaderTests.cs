using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class BoundedFileReaderTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Bounded file reader assertion failed."); }
    private static void Refuses(Action action, FileReadFailure failure)
    {
        try { action(); throw new Exception("Unsafe read succeeded."); }
        catch (BoundedFileReadException ex) { Check(ex.Failure == failure && ex.Message == "Bounded file read refused." && ex.InnerException is null); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "astra-file-read-" + Guid.NewGuid().ToString("N"));
        internal string FilePath => Path.Combine(Root, "server", "config", "PalWorldSettings.ini");
        internal Fixture() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("astra-file-read-", StringComparison.Ordinal))
                throw new Exception("Fixture cleanup path refused.");
            Directory.Delete(full, true);
        }
    }
    private static readonly string[] Relative = ["server", "config", "PalWorldSettings.ini"];

    internal static Task ExactBytesAndBounds()
    {
        using var fixture = new Fixture(); var reader = new WindowsBoundedFileReader();
        foreach (var length in new[] { 0, 1, 65537, WindowsBoundedFileReader.MaximumSupportedBytes })
        {
            var expected = new byte[length]; new Random(length).NextBytes(expected); File.WriteAllBytes(fixture.FilePath, expected);
            var result = reader.Read(fixture.Root, Relative, length); Check(result.SequenceEqual(expected));
            if (length > 0) { result[0] ^= 1; Check(File.ReadAllBytes(fixture.FilePath).SequenceEqual(expected)); Refuses(() => reader.Read(fixture.Root, Relative, length - 1), FileReadFailure.TooLarge); }
        }
        foreach (var bound in new[] { -1, WindowsBoundedFileReader.MaximumSupportedBytes + 1 })
        { try { reader.Read(fixture.Root, Relative, bound); throw new Exception("Invalid bound accepted."); } catch (ArgumentOutOfRangeException) { } }
        return Task.CompletedTask;
    }

    internal static Task ClosedPathsAndMissingObjects()
    {
        using var fixture = new Fixture(); var reader = new WindowsBoundedFileReader();
        foreach (var component in new[] { "", ".", "..", "../escape", @"..\escape", "file:stream", "C:drive", @"\absolute", "tail.", "tail ", "NUL", "con.txt", "COM1", "LPT9.ext", "COM\u00b9.txt", "NUL .txt", "wild*", "q?", "new\nline", new string('x', 256), null! })
            Refuses(() => reader.Read(fixture.Root, [component], 1), FileReadFailure.InvalidPath);
        foreach (var root in new[] { "", "C:", @"C:relative", @"\\server\share", @"\\?\C:\anything", "C:/forward", fixture.Root + @"\..", fixture.Root + "\\", fixture.Root + @"\\double", null! })
            Refuses(() => reader.Read(root, ["file"], 1), FileReadFailure.InvalidPath);
        Refuses(() => reader.Read(fixture.Root, [], 1), FileReadFailure.InvalidPath);
        Refuses(() => reader.Read(fixture.Root, Enumerable.Repeat("x", 129).ToArray(), 1), FileReadFailure.InvalidPath);
        Refuses(() => reader.Read(fixture.Root, Relative, 1), FileReadFailure.NotFound);
        Refuses(() => reader.Read(fixture.Root, ["server"], 1), FileReadFailure.UnsafeObject);
        Check(!File.Exists(fixture.FilePath));
        return Task.CompletedTask;
    }

    internal static Task HardLinksAndJunctionsRefused()
    {
        using var fixture = new Fixture(); var reader = new WindowsBoundedFileReader();
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        var original = Path.Combine(outside, "private.ini"); File.WriteAllText(original, "SYNTHETIC-PRIVATE");
        if (!CreateHardLinkW(fixture.FilePath, original, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Refuses(() => reader.Read(fixture.Root, Relative, 100), FileReadFailure.UnsafeObject);
        File.Delete(fixture.FilePath);
        var junction = Path.Combine(fixture.Root, "junction"); CreateJunction(junction, outside);
        try
        {
            Refuses(() => reader.Read(fixture.Root, ["junction", "private.ini"], 100), FileReadFailure.UnsafeObject);
            Refuses(() => reader.Read(junction, ["private.ini"], 100), FileReadFailure.UnsafeObject);
            Refuses(() => reader.Read(fixture.Root, ["junction"], 100), FileReadFailure.UnsafeObject);
            Check(File.ReadAllText(original) == "SYNTHETIC-PRIVATE");
        }
        finally { Directory.Delete(junction); } // Only the created link, never its target recursively.
        return Task.CompletedTask;
    }

    internal static Task WriterAndReplacementExclusion()
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.FilePath, "ORIGINAL");
        using (var writer = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Refuses(() => new WindowsBoundedFileReader().Read(fixture.Root, Relative, 100), FileReadFailure.Busy);
        var calls = 0; var names = Relative.ToArray();
        var reader = new WindowsBoundedFileReader(() =>
        {
            calls++; names[2] = "different.ini";
            foreach (var action in new Action[] { () => File.Delete(fixture.FilePath), () => { using var writer = File.OpenWrite(fixture.FilePath); },
                () => Directory.Move(Path.GetDirectoryName(fixture.FilePath)!, Path.Combine(fixture.Root, "moved")) })
            {
                try { action(); throw new Exception("Held object could be replaced or written."); }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { }
            }
        });
        Check(Encoding.UTF8.GetString(reader.Read(fixture.Root, names, 100)) == "ORIGINAL" && calls == 1);
        // Successful removal after return proves the reader released file and ancestor handles.
        File.Delete(fixture.FilePath); Directory.Move(Path.GetDirectoryName(fixture.FilePath)!, Path.Combine(fixture.Root, "moved"));
        return Task.CompletedTask;
    }

    internal static Task AccessDeniedAndCleanup()
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.FilePath, "SYNTHETIC-PRIVATE");
        var file = new FileInfo(fixture.FilePath); var original = file.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent(); var denied = file.GetAccessControl();
        denied.AddAccessRule(new(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            file.SetAccessControl(denied);
            Refuses(() => new WindowsBoundedFileReader().Read(fixture.Root, Relative, 100), FileReadFailure.AccessDenied);
        }
        finally
        {
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            file.SetAccessControl(restored);
            Check(file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) == original.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        Check(Encoding.UTF8.GetString(new WindowsBoundedFileReader().Read(fixture.Root, Relative, 100)) == "SYNTHETIC-PRIVATE");
        File.Delete(fixture.FilePath); return Task.CompletedTask;
    }

    internal static Task CancellationReleasesHandles()
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.FilePath, "ORIGINAL");
        using var cancel = new CancellationTokenSource();
        var reader = new WindowsBoundedFileReader(cancel.Cancel);
        try { reader.Read(fixture.Root, Relative, 100, cancel.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancel.Token); }
        try { reader.Read(fixture.Root, Relative, 100, cancel.Token); throw new Exception("Pre-cancellation ignored."); }
        catch (OperationCanceledException) { }
        File.Delete(fixture.FilePath); Directory.Move(Path.GetDirectoryName(fixture.FilePath)!, Path.Combine(fixture.Root, "released"));
        return Task.CompletedTask;
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target); var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0); BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(data, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(data, 14);
        substitute.CopyTo(data, 16); print.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string file, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
