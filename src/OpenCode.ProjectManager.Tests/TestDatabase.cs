using Microsoft.Data.Sqlite;

namespace OpenCode.ProjectManager.Tests;

internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "opencode-project-manager-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(DirectoryPath);
        DatabasePath = System.IO.Path.Combine(DirectoryPath, "opencode.db");
    }

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public void CreateV1Only()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE project (
                id text PRIMARY KEY,
                worktree text NOT NULL,
                name text,
                sandboxes text NOT NULL
            );

            CREATE TABLE session (
                id text PRIMARY KEY,
                project_id text NOT NULL,
                slug text NOT NULL,
                directory text NOT NULL,
                title text NOT NULL,
                version text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
            );

            INSERT INTO project (id, worktree, name, sandboxes)
            VALUES ('prj_v1', 'C:/projects/v1', 'V1 project', '[]');
            """;
        command.ExecuteNonQuery();
    }

    public void Create()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE project (
                id text PRIMARY KEY,
                worktree text NOT NULL,
                vcs text,
                name text,
                icon_url text,
                icon_color text,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                time_initialized integer,
                sandboxes text NOT NULL,
                commands text,
                icon_url_override text,
                time_active integer DEFAULT 0 NOT NULL,
                time_archived integer
            );

            CREATE TABLE session (
                id text PRIMARY KEY,
                project_id text NOT NULL,
                parent_id text,
                slug text NOT NULL,
                directory text NOT NULL,
                title text NOT NULL,
                version text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                time_archived integer,
                FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
            );

            CREATE TABLE session_v2 (
                id text PRIMARY KEY,
                project_id text NOT NULL,
                parent_id text,
                slug text NOT NULL,
                directory text NOT NULL,
                title text,
                version text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                time_archived integer,
                CONSTRAINT fk_session_v2_project FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
            );

            CREATE TABLE worktree (
                project_id text NOT NULL,
                directory text NOT NULL,
                strategy text,
                time_created integer NOT NULL,
                PRIMARY KEY (project_id, directory),
                FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
            );

            CREATE TABLE project_directory (
                project_id text NOT NULL,
                directory text NOT NULL,
                type text,
                strategy text,
                time_created integer NOT NULL,
                PRIMARY KEY (project_id, directory),
                FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
            );

            CREATE TABLE permission (
                id text PRIMARY KEY,
                project_id text NOT NULL,
                action text NOT NULL,
                resource text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE
            );

            CREATE TABLE schedule (
                id text PRIMARY KEY,
                session_id text,
                action text NOT NULL,
                when_value text NOT NULL,
                tz text NOT NULL,
                status text DEFAULT 'active' NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE
            );

            CREATE TABLE blob (
                hash text PRIMARY KEY,
                mime text NOT NULL,
                bytes integer NOT NULL,
                data blob NOT NULL,
                time_created integer NOT NULL
            );

            CREATE TABLE blob_ref (
                session_id text NOT NULL,
                message_id text NOT NULL,
                hash text NOT NULL,
                seq integer NOT NULL,
                PRIMARY KEY (session_id, message_id, hash),
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE,
                FOREIGN KEY (hash) REFERENCES blob(hash) ON DELETE CASCADE
            );

            CREATE TABLE event_sequence (
                aggregate_id text PRIMARY KEY,
                seq integer NOT NULL,
                owner_id text
            );

            CREATE TABLE event (
                id text PRIMARY KEY,
                aggregate_id text NOT NULL,
                seq integer NOT NULL,
                created integer DEFAULT 0 NOT NULL,
                type text NOT NULL,
                data text NOT NULL,
                FOREIGN KEY (aggregate_id) REFERENCES event_sequence(aggregate_id) ON DELETE CASCADE
            );

            CREATE TABLE session_message (
                id text PRIMARY KEY,
                session_id text NOT NULL,
                type text NOT NULL,
                seq integer NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                data text NOT NULL,
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE
            );

            CREATE TABLE session_part (
                message_id text NOT NULL,
                session_id text NOT NULL,
                ordinal integer NOT NULL,
                type text NOT NULL,
                data text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                PRIMARY KEY (message_id, ordinal),
                FOREIGN KEY (message_id) REFERENCES session_message(id) ON DELETE CASCADE,
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE
            );

            CREATE TABLE instruction_entry (
                session_id text NOT NULL,
                key text NOT NULL,
                value text,
                removed integer DEFAULT false NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                PRIMARY KEY (session_id, key),
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE
            );

            CREATE TABLE instruction_state (
                session_id text PRIMARY KEY,
                epoch_start integer NOT NULL,
                through_seq integer NOT NULL,
                initial_values text NOT NULL,
                current_values text NOT NULL,
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE
            );

            CREATE TABLE session_inbox (
                id text PRIMARY KEY,
                session_id text NOT NULL,
                type text NOT NULL,
                payload text NOT NULL,
                delivery text NOT NULL,
                enqueued_seq integer NOT NULL,
                time_created integer NOT NULL,
                FOREIGN KEY (session_id) REFERENCES session_v2(id) ON DELETE CASCADE
            );

            CREATE TABLE message (
                id text PRIMARY KEY,
                session_id text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                data text NOT NULL,
                FOREIGN KEY (session_id) REFERENCES session(id) ON DELETE CASCADE
            );

            CREATE TABLE part (
                id text PRIMARY KEY,
                message_id text NOT NULL,
                session_id text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL,
                data text NOT NULL,
                FOREIGN KEY (message_id) REFERENCES message(id) ON DELETE CASCADE
            );

            CREATE TABLE job (
                notification_id text PRIMARY KEY,
                id text NOT NULL,
                session_id text NOT NULL,
                child_session_id text,
                kind text NOT NULL,
                status text NOT NULL,
                recovery text NOT NULL,
                time_created integer NOT NULL,
                time_updated integer NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        Seed(connection);
    }

    private static void Seed(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO project (id, worktree, vcs, name, time_created, time_updated, time_active, time_archived, sandboxes, commands)
            VALUES
                ('prj_a', 'C:/projects/alpha', 'git', 'Alpha', 1000, 2000, 3000, NULL, '["C:/projects/alpha-sandbox"]', '{"start":"dotnet build"}'),
                ('prj_b', 'C:/projects/beta', 'git', 'Beta', 1100, 2100, 3100, NULL, '[]', NULL),
                ('prj_archived', 'C:/projects/gone', NULL, NULL, 1200, 2200, 3200, 4000, '[]', NULL),
                ('global', '/', NULL, NULL, 1300, 2300, 3300, NULL, '[]', NULL);

            INSERT INTO session_v2 (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated)
            VALUES
                ('ses_a1', 'prj_a', NULL, 'alpha-1', 'C:/projects/alpha', 'Alpha one', '2', 1, 1),
                ('ses_a2', 'prj_a', 'ses_a1', 'alpha-2', 'C:/projects/alpha', 'Alpha two', '2', 1, 1),
                ('ses_b1', 'prj_b', NULL, 'beta-1', 'C:/projects/beta', 'Beta one', '2', 1, 1),
                ('ses_global_1', 'global', NULL, 'global-1', 'C:/global', 'Global one', '2', 1, 1),
                ('ses_global_child', 'global', 'ses_global_1', 'global-child', 'C:/global', 'Global child', '2', 1, 1);

            INSERT INTO session (id, project_id, slug, directory, title, version, time_created, time_updated)
            VALUES
                ('ses_a_legacy', 'prj_a', 'alpha-legacy', 'C:/projects/alpha', 'Alpha legacy', '1', 1, 1),
                ('ses_b_legacy', 'prj_b', 'beta-legacy', 'C:/projects/beta', 'Beta legacy', '1', 1, 1),
                ('ses_global_1', 'global', 'global-legacy', 'C:/global', 'Global legacy', '1', 1, 1);

            INSERT INTO worktree (project_id, directory, strategy, time_created)
            VALUES
                ('prj_a', 'C:/projects/alpha', NULL, 1),
                ('prj_a', 'C:/projects/alpha-worktree', 'git', 1),
                ('prj_b', 'C:/projects/beta', NULL, 1),
                ('global', 'C:/global', NULL, 1);

            INSERT INTO project_directory (project_id, directory, type, strategy, time_created)
            VALUES
                ('prj_a', 'C:/projects/alpha', 'main', NULL, 1),
                ('prj_b', 'C:/projects/beta', 'main', NULL, 1);

            INSERT INTO permission (id, project_id, action, resource, time_created, time_updated)
            VALUES
                ('perm_a', 'prj_a', 'read', 'C:/projects/alpha', 1, 1),
                ('perm_b', 'prj_b', 'read', 'C:/projects/beta', 1, 1),
                ('perm_global', 'global', 'read', 'C:/global', 1, 1);

            INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
            VALUES
                ('msg_a1', 'ses_a1', 'user', 1, 1, 1, '{}'),
                ('msg_a2', 'ses_a2', 'user', 1, 1, 1, '{}'),
                ('msg_b1', 'ses_b1', 'user', 1, 1, 1, '{}'),
                ('msg_global_1', 'ses_global_1', 'user', 1, 1, 1, '{}'),
                ('msg_global_child', 'ses_global_child', 'user', 1, 1, 1, '{}');

            INSERT INTO session_part (message_id, session_id, ordinal, type, data, time_created, time_updated)
            VALUES
                ('msg_a1', 'ses_a1', 0, 'text', '{}', 1, 1),
                ('msg_b1', 'ses_b1', 0, 'text', '{}', 1, 1),
                ('msg_global_1', 'ses_global_1', 0, 'text', '{}', 1, 1),
                ('msg_global_child', 'ses_global_child', 0, 'text', '{}', 1, 1);

            INSERT INTO message (id, session_id, time_created, time_updated, data)
            VALUES
                ('msg_a_legacy', 'ses_a_legacy', 1, 1, '{}'),
                ('msg_global_legacy', 'ses_global_1', 1, 1, '{}');

            INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
            VALUES
                ('part_a_legacy', 'msg_a_legacy', 'ses_a_legacy', 1, 1, '{}'),
                ('part_global_legacy', 'msg_global_legacy', 'ses_global_1', 1, 1, '{}');

            INSERT INTO schedule (id, session_id, action, when_value, tz, status, time_created, time_updated)
            VALUES
                ('sched_a', 'ses_a1', '{}', '{}', 'UTC', 'active', 1, 1),
                ('sched_b', 'ses_b1', '{}', '{}', 'UTC', 'active', 1, 1),
                ('sched_global', 'ses_global_1', '{}', '{}', 'UTC', 'active', 1, 1);

            INSERT INTO job (notification_id, id, session_id, child_session_id, kind, status, recovery, time_created, time_updated)
            VALUES
                ('job_a', 'job_a', 'ses_a1', 'ses_a2', 'task', 'done', '{}', 1, 1),
                ('job_b', 'job_b', 'ses_b1', NULL, 'task', 'done', '{}', 1, 1),
                ('job_global', 'job_global', 'ses_global_1', NULL, 'task', 'done', '{}', 1, 1);

            INSERT INTO blob (hash, mime, bytes, data, time_created)
            VALUES
                ('shared', 'image/png', 1, X'00', 1),
                ('global_only', 'image/png', 1, X'00', 1),
                ('orphan', 'image/png', 1, X'00', 1);

            INSERT INTO blob_ref (session_id, message_id, hash, seq)
            VALUES
                ('ses_a1', 'msg_a1', 'orphan', 1),
                ('ses_b1', 'msg_b1', 'shared', 1),
                ('ses_global_1', 'msg_global_1', 'global_only', 1);

            INSERT INTO event_sequence (aggregate_id, seq)
            VALUES
                ('prj_a', 2),
                ('ses_a1', 3),
                ('ses_a2', 1),
                ('ses_a_legacy', 1),
                ('ses_b1', 2),
                ('prj_b', 1),
                ('ses_global_1', 1),
                ('ses_global_child', 1);

            INSERT INTO event (id, aggregate_id, seq, created, type, data)
            VALUES
                ('evt_project_a', 'prj_a', 1, 1, 'project.updated', '{}'),
                ('evt_project_a_2', 'prj_a', 2, 1, 'project.updated', '{}'),
                ('evt_ses_a1', 'ses_a1', 1, 1, 'session.created', '{}'),
                ('evt_ses_a2', 'ses_a2', 1, 1, 'session.created', '{}'),
                ('evt_ses_a_legacy', 'ses_a_legacy', 1, 1, 'session.created', '{}'),
                ('evt_ses_b1', 'ses_b1', 1, 1, 'session.created', '{}'),
                ('evt_project_b', 'prj_b', 1, 1, 'project.updated', '{}'),
                ('evt_ses_global_1', 'ses_global_1', 1, 1, 'session.created', '{}'),
                ('evt_ses_global_child', 'ses_global_child', 1, 1, 'session.created', '{}');
            """;
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(DirectoryPath))
            {
                System.IO.Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort for temp data.
        }
    }
}

