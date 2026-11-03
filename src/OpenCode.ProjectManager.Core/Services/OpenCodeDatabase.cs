using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenCode.ProjectManager.Core.Models;

namespace OpenCode.ProjectManager.Core.Services;

public sealed class OpenCodeDatabaseException : Exception
{
    public OpenCodeDatabaseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Read and destructive operations against an OpenCode 2 SQLite database.
/// The delete routine mirrors the upstream V2 cascade: the project row is removed,
/// foreign keys cascade sessions/messages/worktrees, then session event streams,
/// jobs and orphaned blobs are cleaned up manually.
/// </summary>
public sealed class OpenCodeDatabase
{
    private const int DeleteBatchSize = 400;

    private readonly string _databasePath;

    public OpenCodeDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public DatabaseSnapshot Load()
    {
        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);
        return Load(connection);
    }

    public DatabaseSchemaValidation ValidateSchema()
    {
        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);
        return ValidateSchema(connection);
    }

    public ProjectDetails LoadDetails(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);
        var snapshot = Load(connection);
        var project = snapshot.Projects.FirstOrDefault(item =>
            string.Equals(item.Id, projectId, StringComparison.Ordinal));

        if (project is null)
        {
            throw new OpenCodeDatabaseException($"Project '{projectId}' was not found in the database.");
        }

        var tables = GetTables(connection);
        var worktrees = ReadWorktrees(connection, projectId, tables);
        var directories = ReadLegacyDirectories(connection, projectId, tables);

        var messageCount = CountForProject(
            connection, tables, "session_message", "session_v2", projectId);
        var legacyMessageCount = CountForProject(
            connection, tables, "message", "session", projectId);

        return new ProjectDetails(
            project,
            worktrees,
            directories,
            messageCount,
            legacyMessageCount,
            CountSimple(connection, tables, "permission", "project_id", projectId),
            CountSchedule(connection, tables, projectId),
            CountJobs(connection, tables, projectId),
            CountForProject(connection, tables, "blob_ref", "session_v2", projectId),
            CountEventSequences(connection, tables, projectId));
    }

    public DeleteResult DeleteProject(
        string projectId,
        string? backupPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        using var connection = OpenConnection(SqliteOpenMode.ReadWrite);

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_keys;";
            var enabled = Convert.ToInt64(check.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            if (enabled != 1)
            {
                throw new OpenCodeDatabaseException(
                    "SQLite foreign key enforcement is disabled. Refusing to delete because the cascade would be incomplete.");
            }
        }

        EnsureCompatible(connection);

        var tables = GetTables(connection);
        var project = ReadProject(connection, tables, projectId)
            ?? throw new OpenCodeDatabaseException($"Project '{projectId}' no longer exists.");

        if (project.IsGlobal)
        {
            throw new OpenCodeDatabaseException("The built-in global project cannot be deleted.");
        }

        progress?.Report($"Preparing to delete “{project.DisplayName}”…");

        var sessionIds = ReadSessionIds(connection, tables, projectId);
        var legacySessionIds = ReadLegacySessionIds(connection, tables, projectId);
        var allSessionIds = sessionIds
            .Concat(legacySessionIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var worktreeCount = CountSimple(connection, tables, "worktree", "project_id", projectId);
        var legacyDirectoryCount = CountSimple(connection, tables, "project_directory", "project_id", projectId);
        var messageCount = CountForProject(connection, tables, "session_message", "session_v2", projectId) +
                           CountForProject(connection, tables, "message", "session", projectId);
        var jobCount = CountJobs(connection, tables, projectId);

        using var transaction = connection.BeginTransaction();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report("Deleting project row and cascading sessions…");
            using (var deleteProject = connection.CreateCommand())
            {
                deleteProject.Transaction = transaction;
                deleteProject.CommandText = "DELETE FROM project WHERE id = $id;";
                deleteProject.Parameters.AddWithValue("$id", projectId);

                var affected = deleteProject.ExecuteNonQuery();
                if (affected == 0)
                {
                    throw new OpenCodeDatabaseException($"Project '{projectId}' no longer exists.");
                }
            }

            var eventSequences = 0;
            if (allSessionIds.Length > 0)
            {
                if (tables.Contains("job"))
                {
                    progress?.Report($"Removing job rows for {allSessionIds.Length:N0} sessions…");
                    DeleteInBatches(connection, transaction, "job", "session_id", allSessionIds, cancellationToken);
                    DeleteInBatches(connection, transaction, "job", "child_session_id", allSessionIds, cancellationToken);
                }

                if (tables.Contains("event_sequence"))
                {
                    var eventIds = allSessionIds.Append(projectId).Distinct(StringComparer.Ordinal).ToArray();
                    progress?.Report($"Removing event streams for {eventIds.Length:N0} aggregates…");
                    eventSequences = DeleteInBatches(
                        connection, transaction, "event_sequence", "aggregate_id", eventIds, cancellationToken);
                }
            }
            else if (tables.Contains("event_sequence"))
            {
                eventSequences = DeleteInBatches(
                    connection, transaction, "event_sequence", "aggregate_id", [projectId], cancellationToken);
            }

            var orphanedBlobs = 0;
            if (tables.Contains("blob") && tables.Contains("blob_ref"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Reclaiming orphaned media blobs…");
                using var deleteBlobs = connection.CreateCommand();
                deleteBlobs.Transaction = transaction;
                deleteBlobs.CommandText = """
                    DELETE FROM blob
                    WHERE NOT EXISTS (
                        SELECT 1 FROM blob_ref WHERE blob_ref.hash = blob.hash
                    );
                    """;
                orphanedBlobs = deleteBlobs.ExecuteNonQuery();
            }

            progress?.Report("Committing transaction…");
            transaction.Commit();

            return new DeleteResult(
                project.Id,
                project.DisplayName,
                sessionIds.Count,
                legacySessionIds.Count,
                worktreeCount,
                legacyDirectoryCount,
                messageCount,
                eventSequences,
                jobCount,
                orphanedBlobs,
                backupPath);
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
                // The transaction may already be rolled back by SQLite.
            }

            throw;
        }
    }

    /// <summary>
    /// Deletes every session in a project while keeping the project row, its worktrees,
    /// its permissions and its directories. Intended for the built-in global project.
    /// </summary>
    public DeleteResult DeleteSessions(
        string projectId,
        string? backupPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        using var connection = OpenConnection(SqliteOpenMode.ReadWrite);

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        EnsureCompatible(connection);

        var tables = GetTables(connection);
        var project = ReadProject(connection, tables, projectId)
            ?? throw new OpenCodeDatabaseException($"Project '{projectId}' no longer exists.");

        var sessionIds = ReadSessionIds(connection, tables, projectId);
        var legacySessionIds = ReadLegacySessionIds(connection, tables, projectId);
        var allSessionIds = sessionIds
            .Concat(legacySessionIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var messageCount = CountForProject(connection, tables, "session_message", "session_v2", projectId) +
                           CountForProject(connection, tables, "message", "session", projectId);
        var jobCount = CountJobs(connection, tables, projectId);

        if (allSessionIds.Length == 0)
        {
            progress?.Report("No sessions to delete.");
            return new DeleteResult(
                project.Id,
                project.DisplayName,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                backupPath);
        }

        progress?.Report($"Deleting {allSessionIds.Length:N0} session(s) from “{project.DisplayName}”…");

        using var transaction = connection.BeginTransaction();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (var deleteV2 = connection.CreateCommand())
            {
                deleteV2.Transaction = transaction;
                deleteV2.CommandText = "DELETE FROM session_v2 WHERE project_id = $id;";
                deleteV2.Parameters.AddWithValue("$id", projectId);
                deleteV2.ExecuteNonQuery();
            }

            if (tables.Contains("session"))
            {
                using var deleteV1 = connection.CreateCommand();
                deleteV1.Transaction = transaction;
                deleteV1.CommandText = "DELETE FROM session WHERE project_id = $id;";
                deleteV1.Parameters.AddWithValue("$id", projectId);
                deleteV1.ExecuteNonQuery();
            }

            if (tables.Contains("job"))
            {
                progress?.Report($"Removing job rows for {allSessionIds.Length:N0} sessions…");
                DeleteInBatches(connection, transaction, "job", "session_id", allSessionIds, cancellationToken);
                DeleteInBatches(connection, transaction, "job", "child_session_id", allSessionIds, cancellationToken);
            }

            var eventSequences = 0;
            if (tables.Contains("event_sequence"))
            {
                progress?.Report($"Removing session event streams for {allSessionIds.Length:N0} sessions…");
                eventSequences = DeleteInBatches(
                    connection, transaction, "event_sequence", "aggregate_id", allSessionIds, cancellationToken);
            }

            var orphanedBlobs = 0;
            if (tables.Contains("blob") && tables.Contains("blob_ref"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Reclaiming orphaned media blobs…");
                using var deleteBlobs = connection.CreateCommand();
                deleteBlobs.Transaction = transaction;
                deleteBlobs.CommandText = """
                    DELETE FROM blob
                    WHERE NOT EXISTS (
                        SELECT 1 FROM blob_ref WHERE blob_ref.hash = blob.hash
                    );
                    """;
                orphanedBlobs = deleteBlobs.ExecuteNonQuery();
            }

            progress?.Report("Committing transaction…");
            transaction.Commit();

            return new DeleteResult(
                project.Id,
                project.DisplayName,
                sessionIds.Count,
                legacySessionIds.Count,
                0,
                0,
                messageCount,
                eventSequences,
                jobCount,
                orphanedBlobs,
                backupPath);
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception)
            {
                // The transaction may already be rolled back by SQLite.
            }

            throw;
        }
    }

    private DatabaseSnapshot Load(SqliteConnection connection)
    {
        var validation = ValidateSchema(connection);
        if (!validation.IsCompatible)
        {
            throw new OpenCodeDatabaseException(
                "The database schema is not compatible with this application.\n\n" +
                validation.ErrorSummary +
                "\n\nOpen the database once with a current OpenCode 2 build to apply migrations, or choose another file.");
        }

        var tables = GetTables(connection);
        var columns = GetColumns(connection, "project");
        var hasLegacySessions = tables.Contains("session");
        var hasV2Sessions = tables.Contains("session_v2");
        var hasArchived = columns.Contains("time_archived");
        var hasWorktrees = tables.Contains("worktree");
        var hasLegacyDirectories = tables.Contains("project_directory");

        string Column(string name, string fallback = "NULL") =>
            columns.Contains(name) ? $"p.{name}" : fallback;

        var sessionV2Expression = hasV2Sessions
            ? "(SELECT COUNT(*) FROM session_v2 s WHERE s.project_id = p.id)"
            : "0";

        var sessionV1Expression = hasLegacySessions
            ? "(SELECT COUNT(*) FROM session s WHERE s.project_id = p.id)"
            : "0";

        var worktreeExpression = hasWorktrees
            ? "(SELECT COUNT(*) FROM worktree w WHERE w.project_id = p.id)"
            : "0";

        var directoryExpression = hasLegacyDirectories
            ? "(SELECT COUNT(*) FROM project_directory d WHERE d.project_id = p.id)"
            : "0";

        var activeExpression = Column("time_active", "0");

        var sql = $"""
            SELECT
                p.id,
                p.worktree,
                {Column("name")} AS name,
                {Column("vcs")} AS vcs,
                {Column("time_created", "0")} AS time_created,
                {Column("time_updated", "0")} AS time_updated,
                {Column("time_active")} AS time_active,
                {Column("time_archived")} AS time_archived,
                {Column("sandboxes", "'[]'")} AS sandboxes,
                {Column("commands")} AS commands,
                {sessionV2Expression} AS sessions_v2,
                {sessionV1Expression} AS sessions_v1,
                {worktreeExpression} AS worktrees,
                {directoryExpression} AS legacy_directories
            FROM project p
            ORDER BY COALESCE({activeExpression}, 0) DESC, p.id ASC;
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var projects = new List<ProjectRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var canonical = reader.GetString(1);
            projects.Add(new ProjectRecord
            {
                Id = reader.GetString(0),
                CanonicalPath = canonical,
                Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                Vcs = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                UpdatedAt = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                ActiveAt = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                ArchivedAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Sandboxes = ProjectRecord.ParseSandboxes(reader.IsDBNull(8) ? null : reader.GetString(8)),
                StartupCommand = ProjectRecord.ParseStartupCommand(reader.IsDBNull(9) ? null : reader.GetString(9)),
                SessionCount = Convert.ToInt32(reader.GetInt64(10), CultureInfo.InvariantCulture),
                LegacySessionCount = Convert.ToInt32(reader.GetInt64(11), CultureInfo.InvariantCulture),
                WorktreeCount = Convert.ToInt32(reader.GetInt64(12), CultureInfo.InvariantCulture),
                LegacyDirectoryCount = Convert.ToInt32(reader.GetInt64(13), CultureInfo.InvariantCulture),
                DirectoryExists = DirectoryExists(canonical)
            });
        }

        var info = new FileInfo(_databasePath);
        var warning = validation.Warnings.Count == 0 ? null : validation.WarningSummary;

        return new DatabaseSnapshot(
            _databasePath,
            true,
            hasV2Sessions,
            hasLegacySessions,
            hasArchived,
            projects,
            warning)
        {
            FileSizeBytes = info.Exists ? info.Length : 0,
            LastWriteTime = info.Exists ? info.LastWriteTimeUtc : null
        };
    }

    private ProjectRecord? ReadProject(SqliteConnection connection, HashSet<string> tables, string projectId)
    {
        var columns = GetColumns(connection, "project");
        string Column(string name, string fallback = "NULL") =>
            columns.Contains(name) ? $"p.{name}" : fallback;

        var hasLegacySessions = tables.Contains("session");
        var hasV2Sessions = tables.Contains("session_v2");

        var sql = $"""
            SELECT
                p.id,
                p.worktree,
                {Column("name")} AS name,
                {Column("vcs")} AS vcs,
                {Column("time_created", "0")} AS time_created,
                {Column("time_updated", "0")} AS time_updated,
                {Column("time_active")} AS time_active,
                {Column("time_archived")} AS time_archived,
                {Column("sandboxes", "'[]'")} AS sandboxes,
                {Column("commands")} AS commands,
                {(hasV2Sessions ? "(SELECT COUNT(*) FROM session_v2 s WHERE s.project_id = p.id)" : "0")} AS sessions_v2,
                {(hasLegacySessions ? "(SELECT COUNT(*) FROM session s WHERE s.project_id = p.id)" : "0")} AS sessions_v1,
                {(tables.Contains("worktree") ? "(SELECT COUNT(*) FROM worktree w WHERE w.project_id = p.id)" : "0")} AS worktrees,
                {(tables.Contains("project_directory") ? "(SELECT COUNT(*) FROM project_directory d WHERE d.project_id = p.id)" : "0")} AS legacy_directories
            FROM project p
            WHERE p.id = $id;
            """;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", projectId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var canonical = reader.GetString(1);
        return new ProjectRecord
        {
            Id = reader.GetString(0),
            CanonicalPath = canonical,
            Name = reader.IsDBNull(2) ? null : reader.GetString(2),
            Vcs = reader.IsDBNull(3) ? null : reader.GetString(3),
            CreatedAt = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
            UpdatedAt = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
            ActiveAt = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            ArchivedAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            Sandboxes = ProjectRecord.ParseSandboxes(reader.IsDBNull(8) ? null : reader.GetString(8)),
            StartupCommand = ProjectRecord.ParseStartupCommand(reader.IsDBNull(9) ? null : reader.GetString(9)),
            SessionCount = Convert.ToInt32(reader.GetInt64(10), CultureInfo.InvariantCulture),
            LegacySessionCount = Convert.ToInt32(reader.GetInt64(11), CultureInfo.InvariantCulture),
            WorktreeCount = Convert.ToInt32(reader.GetInt64(12), CultureInfo.InvariantCulture),
            LegacyDirectoryCount = Convert.ToInt32(reader.GetInt64(13), CultureInfo.InvariantCulture),
            DirectoryExists = DirectoryExists(canonical)
        };
    }

    private static List<string> ReadSessionIds(
        SqliteConnection connection,
        HashSet<string> tables,
        string projectId)
    {
        if (!tables.Contains("session_v2"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM session_v2 WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        return ReadStrings(command);
    }

    private static List<string> ReadLegacySessionIds(
        SqliteConnection connection,
        HashSet<string> tables,
        string projectId)
    {
        if (!tables.Contains("session"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM session WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        return ReadStrings(command);
    }

    private static List<string> ReadStrings(SqliteCommand command)
    {
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }

    private static IReadOnlyList<WorktreeRecord> ReadWorktrees(
        SqliteConnection connection,
        string projectId,
        HashSet<string> tables)
    {
        if (!tables.Contains("worktree"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT directory, strategy
            FROM worktree
            WHERE project_id = $id
            ORDER BY directory;
            """;
        command.Parameters.AddWithValue("$id", projectId);

        var worktrees = new List<WorktreeRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            worktrees.Add(new WorktreeRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return worktrees;
    }

    private static IReadOnlyList<ProjectDirectoryRecord> ReadLegacyDirectories(
        SqliteConnection connection,
        string projectId,
        HashSet<string> tables)
    {
        if (!tables.Contains("project_directory"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT directory, type, strategy
            FROM project_directory
            WHERE project_id = $id
            ORDER BY directory;
            """;
        command.Parameters.AddWithValue("$id", projectId);

        var directories = new List<ProjectDirectoryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            directories.Add(new ProjectDirectoryRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return directories;
    }

    private static int CountSimple(
        SqliteConnection connection,
        HashSet<string> tables,
        string table,
        string column,
        string projectId)
    {
        if (!tables.Contains(table))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int CountForProject(
        SqliteConnection connection,
        HashSet<string> tables,
        string table,
        string sessionTable,
        string projectId)
    {
        if (!tables.Contains(table) || !tables.Contains(sessionTable))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {table}
            WHERE session_id IN (SELECT id FROM {sessionTable} WHERE project_id = $id);
            """;
        command.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int CountSchedule(
        SqliteConnection connection,
        HashSet<string> tables,
        string projectId)
    {
        if (!tables.Contains("schedule") || !tables.Contains("session_v2"))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM schedule
            WHERE session_id IN (SELECT id FROM session_v2 WHERE project_id = $id);
            """;
        command.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int CountJobs(
        SqliteConnection connection,
        HashSet<string> tables,
        string projectId)
    {
        if (!tables.Contains("job"))
        {
            return 0;
        }

        var sessionV2 = tables.Contains("session_v2")
            ? "SELECT id FROM session_v2 WHERE project_id = $id"
            : "SELECT NULL WHERE 0";
        var sessionV1 = tables.Contains("session")
            ? "SELECT id FROM session WHERE project_id = $id"
            : "SELECT NULL WHERE 0";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM job
            WHERE session_id IN ({sessionV2})
               OR session_id IN ({sessionV1})
               OR child_session_id IN ({sessionV2})
               OR child_session_id IN ({sessionV1});
            """;
        command.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int CountEventSequences(
        SqliteConnection connection,
        HashSet<string> tables,
        string projectId)
    {
        if (!tables.Contains("event_sequence"))
        {
            return 0;
        }

        var sessionV2 = tables.Contains("session_v2")
            ? "SELECT id FROM session_v2 WHERE project_id = $id"
            : "SELECT NULL WHERE 0";
        var sessionV1 = tables.Contains("session")
            ? "SELECT id FROM session WHERE project_id = $id"
            : "SELECT NULL WHERE 0";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM event_sequence
            WHERE aggregate_id = $id
               OR aggregate_id IN ({sessionV2})
               OR aggregate_id IN ({sessionV1});
            """;
        command.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static int DeleteInBatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        IReadOnlyCollection<string> values,
        CancellationToken cancellationToken)
    {
        var total = 0;

        foreach (var batch in values.Chunk(DeleteBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            var parameterNames = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameterNames[index] = "$p" + index.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameterNames[index], batch[index]);
            }

            command.CommandText =
                $"DELETE FROM {table} WHERE {column} IN ({string.Join(", ", parameterNames)});";
            total += command.ExecuteNonQuery();
        }

        return total;
    }

    private static DatabaseSchemaValidation ValidateSchema(SqliteConnection connection)
    {
        var tables = GetTables(connection);
        var errors = new List<string>();
        var warnings = new List<string>();
        var columnCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> Columns(string table)
        {
            if (!columnCache.TryGetValue(table, out var columns))
            {
                columns = GetColumns(connection, table);
                columnCache[table] = columns;
            }

            return columns;
        }

        void RequireColumns(string table, params string[] required)
        {
            if (!tables.Contains(table))
            {
                return;
            }

            var columns = Columns(table);
            foreach (var column in required)
            {
                if (!columns.Contains(column))
                {
                    errors.Add($"Table '{table}' is missing required column '{column}'.");
                }
            }
        }

        void RequireCascade(string table, string column, string targetTable, string targetColumn)
        {
            if (!tables.Contains(table) || !tables.Contains(targetTable))
            {
                return;
            }

            if (!HasCascade(connection, table, column, targetTable, targetColumn))
            {
                errors.Add(
                    $"Table '{table}' is missing foreign key '{column} -> {targetTable}.{targetColumn}' with ON DELETE CASCADE.");
            }
        }

        if (!tables.Contains("project"))
        {
            errors.Add("Missing required table 'project'. This does not look like an OpenCode database.");
        }

        if (!tables.Contains("session_v2"))
        {
            errors.Add("Missing required table 'session_v2'. OpenCode 1 databases are not supported.");
        }

        if (errors.Count > 0)
        {
            return new DatabaseSchemaValidation(errors, warnings);
        }

        var requiredTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = ["id", "worktree", "time_created", "time_updated", "time_active", "time_archived", "sandboxes"],
            ["session_v2"] = ["id", "project_id"],
            ["session_message"] = ["id", "session_id"],
            ["session_part"] = ["message_id", "session_id"],
            ["worktree"] = ["project_id", "directory", "strategy"],
            ["project_directory"] = ["project_id", "directory", "type", "strategy"],
            ["permission"] = ["project_id"],
            ["schedule"] = ["session_id"],
            ["job"] = ["session_id", "child_session_id"],
            ["blob"] = ["hash"],
            ["blob_ref"] = ["session_id", "hash"],
            ["event_sequence"] = ["aggregate_id"],
            ["event"] = ["aggregate_id"],
            ["instruction_entry"] = ["session_id"],
            ["instruction_state"] = ["session_id"],
            ["session_inbox"] = ["session_id"]
        };

        foreach (var (table, columns) in requiredTables)
        {
            if (!tables.Contains(table))
            {
                errors.Add($"Missing required table '{table}'.");
                continue;
            }

            RequireColumns(table, columns);
        }

        var optionalLegacyTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["session"] = ["id", "project_id"],
            ["message"] = ["session_id"],
            ["part"] = ["message_id", "session_id"],
            ["todo"] = ["session_id"],
            ["session_share"] = ["session_id"]
        };

        foreach (var (table, columns) in optionalLegacyTables)
        {
            RequireColumns(table, columns);
        }

        if (tables.Contains("message") && !tables.Contains("session"))
        {
            errors.Add("Table 'message' exists but the legacy 'session' table is missing.");
        }

        if (tables.Contains("part") && !tables.Contains("message"))
        {
            errors.Add("Table 'part' exists but the legacy 'message' table is missing.");
        }

        if (tables.Contains("todo") && !tables.Contains("session"))
        {
            errors.Add("Table 'todo' exists but the legacy 'session' table is missing.");
        }

        if (tables.Contains("session_share") && !tables.Contains("session"))
        {
            errors.Add("Table 'session_share' exists but the legacy 'session' table is missing.");
        }

        RequireCascade("session_v2", "project_id", "project", "id");
        RequireCascade("session_message", "session_id", "session_v2", "id");
        RequireCascade("session_part", "session_id", "session_v2", "id");
        RequireCascade("worktree", "project_id", "project", "id");
        RequireCascade("project_directory", "project_id", "project", "id");
        RequireCascade("permission", "project_id", "project", "id");
        RequireCascade("schedule", "session_id", "session_v2", "id");
        RequireCascade("blob_ref", "session_id", "session_v2", "id");
        RequireCascade("event", "aggregate_id", "event_sequence", "aggregate_id");
        RequireCascade("instruction_entry", "session_id", "session_v2", "id");
        RequireCascade("instruction_state", "session_id", "session_v2", "id");
        RequireCascade("session_inbox", "session_id", "session_v2", "id");

        // Legacy tables are optional, but when present they must cascade, otherwise
        // deleting a project/session would leave rows behind.
        RequireCascade("session", "project_id", "project", "id");
        RequireCascade("message", "session_id", "session", "id");
        RequireCascade("part", "message_id", "message", "id");
        RequireCascade("todo", "session_id", "session", "id");
        RequireCascade("session_share", "session_id", "session", "id");

        if (tables.Contains("blob_ref") &&
            !HasCascade(connection, "blob_ref", "hash", "blob", "hash"))
        {
            warnings.Add("Foreign key 'blob_ref.hash -> blob.hash' does not use ON DELETE CASCADE; blob cleanup is still safe.");
        }

        if (tables.Contains("session_part") &&
            !HasCascade(connection, "session_part", "message_id", "session_message", "id"))
        {
            warnings.Add("Foreign key 'session_part.message_id -> session_message.id' does not use ON DELETE CASCADE; session cleanup still removes parts by session_id.");
        }

        return new DatabaseSchemaValidation(errors, warnings);
    }

    private static void EnsureCompatible(SqliteConnection connection)
    {
        var validation = ValidateSchema(connection);
        if (!validation.IsCompatible)
        {
            throw new OpenCodeDatabaseException(
                "The database schema is not compatible with this application.\n\n" +
                validation.ErrorSummary +
                "\n\nOpen the database once with a current OpenCode 2 build to apply migrations, or choose another file.");
        }
    }

    private static bool HasCascade(
        SqliteConnection connection,
        string table,
        string column,
        string targetTable,
        string targetColumn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table.Replace("\"", "\"\"")}\");";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // PRAGMA foreign_key_list columns: id, seq, table, from, to, on_update, on_delete, match.
            var target = reader.IsDBNull(2) ? null : reader.GetString(2);
            var from = reader.IsDBNull(3) ? null : reader.GetString(3);
            var to = reader.IsDBNull(4) ? null : reader.GetString(4);
            var onDelete = reader.IsDBNull(6) ? null : reader.GetString(6);

            if (string.Equals(target, targetTable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(from, column, StringComparison.OrdinalIgnoreCase) &&
                (to is null || string.Equals(to, targetColumn, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(onDelete, "CASCADE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> GetTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                tables.Add(reader.GetString(0));
            }
        }

        return tables;
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var display = ProjectRecord.ToDisplayPath(path);
            return Directory.Exists(display) || File.Exists(display);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private SqliteConnection OpenConnection(SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 15
        };

        var connection = new SqliteConnection(builder.ToString());

        try
        {
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = mode == SqliteOpenMode.ReadOnly
                ? "PRAGMA busy_timeout = 15000;"
                : "PRAGMA busy_timeout = 15000; PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();

            return connection;
        }
        catch (Exception exception)
        {
            connection.Dispose();
            throw new OpenCodeDatabaseException(
                $"Could not open the OpenCode database '{_databasePath}'. " +
                "Close OpenCode and check that the file is readable.",
                exception);
        }
    }
}
