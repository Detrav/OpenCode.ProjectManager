# Backups and compaction

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
- The SQLite backup API copies free pages too, so a backup of a bloated file stays large. Compact the database first if you want compact backups.

## Database compaction

SQLite never shrinks a database file on its own. Deleted rows leave **free pages** inside the file, which are reused by later writes but not returned to the file system. That is why `opencode.db` can stay at 14 GB while it only holds about 1 GB of data.

The **Compact database…** button in the left panel's danger zone runs:

```text
PRAGMA wal_checkpoint(TRUNCATE);
VACUUM;
PRAGMA wal_checkpoint(TRUNCATE);
```

- The confirmation dialog shows the current file size, live data size and reclaimable free pages.
- Compaction requires a checked confirmation and is disabled while OpenCode is running or while read-only mode is active.
- `VACUUM` rewrites the whole file, so it can take several minutes on a large database and needs free disk space for a temporary copy.
- If the operation is interrupted, SQLite leaves the database unchanged.
- Only the database rows are affected; project folders on disk are never touched.
