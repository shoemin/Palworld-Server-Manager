using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence.Migrations;

// One ordered, contiguous schema migration. Version numbering starts at 1; the runner applies
// migrations ascending from (user_version + 1).
public interface IHostSchemaMigration
{
    int Version { get; }

    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
