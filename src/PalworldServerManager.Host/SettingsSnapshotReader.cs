using System.Globalization;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Core.Services;
using PalworldServerManager.Core.Settings;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using ServerRef = PalworldServerManager.Core.Authorization.ServerRef;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

// Trusted synchronous Host engine, no RPC or permission bypass. Composition must retain one
// shared instance for the Host lifetime, including both local/peer readers and future save ordering.
internal sealed class SettingsSnapshotReader
{
    private readonly Guid host;
    private readonly IManagedServerSettingsReader files;
    private readonly ServerInventoryRepository inventory;
    private readonly ConfigurationRevisionRepository revisions;
    private readonly SemaphoreSlim order = new(1, 1);
    internal SettingsSnapshotReader(HostDatabase database, Guid host, IManagedServerSettingsReader files)
    {
        this.host = host; this.files = files ?? throw new ArgumentNullException(nameof(files));
        inventory = new(database, host); revisions = new(database, host);
    }
    internal Wire.SettingsSnapshot Read(Wire.ServerRef? requested, Action<ServerRef> authorize,
        Action<SqliteConnection, SqliteTransaction, ServerRef> validateCurrentRead, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize); ArgumentNullException.ThrowIfNull(validateCurrentRead);
        var server = SettingsSchemaReader.Target(requested);
        if (server.AuthoritativeHostId != host) throw new ArgumentException("Local server required.");
        ct.ThrowIfCancellationRequested(); authorize(server);
        order.Wait(ct);
        try
        {
            // The wait never carries a prior permission observation into file access.
            ct.ThrowIfCancellationRequested(); authorize(server);
            var registered = inventory.Read(server, ct) ?? throw new InvalidOperationException("Server is not registered.");
            using var source = files.Read(server.ServerProfileId, registered.InstallPath, ct);
            ct.ThrowIfCancellationRequested();
            var active = Values(PreservedPalworldConfigFile.Parse(source.Active.Span), ct);
            var defaults = source.InstallationDefaults is { } bytes ? Values(PreservedPalworldConfigFile.Parse(bytes.Span), ct) : new Dictionary<string, string>();
            // Validate the full nonsecret projection before any revision metadata effects.
            var result = SettingsContractMapping.Snapshot(new(new HostId(host), server.ServerProfileId), 0, active, defaults);
            var observed = revisions.ObserveContent(new("ServerSettings", new ServerTarget(server)), source.Active.Span, (c, tx) =>
            {
                validateCurrentRead(c, tx, server); inventory.ValidateUnchanged(c, tx, registered, ct);
            }, ct);
            result.Revision = observed.Revision.ToString(CultureInfo.InvariantCulture);
            authorize(server); ct.ThrowIfCancellationRequested(); return result;
        }
        finally { order.Release(); }
    }
    private static Dictionary<string, string> Values(PreservedPalworldConfigFile file, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in file.Keys)
        {
            ct.ThrowIfCancellationRequested();
            var nonsecret = PalworldSettingCatalog.Resolve(key).IsSecret == false;
            if (result.ContainsKey(key))
            {
                if (nonsecret) throw new InvalidDataException("Ambiguous configured settings source.");
                continue;
            }
            if (result.Count >= SettingsWireValidation.MaximumEntries) throw new InvalidDataException("Settings source exceeds the entry bound.");
            // Never retrieve unknown/secret values just to hide them later. Preserve their key metadata only.
            result.Add(key, nonsecret ? PalworldConfigParser.Unquote(file.Get(key)) : string.Empty);
        }
        return result;
    }
}
