using System.ComponentModel;
using System.Globalization;
using System.Text;
using PalworldServerManager.Contracts;

namespace PalworldServerManager.Client.Avalonia.Shell;

// Presentation input only. The future inventory adapter must supply the authenticated local
// Host's authorized inventory; a row, alias or successful selection creates no capability.
public sealed record ShellServer(ServerRef Reference, string Name, string HostName);
public sealed record ShellRow(ServerRef Reference, string Name, string HostName, bool IsLocal,
    string HostAlias, string ServerAlias, string HostDiscriminator)
{
    public string Location => IsLocal ? "This PC" : "Remote";
    public string HostLabel => $"{Location} · {HostName} · {HostAlias} · {HostDiscriminator}";
    public string IdentityDetails => $"{Name}\n{Location} · {HostName}\nHost: {Reference.AuthoritativeHostId.Value:D}\nServer: {Reference.ServerProfileId:D}";
    public string AccessibleName => IdentityDetails.Replace('\n', ' ');
}

// UI-thread owned. The supplied selector revalidates the exact target through the local Host;
// it must not implement remote/direct process or filesystem access. Async replies are versioned.
public sealed class ShellState(HostId localHost, Func<ServerRef, CancellationToken, Task<bool>> revalidate) : INotifyPropertyChanged
{
    public HostId LocalHost { get; } = localHost ?? throw new ArgumentNullException(nameof(localHost));
    private readonly Func<ServerRef, CancellationToken, Task<bool>> _revalidate = revalidate ?? throw new ArgumentNullException(nameof(revalidate));
    private readonly Dictionary<HostId, string> _hosts = [];
    private readonly Dictionary<ServerRef, string> _servers = [];
    private int _nextHost, _nextServer;
    private long _generation;
    public IReadOnlyList<ShellRow> Rows { get; private set; } = Array.Empty<ShellRow>();
    public HostId? Scope { get; private set; } // null: All Servers; LocalHost: This PC
    public IEnumerable<ShellRow> VisibleRows => Rows.Where(row => Scope is null || row.Reference.AuthoritativeHostId == Scope);
    public ServerRef? Focused { get; private set; }
    public ServerRef? Selected { get; private set; }
    public ShellRow? SelectedRow => Rows.SingleOrDefault(row => row.Reference == Selected);
    public ShellRow? FocusedRow => Rows.SingleOrDefault(row => row.Reference == Focused);
    public bool IsSelectionPending { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
    private void Invalidate() { _generation++; IsSelectionPending = false; }
    private static string Display(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A display label is required.");
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            result.Append(Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? "\uFFFD" : rune.ToString());
        return result.ToString();
    }
    public void ReplaceAuthorizedInventory(IReadOnlyList<ShellServer> inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        // Validate and copy all input before changing any state or reserving aliases.
        var values = inventory.Select(item => item is null || item.Reference is null ? throw new ArgumentException("Exact server reference required.") :
            new ShellServer(item.Reference, Display(item.Name), Display(item.HostName))).ToArray();
        if (values.Select(item => item.Reference).Distinct().Count() != values.Length) throw new ArgumentException("Duplicate exact server identity.");
        var hostNames = values.GroupBy(item => item.Reference.AuthoritativeHostId).ToArray();
        if (hostNames.Any(group => group.Select(item => item.HostName).Distinct(StringComparer.Ordinal).Count() != 1))
            throw new ArgumentException("One Host identity has conflicting labels.");
        var hostIds = hostNames.Select(group => group.Key).ToArray();
        string Discriminator(HostId host)
        {
            var full = host.Value.ToString("N"); var length = 8;
            while (length < 32 && hostIds.Any(other => other != host && other.Value.ToString("N")[^length..] == full[^length..])) length += 4;
            return full[^length..];
        }
        foreach (var host in hostIds)
            if (!_hosts.ContainsKey(host)) _hosts.Add(host, host == LocalHost ? "L" : "R" + (++_nextHost).ToString(CultureInfo.InvariantCulture));
        foreach (var item in values)
            if (!_servers.ContainsKey(item.Reference)) _servers.Add(item.Reference, "S" + (++_nextServer).ToString(CultureInfo.InvariantCulture));
        Rows = Array.AsReadOnly(values.Select(item => new ShellRow(item.Reference, item.Name, item.HostName, item.Reference.AuthoritativeHostId == LocalHost,
            _hosts[item.Reference.AuthoritativeHostId], _servers[item.Reference], Discriminator(item.Reference.AuthoritativeHostId))).ToArray());
        if (Scope is not null && Scope != LocalHost && !hostIds.Contains(Scope)) Scope = null;
        if (!VisibleRows.Any(row => row.Reference == Selected)) Selected = null;
        if (!VisibleRows.Any(row => row.Reference == Focused)) Focused = null;
        Invalidate(); Changed();
    }
    public void Show(HostId? scope)
    {
        if (scope is not null && scope != LocalHost && !Rows.Any(row => row.Reference.AuthoritativeHostId == scope))
            throw new ArgumentException("Host is not in the authorized inventory.");
        Scope = scope;
        if (!VisibleRows.Any(row => row.Reference == Selected)) Selected = null;
        if (!VisibleRows.Any(row => row.Reference == Focused)) Focused = null;
        Invalidate(); Changed();
    }
    public void Focus(ServerRef? target)
    {
        if (target is not null && !VisibleRows.Any(row => row.Reference == target)) throw new ArgumentException("Server is not visible.");
        Focused = target; Changed(); // inspection does not change selection or pending validation
    }
    public async Task<bool> SelectAsync(ServerRef target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target); ct.ThrowIfCancellationRequested();
        if (!VisibleRows.Any(row => row.Reference == target)) return false;
        var generation = ++_generation; IsSelectionPending = true; Changed();
        try
        {
            var accepted = await _revalidate(target, ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation || !VisibleRows.Any(row => row.Reference == target)) return false;
            if (!accepted) { if (Selected == target) Selected = null; return false; }
            Selected = target; return true;
        }
        catch
        {
            // An exception/cancellation is not successful revalidation. A stale failing
            // request must still leave a newer selection untouched.
            if (generation == _generation && Selected == target) Selected = null;
            throw;
        }
        finally { if (generation == _generation) { IsSelectionPending = false; Changed(); } }
    }
    public void Disconnect() => ReplaceAuthorizedInventory(Array.Empty<ShellServer>());
}
