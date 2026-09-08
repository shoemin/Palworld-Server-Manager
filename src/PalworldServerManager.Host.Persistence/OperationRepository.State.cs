using System.Globalization;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.Host.Persistence;

public sealed class OperationConflictException() : Exception("The operation identity or lock scope is already in use.");
public sealed class OperationRecoveryRequiredException() : Exception("Stored operation state requires explicit recovery.");

// Trusted Host persistence, not an RPC or authorization policy. The caller retains the
// machine lease. Authority callbacks are read-only and cannot end the supplied transaction.
public sealed partial class OperationRepository
{
    private readonly HostDatabase database;
    private readonly Guid hostId;
    private readonly TimeProvider time;
    private readonly Dictionary<string, OperationDefinition> definitions = new(StringComparer.Ordinal);

    public OperationRepository(HostDatabase database, Guid hostId, IEnumerable<OperationDefinition> definitions, TimeProvider? timeProvider = null)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
        time = timeProvider ?? TimeProvider.System; ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
            if (definition is null || !this.definitions.TryAdd(definition.Kind, definition))
                throw new ArgumentException("A unique trusted definition for each operation kind is required.");
    }

    public OperationStateSnapshot Read(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var c = Open(true); using var tx = c.BeginTransaction(deferred: true);
        RequireHost(c, tx); var state = Read(c, tx); ct.ThrowIfCancellationRequested(); return state;
    }

    private OperationStateSnapshot Read(SqliteConnection c, SqliteTransaction tx)
    {
        long revision;
        using (var cmd = Command(c, tx, "SELECT Revision FROM OperationStateRevision WHERE Id=1 AND typeof(Revision)='integer' AND Revision>=0;"))
            revision = cmd.ExecuteScalar() is long value ? value : throw Corrupt();
        var operations = new List<OperationObservation>(); var locks = new List<DurableOperationLock>();
        using (var cmd = Command(c, tx, """
            SELECT OperationId,Kind,TargetKind,TargetHostId,TargetServerProfileId,Phase,IsTerminal,RecoveryDisposition,
                LockRequirement,PolicyFingerprint,RecordRevision,StartedUtc,LastHeartbeatUtc FROM OperationRecords ORDER BY OperationId;
            """))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                OperationTarget target = Text(r, 2) switch
                {
                    "HostTarget" when r.IsDBNull(4) => new HostTarget(Identifier(Text(r, 3))),
                    "ServerTarget" when !r.IsDBNull(4) => new ServerTarget(new(Identifier(Text(r, 3)), Identifier(Text(r, 4)))),
                    _ => throw Corrupt()
                };
                if (target.AuthoritativeHostId != hostId) throw Corrupt();
                if (r.GetValue(6) is not long terminal || terminal is < 0 or > 1 || r.GetValue(10) is not long recordRevision || recordRevision < 0) throw Corrupt();
                var record = new DurableOperation(Identifier(Text(r, 0)), Name(Text(r, 1)), target, Name(Text(r, 5)), terminal == 1,
                    r.IsDBNull(7) ? null : Known<RecoveryDisposition>(Text(r, 7)),
                    r.IsDBNull(8) ? null : Known<LockRequirement>(Text(r, 8)),
                    r.IsDBNull(9) ? null : Text(r, 9), recordRevision, Utc(Text(r, 11)), r.IsDBNull(12) ? null : Utc(Text(r, 12)));
                operations.Add(new(record, PolicyMatches(record)));
            }
        }
        using (var cmd = Command(c, tx, """
            SELECT OperationLockId,ScopeKind,ScopeHostId,ScopeServerProfileId,OperationKind,OwningOperationId,AcquiredUtc
                FROM OperationLocks ORDER BY OperationLockId;
            """))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                OperationLockScope scope = Text(r, 1) switch
                {
                    "HostScope" when r.IsDBNull(3) => new HostScope(Identifier(Text(r, 2))),
                    "ServerScope" when !r.IsDBNull(3) => new ServerScope(new(Identifier(Text(r, 2)), Identifier(Text(r, 3)))),
                    _ => throw Corrupt()
                };
                if (scope.AuthoritativeHostId != hostId) throw Corrupt();
                locks.Add(new(Identifier(Text(r, 0)), scope, Name(Text(r, 4)), Identifier(Text(r, 5)), Utc(Text(r, 6))));
            }
        }
        var recovery = operations.Any(o => !o.Operation.IsTerminal && !o.PolicyIsCurrent);
        var ids = operations.Select(o => o.Operation.OperationId).ToHashSet();
        recovery |= locks.Any(l => !ids.Contains(l.OwningOperationId));
        foreach (var observation in operations)
        {
            var op = observation.Operation; var owned = locks.Where(l => l.OwningOperationId == op.OperationId).ToArray();
            if (op.IsTerminal) { recovery |= owned.Length != 0; continue; }
            if (!observation.PolicyIsCurrent) continue;
            var scope = OperationLockPolicy.RequiredScope(op.Target, op.LockRequirement!.Value);
            recovery |= scope is null ? owned.Length != 0 : owned.Length != 1 ||
                owned[0].Scope != scope || owned[0].Kind != op.Kind || owned[0].AcquiredUtc != op.StartedUtc;
        }
        for (var i = 0; i < locks.Count; i++) for (var j = i + 1; j < locks.Count; j++)
            recovery |= OperationLockPolicy.Conflicts(locks[i].Scope, locks[j].Scope);
        return new(revision, operations, locks, recovery);
    }

    private bool PolicyMatches(DurableOperation op)
        => definitions.TryGetValue(op.Kind, out var definition) && op.PolicyFingerprint == definition.Fingerprint &&
            op.LockRequirement == definition.LockRequirement && op.Revision > 0 &&
            !(op.Target is HostTarget && definition.LockRequirement == LockRequirement.ServerExclusive) &&
            definition.Phases.TryGetValue(op.Phase, out var phase) && op.IsTerminal == phase.IsTerminal && op.Recovery == phase.Recovery;

    private void RequireHost(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = Command(c, tx, """
            SELECT COUNT(*) FROM HostIdentity WHERE Id=1 AND HostId=$host AND HostBootstrapState='Initialized'
                AND (SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active')=1;
            """, ("$host", hostId.ToString("D")));
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1) throw new InvalidOperationException("Current initialized Host required.");
    }

    private SqliteConnection Open(bool readOnly)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd;
    }
    private static int Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    { using var cmd = Command(c, tx, sql, values); return cmd.ExecuteNonQuery(); }
    private static string Text(SqliteDataReader reader, int column) => reader.GetValue(column) is string text ? text : throw Corrupt();
    private static Guid Identifier(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw Corrupt();
    private static string Id(Guid value) => value != Guid.Empty ? value.ToString("D") : throw new ArgumentException("Operation identity required.");
    private static string Name(string value) => value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : throw Corrupt();
    private static T Known<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == value ? parsed : throw Corrupt();
    private static DateTimeOffset Utc(string value) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var utc) && utc.Offset == TimeSpan.Zero ? utc : throw Corrupt();
    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static InvalidDataException Corrupt() => new("Stored operation state requires recovery inspection.");
    private static void RequireConsistent(OperationStateSnapshot state)
    { if (state.HasUnqualifiedState) throw new OperationRecoveryRequiredException(); }
}
