# Development

## Project structure

```text
Directory.Packages.props             # central NuGet versions
src/
  OpenCode.ProjectManager.slnx       # solution
  OpenCode.ProjectManager.Core/      # models, SQLite access, backup, process guard
  OpenCode.ProjectManager/           # Avalonia UI and MVVM
  OpenCode.ProjectManager.Tests/     # xUnit tests
docs/                                # documentation
README.md
```

### Core

- `DatabaseLocator` — OpenCode/XDG database discovery.
- `OpenCodeDatabase` — schema validation, project reads, project deletion and global session deletion.
- `BackupService` — on-demand SQLite backup; each call creates a new file.
- `DatabaseMaintenanceService` — reports live/free page usage and compacts the file with `VACUUM`.
- `OpenCodeProcessGuard` — running OpenCode process detection.

### UI

- `MainViewModel` — state, filtering, commands and confirmation flows.
- `MainWindow.axaml` — project list, details pane, maintenance card, blocking overlay and confirmation overlays.

## Package management

NuGet versions are managed centrally in [`Directory.Packages.props`](../Directory.Packages.props). Project files use versionless `<PackageReference>` entries, so dependency versions are changed in one place.

## Self-contained publish

```powershell
dotnet publish src/OpenCode.ProjectManager -c Release -r win-x64 --self-contained true
```

The published build is written to `src/OpenCode.ProjectManager/bin/Release/net10.0/win-x64/publish/`.

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
- creation of a new backup file on every backup call;
- reporting of SQLite free pages and reclaiming them with `VACUUM` while keeping the data.
