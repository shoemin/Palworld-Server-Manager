using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PalworldServerManager.Core.Infrastructure;

public interface IAppLogger
{
    string SessionId { get; }
    string CurrentLogFile { get; }
    string LogsDirectory { get; }

    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    IDisposable BeginOperation(string operationName, Guid? serverId = null, string? serverName = null);
}

public sealed class FileLogger : IAppLogger
{
    private sealed record LogScope(string OperationName, string OperationId, Guid? ServerId, string? ServerName, LogScope? Parent);

    private sealed class OperationScope : IDisposable
    {
        private readonly FileLogger _owner;
        private readonly LogScope _scope;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public OperationScope(FileLogger owner, LogScope scope)
        {
            _owner = owner;
            _scope = scope;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopwatch.Stop();
            _owner.WriteWithScope("INFO", $"END operation '{_scope.OperationName}' elapsed={_stopwatch.Elapsed.TotalSeconds:F2}s", null, _scope);
            _owner._currentScope.Value = _scope.Parent;
        }
    }

    private readonly object _gate = new();
    private readonly AsyncLocal<LogScope?> _currentScope = new();
    private readonly string _logFile;

    public FileLogger(AppPaths paths)
    {
        paths.EnsureCreated();
        LogsDirectory = paths.LogsRoot;
        SessionId = Guid.NewGuid().ToString("N")[..8];
        _logFile = Path.Combine(paths.LogsRoot, $"manager-{DateTime.Now:yyyyMMdd-HHmmss}-{SessionId}.log");
        CleanupOldLogs(paths.LogsRoot, TimeSpan.FromDays(30));

        Write("INFO", "===== Palworld Server Manager session started =====", null);
        Write("INFO", $"SessionId={SessionId}", null);
        Write("INFO", $"LogFile={_logFile}", null);
        Write("INFO", $"AppVersion={GetAppVersion()} Framework={RuntimeInformation.FrameworkDescription} OS={RuntimeInformation.OSDescription} ProcessArch={RuntimeInformation.ProcessArchitecture}", null);
        Write("INFO", $"ProcessId={Environment.ProcessId} BaseDirectory={AppContext.BaseDirectory}", null);
    }

    public string SessionId { get; }
    public string CurrentLogFile => _logFile;
    public string LogsDirectory { get; }

    public void Debug(string message) => Write("DEBUG", message, null);
    public void Info(string message) => Write("INFO", message, null);
    public void Warning(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public IDisposable BeginOperation(string operationName, Guid? serverId = null, string? serverName = null)
    {
        var previous = _currentScope.Value;
        var scope = new LogScope(
            string.IsNullOrWhiteSpace(operationName) ? "UnnamedOperation" : operationName.Trim(),
            Guid.NewGuid().ToString("N")[..10],
            serverId,
            serverName,
            previous);
        _currentScope.Value = scope;
        WriteWithScope("INFO", $"BEGIN operation '{scope.OperationName}'", null, scope);
        return new OperationScope(this, scope);
    }

    private void Write(string level, string message, Exception? ex)
        => WriteWithScope(level, message, ex, _currentScope.Value);

    private void WriteWithScope(string level, string message, Exception? ex, LogScope? scope)
    {
        var context = $"session={SessionId}";
        if (scope is not null)
        {
            context += $" op={scope.OperationId}:{scope.OperationName}";
            if (scope.ServerId.HasValue) context += $" serverId={scope.ServerId.Value:D}";
            if (!string.IsNullOrWhiteSpace(scope.ServerName)) context += $" serverName=\"{EscapeContext(scope.ServerName!)}\"";
        }

        var line = $"{DateTimeOffset.Now:O} [{level,-5}] [{context}] {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + $"ExceptionType={ex.GetType().FullName}" + Environment.NewLine + ex;
        }

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
                if (scope?.ServerId is Guid serverId)
                {
                    var serverLogRoot = Path.Combine(LogsDirectory, "servers");
                    Directory.CreateDirectory(serverLogRoot);
                    var serverLog = Path.Combine(serverLogRoot, $"server-{serverId:D}.log");
                    File.AppendAllText(serverLog, line + Environment.NewLine);
                }
            }
        }
        catch
        {
            // Logging must never become the reason the manager itself fails.
        }
    }

    private static string EscapeContext(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string GetAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static void CleanupOldLogs(string logsRoot, TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(logsRoot, "manager-*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }
}
