using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCode.ProjectManager.Core.Models;
using OpenCode.ProjectManager.Core.Services;

namespace OpenCode.ProjectManager.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly BackupService _backupService = new();
    private readonly DatabaseMaintenanceService _maintenanceService = new();
    private int _detailsVersion;
    private bool _initialized;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private DatabaseSnapshot? _snapshot;

    [ObservableProperty]
    private ProjectListItem? _selectedProject;

    [ObservableProperty]
    private ProjectDetails? _selectedDetails;

    [ObservableProperty]
    private GlobalSessionDetails? _selectedGlobalSessionDetails;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDetailsLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _schemaWarning;

    [ObservableProperty]
    private bool _isOpenCodeRunning;

    [ObservableProperty]
    private bool _readOnlyMode;

    [ObservableProperty]
    private bool _isBlockingOverlayVisible;

    [ObservableProperty]
    private string _runningProcessesText = string.Empty;

    [ObservableProperty]
    private bool _isDeleteConfirmationVisible;

    [ObservableProperty]
    private bool _deleteConfirmed;

    [ObservableProperty]
    private string _deletePreviewText = string.Empty;

    [ObservableProperty]
    private string _deleteConfirmationTitle = "Delete permanently?";

    [ObservableProperty]
    private string _deleteConfirmationDescription = string.Empty;

    [ObservableProperty]
    private string _deleteConfirmationCheckboxText = "I understand that this cannot be undone.";

    [ObservableProperty]
    private string _deleteConfirmationButtonText = "Delete permanently";

    [ObservableProperty]
    private string? _lastBackupPath;

    [ObservableProperty]
    private DatabaseSpaceInfo? _databaseSpace;

    [ObservableProperty]
    private bool _isCompactConfirmationVisible;

    [ObservableProperty]
    private bool _compactConfirmed;

    [ObservableProperty]
    private string _compactPreviewText = string.Empty;

    public ObservableCollection<ProjectListItem> Projects { get; } = [];

    public ObservableCollection<ProjectListItem> FilteredProjects { get; } = [];

    public bool HasDatabase => Snapshot is not null;

    public bool HasSelection => SelectedProject is not null;

    public bool IsProjectSelected => SelectedProject is { IsProject: true };

    public bool IsGlobalSessionSelected => SelectedProject is { IsGlobalSession: true };

    public bool CanDelete =>
        SelectedProject is not null &&
        Snapshot is { IsSupported: true } &&
        !IsBusy &&
        !IsOpenCodeRunning &&
        !ReadOnlyMode;

    public bool CanConfirmDelete => DeleteConfirmed && CanDelete;

    public bool IsNoDatabaseVisible => !HasDatabase && !IsBusy;

    public bool IsDetailsPlaceholderVisible => HasDatabase && !HasSelection;

    public bool IsEmptyProjectsVisible => HasDatabase && FilteredProjects.Count == 0;

    public bool IsErrorVisible => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsSchemaWarningVisible => !string.IsNullOrWhiteSpace(SchemaWarning);

    public bool CanEnableWriteMode => !IsOpenCodeRunning && ReadOnlyMode;

    public bool HasDatabaseSpace => DatabaseSpace is not null;

    public bool HasReclaimableSpace => DatabaseSpace?.HasReclaimableSpace == true;

    public bool CanCompactDatabase =>
        HasDatabaseSpace &&
        Snapshot is { IsSupported: true } &&
        !IsBusy &&
        !IsOpenCodeRunning &&
        !ReadOnlyMode;

    public bool CanConfirmCompact => CompactConfirmed && CanCompactDatabase;

    public string DatabaseLiveSizeDisplay => DatabaseSpace is null
        ? "—"
        : DatabaseCandidate.FormatBytes(DatabaseSpace.LiveBytes);

    public string DatabaseReclaimableDisplay => DatabaseSpace is null
        ? "—"
        : DatabaseSpace.HasReclaimableSpace
            ? $"{DatabaseCandidate.FormatBytes(DatabaseSpace.ReclaimableBytes)} ({DatabaseSpace.FreePercent:0.#}%)"
            : "none";

    public string ProjectCountDisplay => HasDatabase
        ? $"{FilteredProjects.Count} of {Projects.Count} item{(Projects.Count == 1 ? string.Empty : "s")}"
        : "No database loaded";

    public string DatabaseSizeDisplay => Snapshot is null
        ? "—"
        : DatabaseCandidate.FormatBytes(Snapshot.FileSizeBytes);

    public string BackupStatusDisplay => string.IsNullOrWhiteSpace(LastBackupPath)
        ? "Backup: not created. Use Backup now if you need a recovery point."
        : $"Latest backup: {LastBackupPath}";

    public string DatabasePathDisplay => string.IsNullOrWhiteSpace(DatabasePath)
        ? "No database selected"
        : ProjectRecord.ToDisplayPath(DatabasePath);

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (!string.IsNullOrWhiteSpace(DatabasePath))
        {
            await LoadAsync();
        }
        else
        {
            var candidates = DatabaseLocator.Discover();
            var existing = candidates.FirstOrDefault(candidate => candidate.Exists);

            if (existing is not null)
            {
                DatabasePath = existing.Path;
                await LoadAsync();
            }
            else
            {
                DatabasePath = candidates.FirstOrDefault()?.Path ??
                               Path.Combine(DatabaseLocator.GetPrimaryDataRoot(), "opencode.db");
                StatusMessage = "No OpenCode database was found automatically. Choose a database file.";
            }
        }

        await RefreshProcessesAsync();
    }

    public async Task LoadDatabaseAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DatabasePath = path;
        await LoadAsync(force: true);
        await RefreshProcessesAsync();
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync(bool force = false)
    {
        if (IsBusy && !force)
        {
            return;
        }

        var path = DatabasePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Snapshot = null;
            DatabaseSpace = null;
            Projects.Clear();
            ApplyFilter();
            ErrorMessage = string.IsNullOrWhiteSpace(path)
                ? "Choose an OpenCode database file."
                : $"Database not found: {path}";
            StatusMessage = "Database not loaded.";
            NotifyState();
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        SchemaWarning = null;
        StatusMessage = "Reading database…";

        try
        {
            var snapshot = await Task.Run(() => new OpenCodeDatabase(path).Load());

            Snapshot = snapshot;
            DatabaseSpace = TryReadSpaceInfo(path);
            SchemaWarning = snapshot.SchemaWarning;
            Projects.Clear();

            var items = snapshot.Projects
                .Where(project => !project.IsGlobal)
                .Select(ProjectListItem.FromProject)
                .Concat(snapshot.GlobalSessions.Select(ProjectListItem.FromGlobalSession))
                .OrderByDescending(item => item.SortTimestamp)
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var item in items)
            {
                Projects.Add(item);
            }

            ApplyFilter();

            var previousId = SelectedProject?.Id;
            SelectedProject = previousId is not null
                ? FilteredProjects.FirstOrDefault(item => item.Id == previousId)
                : FilteredProjects.FirstOrDefault();

            StatusMessage =
                $"Loaded {items.Length} item{(items.Length == 1 ? string.Empty : "s")} " +
                $"from {ProjectRecord.ToDisplayPath(path)}.";
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            Snapshot = null;
            DatabaseSpace = null;
            Projects.Clear();
            ApplyFilter();
            SelectedProject = null;
            SelectedDetails = null;
            SelectedGlobalSessionDetails = null;
            ErrorMessage = exception.Message;
            StatusMessage = "Could not load the database.";
        }
        finally
        {
            IsBusy = false;
            NotifyState();
        }
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        try
        {
            var processes = await Task.Run(OpenCodeProcessGuard.FindRunningProcesses);
            ApplyProcesses(processes);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Could not inspect running processes: {exception.Message}";
        }
    }

    private void ApplyProcesses(IReadOnlyList<RunningProcessInfo> processes)
    {
        IsOpenCodeRunning = processes.Count > 0;
        RunningProcessesText = processes.Count == 0
            ? "No OpenCode processes detected."
            : string.Join(Environment.NewLine, processes.Select(process =>
                $"{process.Display}{(string.IsNullOrWhiteSpace(process.ExecutablePath) ? string.Empty : $"  ·  {process.ExecutablePath}")}"));

        if (IsOpenCodeRunning && !ReadOnlyMode)
        {
            IsBlockingOverlayVisible = true;
        }
        else if (!IsOpenCodeRunning)
        {
            IsBlockingOverlayVisible = false;
        }

        NotifyState();
    }

    [RelayCommand]
    private void ContinueReadOnly()
    {
        ReadOnlyMode = true;
        IsBlockingOverlayVisible = false;
        NotifyState();
    }

    [RelayCommand]
    private void EnableWriteMode()
    {
        if (IsOpenCodeRunning)
        {
            IsBlockingOverlayVisible = true;
            return;
        }

        ReadOnlyMode = false;
        NotifyState();
    }

    [RelayCommand]
    private async Task ShowDeleteConfirmationAsync()
    {
        if (!CanDelete || SelectedProject is null)
        {
            return;
        }

        if (!await EnsureDetailsLoadedAsync())
        {
            return;
        }

        var selected = SelectedProject;
        DeleteConfirmed = false;

        if (selected.IsProject && selected.Project is { } project)
        {
            DeleteConfirmationTitle = "Delete project permanently?";
            DeleteConfirmationDescription =
                "This removes the project and all of its sessions from the OpenCode database. It cannot be undone from the app.";
            DeleteConfirmationCheckboxText =
                "I understand that the project and all of its sessions will be deleted.";
            DeleteConfirmationButtonText = "Delete permanently";
            DeletePreviewText = BuildDeletePreview(project, SelectedDetails);
        }
        else if (selected.IsGlobalSession && selected.GlobalSession is { } session)
        {
            DeleteConfirmationTitle = "Delete session permanently?";
            DeleteConfirmationDescription =
                "This removes this global session and its child sessions from the OpenCode database. The global project row and all other sessions are kept.";
            DeleteConfirmationCheckboxText =
                "I understand that this session and its child sessions will be deleted.";
            DeleteConfirmationButtonText = "Delete session";
            DeletePreviewText = BuildDeleteSessionPreview(session, SelectedGlobalSessionDetails);
        }
        else
        {
            return;
        }

        IsDeleteConfirmationVisible = true;
        NotifyState();
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmationVisible = false;
        DeleteConfirmed = false;
        NotifyState();
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (!CanConfirmDelete || SelectedProject is null)
        {
            return;
        }

        if (await StopIfOpenCodeRunningAsync(
                "OpenCode started while the confirmation dialog was open. Close it and try again."))
        {
            IsDeleteConfirmationVisible = false;
            return;
        }

        var selected = SelectedProject;
        var path = DatabasePath;
        IsDeleteConfirmationVisible = false;
        DeleteConfirmed = false;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Deleting “{selected.DisplayName}”…";

        var progress = new Progress<string>(message => StatusMessage = message);

        try
        {
            if (selected.IsProject && selected.Project is { } project)
            {
                var result = await Task.Run(() =>
                    new OpenCodeDatabase(path).DeleteProject(project.Id, progress));

                await LoadAsync(force: true);

                StatusMessage =
                    $"Deleted “{result.ProjectName}”: {result.TotalSessions:N0} session(s), " +
                    $"{result.Messages:N0} message(s), {result.Worktrees:N0} worktree(s), " +
                    $"{result.OrphanedBlobs:N0} orphaned blob(s).";
            }
            else if (selected.IsGlobalSession && selected.GlobalSession is { } session)
            {
                var result = await Task.Run(() =>
                    new OpenCodeDatabase(path).DeleteSession(session.Id, progress));

                await LoadAsync(force: true);

                StatusMessage =
                    $"Deleted session “{result.ProjectName}”: {result.TotalSessions:N0} session(s), " +
                    $"{result.Messages:N0} message(s), {result.OrphanedBlobs:N0} orphaned blob(s).";
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Deletion failed. The transaction was rolled back.";
        }
        finally
        {
            IsBusy = false;
            NotifyState();
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DatabasePath) || !File.Exists(DatabasePath))
        {
            ErrorMessage = "Load a database before creating a backup.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        var progress = new Progress<string>(message => StatusMessage = message);

        try
        {
            LastBackupPath = await Task.Run(() => _backupService.CreateBackup(DatabasePath, progress));
            StatusMessage = $"Backup created: {LastBackupPath}";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Backup failed.";
        }
        finally
        {
            IsBusy = false;
            NotifyState();
        }
    }

    [RelayCommand]
    private void ShowCompactConfirmation()
    {
        if (!CanCompactDatabase || DatabaseSpace is null)
        {
            return;
        }

        CompactConfirmed = false;
        CompactPreviewText = BuildCompactPreview(DatabaseSpace);
        IsCompactConfirmationVisible = true;
        NotifyState();
    }

    [RelayCommand]
    private void CancelCompact()
    {
        IsCompactConfirmationVisible = false;
        CompactConfirmed = false;
        NotifyState();
    }

    [RelayCommand]
    private async Task ConfirmCompactAsync()
    {
        if (!CanConfirmCompact || DatabaseSpace is null)
        {
            return;
        }

        if (await StopIfOpenCodeRunningAsync(
                "OpenCode started while the confirmation dialog was open. Close it and try again."))
        {
            IsCompactConfirmationVisible = false;
            return;
        }

        var path = DatabasePath;
        var before = DatabaseSpace;
        IsCompactConfirmationVisible = false;
        CompactConfirmed = false;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "Compacting database…";

        var progress = new Progress<string>(message => StatusMessage = message);

        try
        {
            var after = await Task.Run(() => _maintenanceService.Compact(path, progress));

            DatabaseSpace = after;
            await LoadAsync(force: true);

            var freed = Math.Max(0, before.FileSizeBytes - after.FileSizeBytes);
            StatusMessage =
                $"Database compacted: {DatabaseCandidate.FormatBytes(before.FileSizeBytes)} → " +
                $"{DatabaseCandidate.FormatBytes(after.FileSizeBytes)} " +
                $"({DatabaseCandidate.FormatBytes(freed)} freed).";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Compaction failed. The database file was left unchanged.";
        }
        finally
        {
            IsBusy = false;
            NotifyState();
        }
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = null;
        NotifyState();
    }

    [RelayCommand]
    private void OpenDatabaseFolder()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ErrorMessage = "The database folder does not exist.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedProjectChanged(ProjectListItem? value)
    {
        _ = LoadDetailsAsync(value);
        NotifyState();
    }

    partial void OnSnapshotChanged(DatabaseSnapshot? value)
    {
        OnPropertyChanged(nameof(DatabasePathDisplay));
        OnPropertyChanged(nameof(DatabaseSizeDisplay));
        NotifyState();
    }

    partial void OnDatabasePathChanged(string value)
    {
        _detailsVersion++;
        SelectedDetails = null;
        DatabaseSpace = null;
        LastBackupPath = null;
        OnPropertyChanged(nameof(DatabasePathDisplay));
    }

    partial void OnIsBusyChanged(bool value) => NotifyState();

    partial void OnIsOpenCodeRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEnableWriteMode));
        NotifyState();
    }

    partial void OnReadOnlyModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEnableWriteMode));
        NotifyState();
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(IsErrorVisible));

    partial void OnSchemaWarningChanged(string? value) => OnPropertyChanged(nameof(IsSchemaWarningVisible));

    partial void OnDeleteConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanConfirmDelete));

    partial void OnLastBackupPathChanged(string? value) => OnPropertyChanged(nameof(BackupStatusDisplay));

    partial void OnDatabaseSpaceChanged(DatabaseSpaceInfo? value) => NotifyState();

    partial void OnCompactConfirmedChanged(bool value) => OnPropertyChanged(nameof(CanConfirmCompact));

    private async Task LoadDetailsAsync(ProjectListItem? item)
    {
        var version = ++_detailsVersion;

        if (item is null)
        {
            SelectedDetails = null;
            SelectedGlobalSessionDetails = null;
            IsDetailsLoading = false;
            return;
        }

        SelectedDetails = null;
        SelectedGlobalSessionDetails = null;
        IsDetailsLoading = true;

        try
        {
            if (item.IsProject && item.Project is { } project)
            {
                var details = await Task.Run(() => new OpenCodeDatabase(DatabasePath).LoadDetails(project.Id));

                if (version != _detailsVersion ||
                    SelectedProject?.Id != item.Id ||
                    SelectedProject.Kind != item.Kind)
                {
                    return;
                }

                SelectedDetails = details;
                if (IsDeleteConfirmationVisible)
                {
                    DeletePreviewText = BuildDeletePreview(project, details);
                }
            }
            else if (item.IsGlobalSession && item.GlobalSession is { } session)
            {
                var details = await Task.Run(() =>
                    new OpenCodeDatabase(DatabasePath).LoadGlobalSessionDetails(session.Id));

                if (version != _detailsVersion ||
                    SelectedProject?.Id != item.Id ||
                    SelectedProject.Kind != item.Kind)
                {
                    return;
                }

                SelectedGlobalSessionDetails = details;
                if (IsDeleteConfirmationVisible)
                {
                    DeletePreviewText = BuildDeleteSessionPreview(session, details);
                }
            }
        }
        catch (Exception exception)
        {
            if (version == _detailsVersion)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (version == _detailsVersion)
            {
                IsDetailsLoading = false;
            }
        }
    }

    private async Task<bool> EnsureDetailsLoadedAsync()
    {
        if (SelectedProject is null)
        {
            return false;
        }

        var item = SelectedProject;

        if (item.IsProject && SelectedDetails is not null)
        {
            return true;
        }

        if (item.IsGlobalSession && SelectedGlobalSessionDetails is not null)
        {
            return true;
        }

        IsDetailsLoading = true;

        try
        {
            if (item.IsProject && item.Project is { } project)
            {
                var details = await Task.Run(() => new OpenCodeDatabase(DatabasePath).LoadDetails(project.Id));
                if (SelectedProject?.Id != item.Id || SelectedProject.Kind != item.Kind)
                {
                    return false;
                }

                SelectedDetails = details;
                return true;
            }

            if (item.IsGlobalSession && item.GlobalSession is { } session)
            {
                var details = await Task.Run(() =>
                    new OpenCodeDatabase(DatabasePath).LoadGlobalSessionDetails(session.Id));
                if (SelectedProject?.Id != item.Id || SelectedProject.Kind != item.Kind)
                {
                    return false;
                }

                SelectedGlobalSessionDetails = details;
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        finally
        {
            IsDetailsLoading = false;
        }
    }

    private async Task<bool> StopIfOpenCodeRunningAsync(string message)
    {
        var processes = await Task.Run(OpenCodeProcessGuard.FindRunningProcesses);
        if (processes.Count == 0)
        {
            return false;
        }

        ApplyProcesses(processes);
        ErrorMessage = message;
        return true;
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var selectedId = SelectedProject?.Id;
        var selectedKind = SelectedProject?.Kind;

        FilteredProjects.Clear();
        foreach (var item in Projects)
        {
            if (query.Length > 0 && !Matches(item, query))
            {
                continue;
            }

            FilteredProjects.Add(item);
        }

        if (selectedId is not null &&
            FilteredProjects.All(item => item.Id != selectedId || item.Kind != selectedKind))
        {
            SelectedProject = FilteredProjects.FirstOrDefault();
        }

        OnPropertyChanged(nameof(ProjectCountDisplay));
        OnPropertyChanged(nameof(IsEmptyProjectsVisible));
        NotifyState();
    }

    private static bool Matches(ProjectListItem item, string query) =>
        item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.PathDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (item.Vcs?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private string BuildDeleteSessionPreview(GlobalSessionRecord session, GlobalSessionDetails? details)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session: {session.DisplayName}");
        builder.AppendLine($"Path: {session.PathDisplay}");
        builder.AppendLine($"ID: {session.Id}");
        builder.AppendLine("Project: global");
        builder.AppendLine();
        builder.AppendLine($"Messages: {(details?.TotalMessageCount ?? session.MessageCount + session.LegacyMessageCount):N0}");
        builder.AppendLine($"Schedules: {(details?.ScheduleCount ?? 0):N0}");
        builder.AppendLine($"Jobs: {(details?.JobCount ?? 0):N0}");
        builder.AppendLine($"Event streams: {(details?.EventSequenceCount ?? 0):N0}");
        builder.AppendLine($"Child sessions: {(details?.ChildSessionCount ?? 0):N0}");
        builder.AppendLine();
        builder.AppendLine("The global project row, its worktrees, permissions and all other sessions are kept.");
        builder.AppendLine("The folder on disk is not touched.");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(LastBackupPath)
            ? "No backup has been created in this run. Use Backup now before deleting if you need a recovery point."
            : $"Latest backup: {LastBackupPath}");

        return builder.ToString();
    }

    private string BuildDeletePreview(ProjectRecord project, ProjectDetails? details)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Project: {project.DisplayName}");
        builder.AppendLine($"Path: {project.PathDisplay}");
        builder.AppendLine($"ID: {project.Id}");
        builder.AppendLine();
        builder.AppendLine($"Sessions (V2): {project.SessionCount:N0}");
        builder.AppendLine($"Legacy sessions: {project.LegacySessionCount:N0}");
        builder.AppendLine($"Messages: {(details?.TotalMessageCount ?? 0):N0}");
        builder.AppendLine($"Worktrees: {project.WorktreeCount:N0}");
        builder.AppendLine($"Legacy directories: {project.LegacyDirectoryCount:N0}");
        builder.AppendLine($"Permissions: {(details?.PermissionCount ?? 0):N0}");
        builder.AppendLine($"Schedules: {(details?.ScheduleCount ?? 0):N0}");
        builder.AppendLine($"Jobs: {(details?.JobCount ?? 0):N0}");
        builder.AppendLine($"Event streams: {(details?.EventSequenceCount ?? 0):N0}");
        builder.AppendLine();
        builder.AppendLine("The folder on disk is NOT deleted. Only OpenCode database rows are removed.");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(LastBackupPath)
            ? "No backup has been created in this run. Use Backup now before deleting if you need a recovery point."
            : $"Latest backup: {LastBackupPath}");

        return builder.ToString();
    }

    private DatabaseSpaceInfo? TryReadSpaceInfo(string path)
    {
        try
        {
            return _maintenanceService.ReadSpaceInfo(path);
        }
        catch (Exception)
        {
            // Space statistics are informational; a failure must not block loading.
            return null;
        }
    }

    private string BuildCompactPreview(DatabaseSpaceInfo space)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Database: {ProjectRecord.ToDisplayPath(DatabasePath)}");
        builder.AppendLine($"File size: {DatabaseCandidate.FormatBytes(space.FileSizeBytes)}");
        builder.AppendLine($"Live data: {DatabaseCandidate.FormatBytes(space.LiveBytes)}");
        builder.AppendLine(
            $"Free pages: {DatabaseCandidate.FormatBytes(space.ReclaimableBytes)} " +
            $"({space.FreePercent:0.#}% of the file)");
        builder.AppendLine();
        builder.AppendLine("VACUUM rewrites the whole database file. All rows are kept, but on a large");
        builder.AppendLine("file this can take several minutes and needs disk space for a temporary copy.");
        builder.AppendLine("If compaction is interrupted, SQLite leaves the database unchanged.");
        builder.AppendLine();
        builder.AppendLine("OpenCode must not be running while the database is compacted.");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(LastBackupPath)
            ? "No backup has been created in this run. Use Backup now before compacting if you need a recovery point."
            : $"Latest backup: {LastBackupPath}");

        return builder.ToString();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasDatabase));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsProjectSelected));
        OnPropertyChanged(nameof(IsGlobalSessionSelected));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanConfirmDelete));
        OnPropertyChanged(nameof(CanEnableWriteMode));
        OnPropertyChanged(nameof(HasDatabaseSpace));
        OnPropertyChanged(nameof(HasReclaimableSpace));
        OnPropertyChanged(nameof(CanCompactDatabase));
        OnPropertyChanged(nameof(CanConfirmCompact));
        OnPropertyChanged(nameof(DatabaseLiveSizeDisplay));
        OnPropertyChanged(nameof(DatabaseReclaimableDisplay));
        OnPropertyChanged(nameof(IsNoDatabaseVisible));
        OnPropertyChanged(nameof(IsDetailsPlaceholderVisible));
        OnPropertyChanged(nameof(IsEmptyProjectsVisible));
        OnPropertyChanged(nameof(ProjectCountDisplay));
        OnPropertyChanged(nameof(DatabaseSizeDisplay));
        OnPropertyChanged(nameof(BackupStatusDisplay));
    }
}
