using System.Text;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class ManagedServerSettingsReaderTests
{
    private static readonly Guid Profile = Guid.Parse("a7847214-86db-4278-ae4e-4fa4e6992bdf");
    private sealed class Root(string path) : IManagedStorageRoot
    {
        public string Path => path;
        internal int Calls;
        internal Action<int>? ValidateAction;
        public void Validate(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Calls++; ValidateAction?.Invoke(Calls); }
    }
    private sealed class Reader : IBoundedFileReader
    {
        internal readonly List<(string Root, string[] Parts, int Limit)> Calls = [];
        internal Func<int, byte[]> Result = _ => [1, 2, 3];
        public byte[] Read(string root, IReadOnlyList<string> relativeComponents, int maximumBytes, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls.Add((root, relativeComponents.ToArray(), maximumBytes)); return Result(Calls.Count); }
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Managed settings reader assertion failed."); }
    private static void Refuses(Action action, FileReadFailure expected)
    {
        try { action(); throw new Exception("Unsafe settings read succeeded."); }
        catch (BoundedFileReadException ex) { Check(ex.Failure == expected && ex.Message == "Bounded file read refused." && ex.InnerException is null); }
    }
    private static void Cancelled(Action action)
    { try { action(); throw new Exception("Cancelled settings read succeeded."); } catch (OperationCanceledException) { } }
    private static string Install(Root root) => Path.Combine(root.Path, Profile.ToString("N"));

    internal static Task ExactRoutingAndOwnedLifetime()
    {
        var root = new Root(@"C:\Synthetic\Servers"); byte[] active = [0, 255, 42]; byte[] defaults = [12, 0, 71];
        var reader = new Reader { Result = n => n == 1 ? active : defaults };
        var files = new WindowsManagedServerSettingsReader(root, reader).Read(Profile, Install(root));
        Check(root.Calls == 2 && reader.Calls.Count == 2 && reader.Calls.All(c => c.Root == root.Path && c.Limit == 4_194_308));
        Check(reader.Calls[0].Parts.SequenceEqual(new[] { Profile.ToString("N"), "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini" }));
        Check(reader.Calls[1].Parts.SequenceEqual(new[] { Profile.ToString("N"), "DefaultPalWorldSettings.ini" }));
        var retained = files.Active; var retainedDefault = files.InstallationDefaults!.Value;
        Check(retained.Span.SequenceEqual(active) && retainedDefault.Span.SequenceEqual(defaults));
        files.Dispose(); files.Dispose(); Check(active.All(b => b == 0) && defaults.All(b => b == 0) && retained.Span.IndexOfAnyExcept((byte)0) < 0 && retainedDefault.Span.IndexOfAnyExcept((byte)0) < 0);
        try { _ = files.Active; throw new Exception("Disposed bytes exposed."); } catch (ObjectDisposedException) { }
        try { _ = files.InstallationDefaults; throw new Exception("Disposed defaults exposed."); } catch (ObjectDisposedException) { }
        using var empty = new ManagedServerSettingsFiles([], []); Check(empty.InstallationDefaults is { Length: 0 });
        using var absent = new ManagedServerSettingsFiles([], null); Check(absent.InstallationDefaults is null);
        return Task.CompletedTask;
    }
    internal static Task BindingRefusalBeforeIo()
    {
        var root = new Root(@"C:\Synthetic\Servers"); var reader = new Reader(); var sut = new WindowsManagedServerSettingsReader(root, reader);
        foreach (var path in new[] { null, "", "relative", root.Path, Install(root) + "\\", Install(root).ToUpperInvariant(),
            Path.Combine(root.Path, Guid.NewGuid().ToString("N")), Install(root) + "\\..", @"C:\Legacy\Server", @"\\host\share" })
            Refuses(() => sut.Read(Profile, path), FileReadFailure.InvalidPath);
        Refuses(() => sut.Read(Guid.Empty, Path.Combine(root.Path, Guid.Empty.ToString("N"))), FileReadFailure.InvalidPath);
        Check(root.Calls == 0 && reader.Calls.Count == 0);
        return Task.CompletedTask;
    }
    internal static Task OptionalDefaultsAndFailureClearing()
    {
        foreach (var failure in Enum.GetValues<FileReadFailure>())
        {
            var root = new Root(@"C:\Synthetic\Servers"); byte[] active = [10, 20, 30];
            var reader = new Reader { Result = n => n == 1 ? active : throw new BoundedFileReadException(failure) };
            var sut = new WindowsManagedServerSettingsReader(root, reader);
            if (failure == FileReadFailure.NotFound)
            { using var result = sut.Read(Profile, Install(root)); Check(result.InstallationDefaults is null && active[0] == 10 && root.Calls == 2); }
            else Refuses(() => sut.Read(Profile, Install(root)), failure);
            Check(active.All(b => b == 0));
            var missing = new Reader { Result = _ => throw new BoundedFileReadException(failure) };
            Refuses(() => new WindowsManagedServerSettingsReader(root, missing).Read(Profile, Install(root)), failure);
            Check(missing.Calls.Count == 1);
        }
        return Task.CompletedTask;
    }
    internal static Task CancellationAndFinalPolicyRefusal()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var root = new Root(@"C:\Synthetic\Servers"); var reader = new Reader();
        Cancelled(() => new WindowsManagedServerSettingsReader(root, reader).Read(Profile, Install(root), before.Token));
        Check(root.Calls == 0 && reader.Calls.Count == 0);
        foreach (var phase in new[] { 1, 2, 3 })
        {
            using var cancel = new CancellationTokenSource(); byte[] active = [41]; byte[] defaults = [42];
            root = new Root(@"C:\Synthetic\Servers");
            reader = new Reader { Result = n => { if (n == phase) cancel.Cancel(); return n == 1 ? active : defaults; } };
            if (phase == 3) root.ValidateAction = n => { if (n == 2) cancel.Cancel(); };
            Cancelled(() => new WindowsManagedServerSettingsReader(root, reader).Read(Profile, Install(root), cancel.Token));
            Check(active[0] == 0 && (phase == 1 ? defaults[0] == 42 && reader.Calls.Count == 1 : defaults[0] == 0));
        }
        foreach (var late in new[] { false, true })
        {
            byte[] active = [41]; byte[] defaults = [42]; root = new Root(@"C:\Synthetic\Servers")
            { ValidateAction = n => { if (n == (late ? 2 : 1)) throw new UnauthorizedAccessException("SYNTHETIC-PRIVATE-PATH"); } };
            reader = new Reader { Result = n => n == 1 ? active : defaults };
            Refuses(() => new WindowsManagedServerSettingsReader(root, reader).Read(Profile, Install(root)), FileReadFailure.AccessDenied);
            Check(late ? active[0] == 0 && defaults[0] == 0 : reader.Calls.Count == 0);
        }
        byte[] owned = [12]; root = new Root(@"C:\Synthetic\Servers");
        reader = new Reader { Result = n => n == 1 ? owned : throw new IOException("SYNTHETIC-PRIVATE-PATH") };
        Refuses(() => new WindowsManagedServerSettingsReader(root, reader).Read(Profile, Install(root)), FileReadFailure.Unavailable);
        Check(owned[0] == 0);
        return Task.CompletedTask;
    }
    internal static Task ActualFilesStayUnchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "astra-managed-settings-" + Guid.NewGuid().ToString("N"));
        var root = new Root(directory); var install = Install(root);
        var active = Path.Combine(install, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
        var defaults = Path.Combine(install, "DefaultPalWorldSettings.ini");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(active)!);
            var sut = new WindowsManagedServerSettingsReader(root, new WindowsBoundedFileReader());
            File.WriteAllText(defaults, "OptionSettings=(ServerName=\"SYNTHETIC\")", new UTF8Encoding(true));
            var originalDefault = File.ReadAllBytes(defaults);
            Refuses(() => sut.Read(Profile, install), FileReadFailure.NotFound);
            Check(!File.Exists(active) && File.ReadAllBytes(defaults).SequenceEqual(originalDefault));
            byte[] original = [0xff, 0xfe, 40, 0, 41, 0]; File.WriteAllBytes(active, original);
            var before = File.GetLastWriteTimeUtc(active);
            using (var files = sut.Read(Profile, install)) Check(files.Active.Span.SequenceEqual(original) && files.InstallationDefaults!.Value.Span.SequenceEqual(originalDefault));
            Check(File.ReadAllBytes(active).SequenceEqual(original) && File.GetLastWriteTimeUtc(active) == before && File.ReadAllBytes(defaults).SequenceEqual(originalDefault));
            File.Delete(defaults);
            using (var files = sut.Read(Profile, install)) Check(files.InstallationDefaults is null);
            Check(!File.Exists(defaults));
            Directory.CreateDirectory(defaults);
            Refuses(() => sut.Read(Profile, install), FileReadFailure.UnsafeObject); Directory.Delete(defaults);
            using (var writer = new FileStream(active, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Refuses(() => sut.Read(Profile, install), FileReadFailure.Busy);
            using (var files = sut.Read(Profile, install)) Check(files.Active.Span.SequenceEqual(original));
        }
        finally
        {
            var full = Path.GetFullPath(directory); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("astra-managed-settings-", StringComparison.Ordinal))
                throw new Exception("Fixture cleanup path refused.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        return Task.CompletedTask;
    }
}
