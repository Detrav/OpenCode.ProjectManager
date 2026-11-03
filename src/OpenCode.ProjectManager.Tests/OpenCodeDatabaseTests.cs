using OpenCode.ProjectManager.Core.Services;
using Xunit;

namespace OpenCode.ProjectManager.Tests;

public sealed class OpenCodeDatabaseTests
{
    [Fact]
    public void Load_ReadsProjectsAndCounts()
    {
        using var database = new TestDatabase();
        database.Create();

        var snapshot = new OpenCodeDatabase(database.DatabasePath).Load();

        Assert.True(snapshot.IsSupported);
        Assert.Equal(4, snapshot.Projects.Count);

        var alpha = Assert.Single(snapshot.Projects, project => project.Id == "prj_a");
        Assert.Equal("Alpha", alpha.DisplayName);
        Assert.Equal("C:/projects/alpha", alpha.CanonicalPath);
        Assert.Equal("git", alpha.Vcs);
        Assert.Equal(2, alpha.SessionCount);
        Assert.Equal(1, alpha.LegacySessionCount);
        Assert.Equal(2, alpha.WorktreeCount);
        Assert.Equal(1, alpha.LegacyDirectoryCount);
        Assert.False(alpha.IsArchived);
        Assert.Single(alpha.Sandboxes);
        Assert.Equal("dotnet build", alpha.StartupCommand);

        var archived = Assert.Single(snapshot.Projects, project => project.Id == "prj_archived");
        Assert.True(archived.IsArchived);
        Assert.False(archived.DirectoryExists);

        var global = Assert.Single(snapshot.Projects, project => project.Id == "global");
        Assert.True(global.IsGlobal);
    }

    [Fact]
    public void LoadDetails_ReadsRelatedEntities()
    {
        using var database = new TestDatabase();
        database.Create();

        var details = new OpenCodeDatabase(database.DatabasePath).LoadDetails("prj_a");

        Assert.Equal(2, details.Worktrees.Count);
        Assert.Single(details.LegacyDirectories);
        Assert.Equal(2, details.MessageCount);
        Assert.Equal(1, details.LegacyMessageCount);
        Assert.Equal(1, details.PermissionCount);
        Assert.Equal(1, details.ScheduleCount);
        Assert.Equal(1, details.JobCount);
        Assert.Equal(1, details.BlobReferenceCount);
        Assert.Equal(4, details.EventSequenceCount);
    }

    [Fact]
    public void DeleteProject_RemovesProjectAndDependentRows()
    {
        using var database = new TestDatabase();
        database.Create();

        var result = new OpenCodeDatabase(database.DatabasePath).DeleteProject("prj_a", backupPath: "backup.db");

        Assert.Equal(2, result.SessionsV2);
        Assert.Equal(1, result.SessionsV1);
        Assert.Equal(2, result.Worktrees);
        Assert.Equal(1, result.LegacyDirectories);
        Assert.Equal(3, result.Messages);
        Assert.Equal(4, result.EventSequences);
        Assert.Equal(1, result.Jobs);
        Assert.Equal(1, result.OrphanedBlobs);
        Assert.Equal("backup.db", result.BackupPath);

        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM project WHERE id = 'prj_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session_v2 WHERE project_id = 'prj_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session WHERE project_id = 'prj_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM worktree WHERE project_id = 'prj_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM project_directory WHERE project_id = 'prj_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM permission WHERE project_id = 'prj_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session_message WHERE session_id IN ('ses_a1','ses_a2');"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session_part WHERE session_id IN ('ses_a1','ses_a2');"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM schedule WHERE id = 'sched_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM job WHERE id = 'job_a';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM event_sequence WHERE aggregate_id IN ('prj_a','ses_a1','ses_a2','ses_a_legacy');"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM event WHERE aggregate_id IN ('prj_a','ses_a1','ses_a2','ses_a_legacy');"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM blob WHERE hash = 'orphan';"));

        // Another project and shared media are untouched.
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM project WHERE id = 'prj_b';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM session_v2 WHERE id = 'ses_b1';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM schedule WHERE id = 'sched_b';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM job WHERE id = 'job_b';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM event_sequence WHERE aggregate_id = 'ses_b1';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM blob WHERE hash = 'shared';"));
    }

    [Fact]
    public void DeleteSessions_KeepsProjectAndItsMetadata()
    {
        using var database = new TestDatabase();
        database.Create();

        var result = new OpenCodeDatabase(database.DatabasePath).DeleteSessions("global", backupPath: "backup.db");

        Assert.Equal(1, result.SessionsV2);
        Assert.Equal(1, result.SessionsV1);
        Assert.Equal(2, result.Messages);
        Assert.Equal(2, result.EventSequences);
        Assert.Equal(1, result.Jobs);
        Assert.Equal(1, result.OrphanedBlobs);

        // The project row, worktrees and permissions are kept.
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM project WHERE id = 'global';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM worktree WHERE project_id = 'global';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM permission WHERE project_id = 'global';"));

        // Sessions and their dependants are gone.
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session_v2 WHERE project_id = 'global';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session WHERE project_id = 'global';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM session_message WHERE session_id = 'ses_global_1';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM message WHERE session_id = 'ses_global_legacy';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM schedule WHERE id = 'sched_global';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM job WHERE id = 'job_global';"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM event_sequence WHERE aggregate_id IN ('ses_global_1','ses_global_legacy');"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM blob WHERE hash = 'global_only';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM blob WHERE hash = 'shared';"));

        // Other projects are untouched.
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM session_v2 WHERE project_id = 'prj_a';"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM session_v2 WHERE project_id = 'prj_b';"));
    }

    [Fact]
    public void Load_RejectsV1OnlyDatabase()
    {
        using var database = new TestDatabase();
        database.CreateV1Only();

        var exception = Assert.Throws<OpenCodeDatabaseException>(() =>
            new OpenCodeDatabase(database.DatabasePath).Load());

        Assert.Contains("session_v2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteProject_RefusesGlobalProject()
    {
        using var database = new TestDatabase();
        database.Create();

        var exception = Assert.Throws<OpenCodeDatabaseException>(() =>
            new OpenCodeDatabase(database.DatabasePath).DeleteProject("global", backupPath: null));

        Assert.Contains("global", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM project WHERE id = 'global';"));
    }

    [Fact]
    public void Backup_IsCreatedOncePerDatabase()
    {
        using var database = new TestDatabase();
        database.Create();

        var service = new BackupService();
        var first = service.EnsureBackup(database.DatabasePath);
        var second = service.EnsureBackup(database.DatabasePath);

        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.True(new FileInfo(first).Length > 0);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={first};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM project;";
        Assert.Equal(4L, Convert.ToInt64(command.ExecuteScalar()));
    }
}
