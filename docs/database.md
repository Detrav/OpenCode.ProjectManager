# Database schema and deletion

## Schema validation

Before loading projects, the app checks the database against the schema it knows how to handle.

Required core tables include:

```text
project, session_v2, session_message, session_part, worktree,
project_directory, permission, schedule, job, blob, blob_ref,
event_sequence, event, instruction_entry, instruction_state, session_inbox
```

It also checks required columns and, for every table involved in deletion, that the foreign keys use `ON DELETE CASCADE`. For example:

- `session_v2.project_id -> project.id`
- `session_message.session_id -> session_v2.id`
- `worktree.project_id -> project.id`
- `permission.project_id -> project.id`
- `event.aggregate_id -> event_sequence.aggregate_id`

Legacy tables (`session`, `message`, `part`, `todo`, `session_share`) are optional, but if present they must also cascade correctly.

If validation fails, the app shows an error with the missing tables, columns or cascades and does not load the project list. Opening the database once with a current OpenCode 2 build usually applies the required migrations. Non-critical missing cascades are reported as warnings.

## What deletion removes

### Deleting a project

1. The `project` row is deleted.
2. Foreign keys cascade to:
   - `session_v2` and legacy `session`;
   - `session_message`, `session_part`, legacy `message` and `part`;
   - `worktree`, `project_directory`;
   - `permission`;
   - `schedule`;
   - `blob_ref`;
   - `instruction_entry`, `instruction_state`, `session_inbox`, `todo`, `session_share`.
3. The app also removes:
   - `job` rows for the project's sessions;
   - `event_sequence` rows for the project and its sessions (along with their `event` rows);
   - orphaned `blob` rows that are no longer referenced.
4. The transaction is committed only as a whole. On failure it is rolled back.

### Deleting a global session

The built-in `global` project is represented by its root sessions in the list. Deleting one of those entries:

- removes that session and its child sessions (V2 and matching legacy rows);
- removes their messages, parts, schedules, jobs, blob references and event streams;
- keeps the `global` project row, its worktrees, permissions and directories;
- keeps all other global sessions;
- never touches files on disk.

The confirmation dialog and the delete button are the same as for a regular project.

### What is never removed

- The project folder or any other files on disk.
- The built-in `global` project row.
- Other projects and their sessions.
- Other global sessions.
- Blobs still referenced by remaining sessions.
