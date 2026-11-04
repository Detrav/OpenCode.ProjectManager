# Usage

## Database discovery

When no path is given, the app picks the newest existing candidate from:

- `OPENCODE_DB` when set;
- `$XDG_DATA_HOME/opencode/opencode.db`;
- `~/.local/share/opencode/opencode.db`;
- `%LOCALAPPDATA%\opencode\opencode.db`;
- additional `opencode-*.db` channel databases in the same directories.

You can also paste a path in the database field, use **Browse…**, or pass it as the first command-line argument:

```powershell
dotnet run --project src/OpenCode.ProjectManager -- "C:\Users\you\.local\share\opencode\opencode.db"
```

## Main window

- **Projects** — search by name, path, VCS or project ID. Archived projects and projects whose folder no longer exists are marked in the details pane.
- **Project details** — name, path, ID, VCS, created/active/archived timestamps, V2 and legacy session counts, messages, worktrees, legacy directories, permissions, schedules, jobs, blob references and event streams.
- **Global session details** — title, path, ID, slug, agent, model, messages, parts, schedules, jobs, event streams, blob references and child-session count.

The built-in **global project** is not shown as an aggregate row. Every root session of that project is shown in the list as its own project-like entry. Deleting such an entry removes only that session and its child sessions; the global project row and all other sessions are kept.

## Deleting

The **Delete permanently…** button opens a danger-zone confirmation dialog. The action cannot be enabled until the confirmation checkbox is ticked.

- Deletion never creates or waits for a backup. Press **Backup now** first if you need a recovery point.
- The project folder and all other files on disk are never touched.
- Deleting a project removes the project and all of its sessions.
- Deleting a global session entry removes that session, its child sessions and their related rows.

See [Database schema and deletion](database.md) for the full list of removed rows.

## OpenCode process guard

The app looks for processes named `opencode`, `opencode-cli`, `opencode-desktop`, `opencode-service`, `opencode-tui`, `opencode2`, `opencode2-node`, plus processes starting with `opencode-` or `opencode2-` (this covers truncated Linux process names).

When a matching process is found:

- a blocking screen lists the detected processes;
- writes, deletions and compaction are disabled;
- **Continue read-only** allows inspection only;
- the check runs every 5 seconds and on **Check again**.

Limitation: if OpenCode runs under a non-standard process name (for example, `bun` from source), it is not detected automatically. Close OpenCode manually in that case.

## Limitations

- There is no restore-from-backup UI; a backup is a regular SQLite file that can be opened manually.
- Deletion does not run `VACUUM` automatically. Use **Compact database…** when you want to return free pages to disk.
- Process detection is based on executable names.
- The app modifies only `opencode.db`; local desktop-app state (for example, its own recent-project list) may live elsewhere and is not cleaned.
- Do not run the utility against a database currently in use by OpenCode. The process guard exists specifically to prevent that.
