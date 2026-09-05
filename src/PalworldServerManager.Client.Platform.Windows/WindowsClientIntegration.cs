using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

public static class WindowsClientCommandLine
{
    // CommandLineToArgvW/CRT escaping: double trailing backslashes before the closing quote,
    // and double backslashes preceding an embedded quote. No command interpreter is used.
    public static string Quote(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("NUL in argument.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static string Build(ClientLaunchTarget target)
    {
        if (!Path.IsPathFullyQualified(target.ExecutablePath) || target.ExecutablePath.Contains('"'))
            throw new ArgumentException("An absolute executable path is required.");
        return string.Join(" ", new[] { Quote(target.ExecutablePath) }.Concat(target.Arguments.Select(Quote)));
    }
}

public interface ILoginStartRegistry
{
    string? Read();
    void Write(string command);
    void Delete();
}
public sealed class WindowsClientLoginStart(ILoginStartRegistry registry) : IClientLoginStartPlatform
{
    public WindowsClientLoginStart() : this(new CurrentUserRunRegistry()) { }
    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(registry.Read() is not null); }
    public Task SetEnabledAsync(bool enabled, ClientLaunchTarget target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (enabled) registry.Write(WindowsClientCommandLine.Build(target)); else registry.Delete();
        return Task.CompletedTask;
    }
    private sealed class CurrentUserRunRegistry : ILoginStartRegistry
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Value = "PalworldServerManager";
        public string? Read() { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Value) as string; }
        public void Write(string command) { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue(Value, command, RegistryValueKind.String); }
        public void Delete() { using var key = Registry.CurrentUser.OpenSubKey(Key, true); key?.DeleteValue(Value, false); }
    }
}

public interface ILocalDirectoryLauncher { void Open(string directory); }
public sealed class WindowsClientShellIntegration(string diagnosticsDirectory, ILocalDirectoryLauncher launcher) : IClientShellIntegration
{
    public WindowsClientShellIntegration(string diagnosticsDirectory) : this(diagnosticsDirectory, new ExplorerLauncher()) { }
    public Task OpenClientDiagnosticsAsync(CancellationToken ct = default) => OpenAuthorizedLocalDirectoryAsync(diagnosticsDirectory, ct);
    public Task OpenAuthorizedLocalDirectoryAsync(string localDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = RequireLocalDirectory(localDirectory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException();
        launcher.Open(fullPath);
        return Task.CompletedTask;
    }
    public static string RequireLocalDirectory(string path)
    {
        // Deny UNC/device namespaces and mapped network drives. Inspect every ancestor for
        // reparse points so a local-looking junction cannot redirect Explorer to a remote path.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('"'))
            throw new ArgumentException("A local drive directory is required.");
        var full = Path.GetFullPath(path);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
            throw new ArgumentException("Only local filesystem drives are permitted.");
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse paths are not permitted.");
        return full;
    }
    private sealed class ExplorerLauncher : ILocalDirectoryLauncher
    {
        public void Open(string directory)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            info.ArgumentList.Add(directory);
            using var process = Process.Start(info);
        }
    }
}
