namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// Builds the SCM <c>lpBinaryPathName</c> value.
///
/// The executable path and its arguments are kept SEPARATE internally and combined only here,
/// because an unquoted service path containing spaces is a classic Windows privilege-escalation
/// vector: SCM would try "C:\Program.exe" before "C:\Program Files\App\Host.exe", letting anyone
/// able to write that earlier path run code as the service account.
/// </summary>
public static class ServiceBinaryPath
{
    public static string Build(string executablePath, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("A service executable path is required.", nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(executablePath))
        {
            // #41 never chooses an install directory: the caller supplies the executable's
            // location, and a relative (or drive-relative) path would resolve against SCM's own
            // working directory rather than any location the caller actually meant.
            throw new ArgumentException("A service executable path must be absolute.", nameof(executablePath));
        }

        if (executablePath.Contains('"'))
        {
            // A quote inside the path would let a caller terminate the quoted argument early and
            // append their own; refuse rather than attempt to escape it.
            throw new ArgumentException("A service executable path must not contain a quote character.", nameof(executablePath));
        }

        // Always quote, not just when a space is present: quoting unconditionally is correct for
        // SCM and removes any dependence on getting the "does it need quotes" test right.
        var quoted = $"\"{executablePath}\"";

        return string.IsNullOrWhiteSpace(arguments) ? quoted : $"{quoted} {arguments}";
    }
}
