using Microsoft.Data.Sqlite;

namespace OpenCode.ProjectManager.Core.Services;

/// <summary>
/// Creates one consistent SQLite backup per database per application run.
/// The SQLite online backup API is used instead of a file copy so that WAL
/// contents are included and the copy is transactionally consistent.
/// </summary>
public sealed class BackupService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _backups = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Backups
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_backups, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public string? GetExistingBackup(string databasePath)
    {
        lock (_sync)
        {
            return _backups.TryGetValue(Path.GetFullPath(databasePath), out var backup)
                ? backup
                : null;
        }
    }

    public string EnsureBackup(
        string databasePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);

        lock (_sync)
        {
            if (_backups.TryGetValue(fullPath, out var existing))
            {
                return existing;
            }

            progress?.Report("Creating a one-time database backup…");
            var backupPath = CreateBackup(fullPath, progress, cancellationToken);
            _backups[fullPath] = backupPath;
            progress?.Report($"Backup created: {backupPath}");
            return backupPath;
        }
    }

    private static string CreateBackup(
        string databasePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("The OpenCode database does not exist.", databasePath);
        }

        var backupDirectory = DatabaseLocator.GetDefaultBackupDirectory(databasePath);
        Directory.CreateDirectory(backupDirectory);

        EnsureFreeSpace(databasePath, backupDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(backupDirectory, $"opencode-{stamp}.db");
        var suffix = 1;

        while (File.Exists(backupPath) || File.Exists(backupPath + ".partial"))
        {
            backupPath = Path.Combine(backupDirectory, $"opencode-{stamp}-{suffix++}.db");
        }

        // Write to a temporary name and only rename after verification, so an
        // interrupted process never leaves a file that looks like a valid backup.
        var partialPath = backupPath + ".partial";

        cancellationToken.ThrowIfCancellationRequested();

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        };

        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = partialPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        };

        try
        {
            using var source = new SqliteConnection(sourceBuilder.ToString());
            source.Open();

            using (var command = source.CreateCommand())
            {
                command.CommandText = "PRAGMA busy_timeout = 30000;";
                command.ExecuteNonQuery();
            }

            using var destination = new SqliteConnection(destinationBuilder.ToString());
            destination.Open();

            progress?.Report("Copying database pages (this can take a while for large databases)…");
            source.BackupDatabase(destination);
        }
        catch (Exception)
        {
            TryDelete(partialPath);
            throw;
        }

        try
        {
            VerifyBackup(partialPath);
            File.Move(partialPath, backupPath);
        }
        catch (Exception)
        {
            TryDelete(partialPath);
            throw;
        }

        var size = new FileInfo(backupPath).Length;
        progress?.Report($"Backup verified: {backupPath} ({Models.DatabaseCandidate.FormatBytes(size)})");
        return backupPath;
    }

    private static void EnsureFreeSpace(string databasePath, string backupDirectory)
    {
        try
        {
            var required = new FileInfo(databasePath).Length;
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = new FileInfo(databasePath + suffix);
                if (sidecar.Exists)
                {
                    required += sidecar.Length;
                }
            }

            // Keep a little headroom for the database header and filesystem metadata.
            required += 64L * 1024 * 1024;

            var root = Path.GetPathRoot(backupDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < required)
            {
                throw new IOException(
                    $"Not enough free space for the backup. Need about {Models.DatabaseCandidate.FormatBytes(required)}, " +
                    $"but {drive.Name} has {Models.DatabaseCandidate.FormatBytes(drive.AvailableFreeSpace)} available.");
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

    private static void VerifyBackup(string backupPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master;";
        _ = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }
}
