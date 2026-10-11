using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class ConfigurationRevisionRepository
{
    // Trusted Host-only observation of owned bytes, not a file reader, authorization permit or
    // save. The caller serializes its read/observe/save sequence and retains the machine lease.
    // Re-read/reconcile before comparing a save token; an observation cannot freeze a file.
    public ConfigurationRevision ObserveContent(EditableResource resource, ReadOnlySpan<byte> content,
        Action validateCurrentRead, CancellationToken ct = default)
    {
        RequireResource(resource); ArgumentNullException.ThrowIfNull(validateCurrentRead); ct.ThrowIfCancellationRequested();
        if (content.Length > 4194308) throw new ArgumentException("Configuration observation exceeds the byte bound.");
        validateCurrentRead();
        ct.ThrowIfCancellationRequested();
        var fingerprint = SHA256.HashData(content);
        try
        {
            using var c = Open(false); using var tx = c.BeginTransaction(deferred: false);
            RequireHost(c, tx); var before = Read(c, tx, resource);
            var observed = ReadObservation(c, tx, resource, before.Revision);
            var unchanged = observed is not null && observed.Value.Revision == before.Revision &&
                CryptographicOperations.FixedTimeEquals(observed.Value.Fingerprint, fingerprint);
            var after = before;
            if (!unchanged)
            {
                var next = checked(before.Revision + 1); var now = time.GetUtcNow();
                if (now.Offset != TimeSpan.Zero) throw new InvalidOperationException("UTC time required.");
                ct.ThrowIfCancellationRequested();
                using (var cmd = Command(c, tx, """
                    INSERT INTO ConfigurationRevisions (ResourceKind,ResourceId,RevisionId,LastModifiedUtc)
                        VALUES ($kind,$id,$next,$now)
                    ON CONFLICT(ResourceKind,ResourceId) DO UPDATE SET RevisionId=$next,LastModifiedUtc=$now
                        WHERE RevisionId=$expected;
                    """, ("$kind", resource.Kind), ("$id", StorageId(resource)), ("$next", next),
                    ("$now", now.ToString("O", CultureInfo.InvariantCulture)), ("$expected", before.Revision)))
                    if (cmd.ExecuteNonQuery() != 1) throw new StaleWriteConflictException();
                using (var cmd = Command(c, tx, """
                    INSERT INTO ConfigurationContentObservations (ResourceKind,ResourceId,ObservedRevision,ContentFingerprint)
                        VALUES ($kind,$id,$next,$fingerprint)
                    ON CONFLICT(ResourceKind,ResourceId) DO UPDATE SET ObservedRevision=$next,ContentFingerprint=$fingerprint;
                    """, ("$kind", resource.Kind), ("$id", StorageId(resource)), ("$next", next), ("$fingerprint", fingerprint)))
                    if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("Configuration observation was not recorded.");
                after = new(resource, next, now);
            }
            // Mandatory even for unchanged bytes: no metadata observation becomes a cached proof.
            validateCurrentRead(); RequireHost(c, tx);
            if (Read(c, tx, resource) != after) throw new InvalidDataException("Configuration revision changed before commit.");
            var final = ReadObservation(c, tx, resource, after.Revision);
            if (final is null || final.Value.Revision != after.Revision ||
                !CryptographicOperations.FixedTimeEquals(final.Value.Fingerprint, fingerprint))
                throw new InvalidDataException("Configuration observation changed before commit.");
            ct.ThrowIfCancellationRequested(); tx.Commit(); return after;
        }
        finally { CryptographicOperations.ZeroMemory(fingerprint); }
    }

    private static (long Revision, byte[] Fingerprint)? ReadObservation(SqliteConnection c, SqliteTransaction tx,
        EditableResource resource, long currentRevision)
    {
        using var cmd = Command(c, tx, """
            SELECT ObservedRevision,ContentFingerprint FROM ConfigurationContentObservations
                WHERE ResourceKind=$kind AND ResourceId=$id;
            """, ("$kind", resource.Kind), ("$id", StorageId(resource)));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetValue(0) is not long revision || revision <= 0 || revision > currentRevision ||
            reader.GetValue(1) is not byte[] { Length: 32 } fingerprint)
            throw new InvalidDataException("Invalid persisted configuration observation.");
        return (revision, fingerprint);
    }
}
