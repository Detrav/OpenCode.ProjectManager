# OpenCode Project Manager

A small **C# + Avalonia** desktop application for inspecting and permanently deleting projects from an **OpenCode 2** SQLite database.

The app works directly with `opencode.db`, validates the schema before showing data, displays projects and global sessions with their related rows, and can create a consistent database backup on demand. While OpenCode is running, database changes are blocked.

> This is a standalone utility, not an OpenCode plugin or a patch for OpenCode. It never deletes project folders or any other files on disk — only rows in the OpenCode database are removed.

![OpenCode Project Manager](docs/screenshot.png)

## Features

- Automatic database discovery:
  - `OPENCODE_DB` when set;
  - `$XDG_DATA_HOME/opencode/opencode.db`;
  - `~/.local/share/opencode/opencode.db`;
  - `%LOCALAPPDATA%\opencode\opencode.db`;
  - additional `opencode-*.db` channel databases in the same directories.
- Manual database selection and loading a specific file from the first command-line argument.
- Schema validation on open. Incompatible databases are rejected with a detailed error instead of being partially modified.
- Project list with search by name, path, VCS and project ID.
- Project details:
  - name, path, ID, VCS, created/active/archived timestamps and folder presence;
  - V2 and legacy session counts;
  - messages, worktrees, legacy directories, permissions, schedules, jobs, blob references and event streams;
  - worktree and legacy-directory lists, sandboxes and the worktree startup command.
- Global session details:
  - title, path, ID, slug, agent and model;
  - messages, parts, schedules, jobs, event streams, blob references and child-session count.
- Permanent project deletion with confirmation.
- The built-in **global project** is not shown as an aggregate row. Every root session of that project is shown in the list as its own project-like entry. Deleting such an entry removes only that session and its child sessions; the global project row and all other sessions are kept.
- Backup only through the **Backup now** button. Every click creates a new backup file.
- Write blocking while OpenCode is detected, plus a read-only mode for inspecting data.

## Requirements

- .NET SDK **10.0** or newer.
- Windows, Linux or macOS (Avalonia Desktop).
- A current OpenCode 2 database schema. OpenCode 1 databases (`session_v2` is missing) are rejected.

## Build and run

```powershell
dotnet build src/OpenCode.ProjectManager.slnx
dotnet run --project src/OpenCode.ProjectManager
```

Run with a specific database:

```powershell
dotnet run --project src/OpenCode.ProjectManager -- "C:\Users\you\.local\share\opencode\opencode.db"
```

Built executable:

```text
src\OpenCode.ProjectManager\bin\Debug\net10.0\OpenCode.ProjectManager.exe
```

The first command-line argument is the path to `opencode.db`. When omitted, the app picks the newest existing candidate it finds.

Self-contained publish:

```powershell
dotnet publish src/OpenCode.ProjectManager -c Release -r win-x64 --self-contained true
```

The published build is written to `src/OpenCode.ProjectManager/bin/Release/net10.0/win-x64/publish/`.

## Package management

NuGet versions are managed centrally in [`Directory.Packages.props`](Directory.Packages.props). Project files use versionless `<PackageReference>` entries, so dependency versions are changed in one place.

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

## Backups

Backups are **not created automatically**. The only way to create one is the **Backup now** button. Every click creates a new backup file:

```text
<opencode.db directory>\opencode-project-manager-backups\opencode-YYYYMMDD-HHmmss.db
```

- The SQLite Online Backup API is used, so WAL contents are included.
- The backup is written to a temporary `*.partial` file, verified by opening it and reading `sqlite_master`, and only then renamed to the final `.db` file.
- Free space is checked first; if there is not enough room, the backup fails.
- Deletion never creates or waits for a backup. If you want a recovery point, press **Backup now** before deleting.
- A large database can require a backup of the same size (for example, 15 GB).

## OpenCode process guard

The app looks for processes named `opencode`, `opencode-cli`, `opencode-desktop`, `opencode-service`, `opencode-tui`, `opencode2`, `opencode2-node`, plus processes starting with `opencode-` or `opencode2-` (this covers truncated Linux process names).

When a matching process is found:

- a blocking screen lists the detected processes;
- writes and deletions are disabled;
- **Continue read-only** allows inspection only;
- the check runs every 5 seconds and on **Check again**.

Limitation: if OpenCode runs under a non-standard process name (for example, `bun` from source), it is not detected automatically. Close OpenCode manually in that case.

## Project structure

```text
Directory.Packages.props             # central NuGet versions
src/
  OpenCode.ProjectManager.slnx       # solution
  OpenCode.ProjectManager.Core/      # models, SQLite access, backup, process guard
  OpenCode.ProjectManager/           # Avalonia UI and MVVM
  OpenCode.ProjectManager.Tests/     # xUnit tests
README.md
```

### Core

- `DatabaseLocator` — OpenCode/XDG database discovery.
- `OpenCodeDatabase` — schema validation, project reads, project deletion and global session deletion.
- `BackupService` — on-demand SQLite backup; each call creates a new file.
- `OpenCodeProcessGuard` — running OpenCode process detection.

### UI

- `MainViewModel` — state, filtering, commands and confirmation flows.
- `MainWindow.axaml` — project list, details pane, blocking overlay and confirmation overlays.

## Tests

```powershell
dotnet test src/OpenCode.ProjectManager.slnx
```

The tests create a temporary SQLite database with the V2 and legacy schemas and verify:

- reading projects and related entities;
- full project deletion and cascade behavior;
- deletion of a single global session with its child sessions while keeping the global project row and metadata;
- preservation of other projects, other global sessions and shared blobs;
- refusal to delete the `global` project row;
- rejection of an OpenCode 1-only database;
- creation of a new backup file on every backup call.

## Limitations

- There is no restore-from-backup UI; a backup is a regular SQLite file that can be opened manually.
- No automatic `VACUUM` is performed after deletion. The database file may not shrink immediately, but freed pages are reused by SQLite.
- Process detection is based on executable names.
- The app modifies only `opencode.db`; local desktop-app state (for example, its own recent-project list) may live elsewhere and is not cleaned.
- Do not run the utility against a database currently in use by OpenCode. The process guard exists specifically to prevent that.
