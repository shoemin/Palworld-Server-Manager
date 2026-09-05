using Microsoft.Win32;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

/// <summary>Registry seam so tests never touch the real user's startup configuration.</summary>
public interface ILoginStartRegistryStore
{
    string? GetValue(string valueName);

    void SetValue(string valueName, string value);

    void DeleteValue(string valueName);
}

/// <summary>
/// Per-user client login-start via the standard HKCU Run key. Non-elevated, standard, and easy to
/// inspect or remove. Entirely separate from Host boot-start.
///
/// The launch command is CALLER-SUPPLIED: Velopack installs into versioned directories, so
/// resolving and persisting an executable path here would break after the next update. Packaging
/// owns the stable command.
/// </summary>
public sealed class WindowsClientLoginStartPlatform : IClientLoginStartPlatform
{
    public const string DefaultValueName = "PalworldServerManager";

    private readonly ILoginStartRegistryStore _store;
    private readonly string _valueName;

    public WindowsClientLoginStartPlatform(ILoginStartRegistryStore? store = null, string valueName = DefaultValueName)
    {
        _store = store ?? new HkcuRunRegistryStore();
        _valueName = valueName;
    }

    public Task<bool> IsLoginStartEnabledAsync(CancellationToken ct = default)
        => Task.FromResult(_store.GetValue(_valueName) is { Length: > 0 });

    public Task SetLoginStartAsync(bool enabled, string launchCommand, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!enabled)
        {
            _store.DeleteValue(_valueName);
            return Task.CompletedTask;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(launchCommand);
        _store.SetValue(_valueName, launchCommand);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Quotes an executable path for a Run value the same way a service binary path is quoted -
    /// an unquoted path with spaces is ambiguous to the shell that will execute it.
    /// </summary>
    public static string BuildLaunchCommand(string executablePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"'))
        {
            throw new ArgumentException("A launch executable path must not contain a quote character.", nameof(executablePath));
        }

        var quoted = $"\"{executablePath}\"";
        return string.IsNullOrWhiteSpace(arguments) ? quoted : $"{quoted} {arguments}";
    }
}

internal sealed class HkcuRunRegistryStore : ILoginStartRegistryStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void SetValue(string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
