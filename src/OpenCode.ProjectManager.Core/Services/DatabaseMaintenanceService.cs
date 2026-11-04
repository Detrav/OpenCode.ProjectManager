using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenCode.ProjectManager.Core.Models;

namespace OpenCode.ProjectManager.Core.Services;

/// <summary>
/// Reports how much of an OpenCode SQLite file is live data and reclaims the
/// rest with VACUUM. SQLite never shrinks a database on its own: deleted rows
/// leave free pages inside the file, and only VACUUM returns that space to disk.
/// </summary>
public sealed class DatabaseMaintenanceService
{
    private const long HeadroomBytes = 64L * 1024 * 1024;

    public DatabaseSpaceInfo ReadSpaceInfo(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The OpenCode database does not exist.", fullPath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 15
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout = 15000;";
            command.ExecuteNonQuery();
        }

        return new DatabaseSpaceInfo(
            new FileInfo(fullPath).Length,
            ReadPragma(connection, "page_size"),
            ReadPragma(connection, "page_count"),
            ReadPragma(connection, "freelist_count"));
    }

    public DatabaseSpaceInfo Compact(
        string databasePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The OpenCode database does not exist.", fullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var before = ReadSpaceInfo(fullPath);
        progress?.Report(
            $"Compacting database ({DatabaseCandidate.FormatBytes(before.FileSizeBytes)}, " +
            $"{DatabaseCandidate.FormatBytes(before.ReclaimableBytes)} reclaimable)…");

        EnsureFreeSpace(fullPath, before);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        };

        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                // VACUUM builds the compacted copy in a temporary file, so keep
                // that copy on disk instead of risking a multi-gigabyte RAM spike.
                command.CommandText = "PRAGMA busy_timeout = 30000; PRAGMA temp_store = FILE;";
                command.ExecuteNonQuery();
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Checkpointing the write-ahead log…");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Rebuilding the database file with VACUUM (this can take several minutes)…");
            Execute(connection, "VACUUM;");

            progress?.Report("Truncating the write-ahead log…");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException exception)
        {
            throw new OpenCodeDatabaseException(
                "Could not compact the database. Close OpenCode, make sure the file is writable and " +
                "that there is enough free disk space, then try again.",
                exception);
        }

        var after = ReadSpaceInfo(fullPath);
        progress?.Report(
            $"Database compacted: {DatabaseCandidate.FormatBytes(before.FileSizeBytes)} → " +
            $"{DatabaseCandidate.FormatBytes(after.FileSizeBytes)}.");

        return after;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadPragma(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static void EnsureFreeSpace(string databasePath, DatabaseSpaceInfo space)
    {
        try
        {
            // SQLite writes the rebuilt database to a temporary file. It lives in
            // the OS temp directory unless SQLITE_TMPDIR overrides it, so prefer
            // that drive and fall back to the database directory.
            var tempRoot = Path.GetPathRoot(Path.GetTempPath());
            var databaseRoot = Path.GetPathRoot(databasePath);

            var required = Math.Max(space.LiveBytes, 1) + HeadroomBytes;

            foreach (var root in new[] { tempRoot, databaseRoot })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < required)
                {
                    throw new IOException(
                        $"Not enough free space to compact the database. Need about " +
                        $"{DatabaseCandidate.FormatBytes(required)}, but {drive.Name} has " +
                        $"{DatabaseCandidate.FormatBytes(drive.AvailableFreeSpace)} available.");
                }
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception)
        {
            // DriveInfo is not available for every path type (for example UNC paths).
        }
    }
}
