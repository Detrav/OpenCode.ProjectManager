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
    private int _detailsVersion;
    private bool _initialized;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private DatabaseSnapshot? _snapshot;

    [ObservableProperty]
    private ProjectRecord? _selectedProject;

    [ObservableProperty]
    private ProjectDetails? _selectedDetails;

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
    private bool _isDeleteSessionsConfirmationVisible;

    [ObservableProperty]
    private bool _deleteSessionsConfirmed;

    [ObservableProperty]
    private string _deleteSessionsPreviewText = string.Empty;

    [ObservableProperty]
    private string? _lastBackupPath;

    public ObservableCollection<ProjectRecord> Projects { get; } = [];

    public ObservableCollection<ProjectRecord> FilteredProjects { get; } = [];

    public bool HasDatabase => Snapshot is not null;

    public bool HasSelection => SelectedProject is not null;

    public bool CanDelete =>
        SelectedProject is { IsGlobal: false } &&
        Snapshot is { IsSupported: true } &&
        !IsBusy &&
        !IsOpenCodeRunning &&
        !ReadOnlyMode;

    public bool CanConfirmDelete => DeleteConfirmed && CanDelete;

    public bool CanDeleteSessions =>
        SelectedProject is { IsGlobal: true, TotalSessionCount: > 0 } &&
        Snapshot is { IsSupported: true } &&
        !IsBusy &&
        !IsOpenCodeRunning &&
        !ReadOnlyMode;

    public bool CanConfirmDeleteSessions => DeleteSessionsConfirmed && CanDeleteSessions;

    public bool IsNoDatabaseVisible => !HasDatabase && !IsBusy;

    public bool IsDetailsPlaceholderVisible => HasDatabase && !HasSelection;

    public bool IsEmptyProjectsVisible => HasDatabase && FilteredProjects.Count == 0;

    public bool IsErrorVisible => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsSchemaWarningVisible => !string.IsNullOrWhiteSpace(SchemaWarning);

    public bool CanEnableWriteMode => !IsOpenCodeRunning && ReadOnlyMode;

    public string ProjectCountDisplay => HasDatabase
        ? $"{FilteredProjects.Count} of {Projects.Count} project{(Projects.Count == 1 ? string.Empty : "s")}"
        : "No database loaded";

    public string DatabaseSizeDisplay => Snapshot is null
        ? "—"
        : DatabaseCandidate.FormatBytes(Snapshot.FileSizeBytes);

    public string BackupStatusDisplay => string.IsNullOrWhiteSpace(LastBackupPath)
        ? "Backup: created once before the first deletion"
        : $"Backup: {LastBackupPath}";

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
            SchemaWarning = snapshot.SchemaWarning;
            Projects.Clear();
            foreach (var project in snapshot.Projects)
            {
                Projects.Add(project);
            }

            LastBackupPath = _backupService.GetExistingBackup(path);
            ApplyFilter();

            var previousId = SelectedProject?.Id;
            SelectedProject = previousId is not null
                ? FilteredProjects.FirstOrDefault(project => project.Id == previousId)
                : FilteredProjects.FirstOrDefault();

            StatusMessage =
                $"Loaded {snapshot.Projects.Count} project{(snapshot.Projects.Count == 1 ? string.Empty : "s")} " +
                $"from {ProjectRecord.ToDisplayPath(path)}.";
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            Snapshot = null;
            Projects.Clear();
            ApplyFilter();
            SelectedProject = null;
            SelectedDetails = null;
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

        DeleteConfirmed = false;
        DeletePreviewText = BuildDeletePreview(SelectedProject, SelectedDetails);
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
    private async Task ShowDeleteSessionsConfirmationAsync()
    {
        if (!CanDeleteSessions || SelectedProject is null)
        {
            return;
        }

        if (!await EnsureDetailsLoadedAsync())
        {
            return;
        }

        DeleteSessionsConfirmed = false;
        DeleteSessionsPreviewText = BuildDeleteSessionsPreview(SelectedProject, SelectedDetails);
        IsDeleteSessionsConfirmationVisible = true;
        NotifyState();
    }

    [RelayCommand]
    private void CancelDeleteSessions()
    {
        IsDeleteSessionsConfirmationVisible = false;
        DeleteSessionsConfirmed = false;
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

        var project = SelectedProject;
        var path = DatabasePath;
        IsDeleteConfirmationVisible = false;
        DeleteConfirmed = false;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Deleting “{project.DisplayName}”…";

        var progress = new Progress<string>(message => StatusMessage = message);

        try
        {
            var backupPath = await Task.Run(() => _backupService.EnsureBackup(path, progress));
            LastBackupPath = backupPath;

            // Backup can take a long time for a large database. Re-check right before
            // the write transaction so a newly started OpenCode instance aborts it.
            if (await StopIfOpenCodeRunningAsync(
                    "OpenCode started while the backup was running. Deletion was cancelled; the backup is ready."))
            {
                StatusMessage = "Deletion cancelled: OpenCode is running.";
                return;
            }

            var result = await Task.Run(() =>
                new OpenCodeDatabase(path).DeleteProject(project.Id, backupPath, progress));

            await LoadAsync(force: true);

            StatusMessage =
                $"Deleted “{result.ProjectName}”: {result.TotalSessions:N0} session(s), " +
                $"{result.Messages:N0} message(s), {result.Worktrees:N0} worktree(s), " +
                $"{result.OrphanedBlobs:N0} orphaned blob(s). Backup: {result.BackupPath}";
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
    private async Task ConfirmDeleteSessionsAsync()
    {
        if (!CanConfirmDeleteSessions || SelectedProject is null)
        {
            return;
        }

        if (await StopIfOpenCodeRunningAsync(
                "OpenCode started while the confirmation dialog was open. Close it and try again."))
        {
            IsDeleteSessionsConfirmationVisible = false;
            return;
        }

        var project = SelectedProject;
        var path = DatabasePath;
        IsDeleteSessionsConfirmationVisible = false;
        DeleteSessionsConfirmed = false;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Deleting sessions of “{project.DisplayName}”…";

        var progress = new Progress<string>(message => StatusMessage = message);

        try
        {
            var backupPath = await Task.Run(() => _backupService.EnsureBackup(path, progress));
            LastBackupPath = backupPath;

            if (await StopIfOpenCodeRunningAsync(
                    "OpenCode started while the backup was running. Session deletion was cancelled; the backup is ready."))
            {
                StatusMessage = "Session deletion cancelled: OpenCode is running.";
                return;
            }

            var result = await Task.Run(() =>
                new OpenCodeDatabase(path).DeleteSessions(project.Id, backupPath, progress));

            await LoadAsync(force: true);

            StatusMessage =
                $"Deleted {result.TotalSessions:N0} session(s) from “{result.ProjectName}”. " +
                $"The project row was kept. Backup: {result.BackupPath}";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Session deletion failed. The transaction was rolled back.";
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
            LastBackupPath = await Task.Run(() => _backupService.EnsureBackup(DatabasePath, progress));
            StatusMessage = _backupService.GetExistingBackup(DatabasePath) is { } existing
                ? $"Backup ready: {existing}"
                : "Backup created.";
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

    partial void OnSelectedProjectChanged(ProjectRecord? value)
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

    partial void OnDeleteSessionsConfirmedChanged(bool value) =>
        OnPropertyChanged(nameof(CanConfirmDeleteSessions));

    partial void OnLastBackupPathChanged(string? value) => OnPropertyChanged(nameof(BackupStatusDisplay));

    private async Task LoadDetailsAsync(ProjectRecord? project)
    {
        var version = ++_detailsVersion;

        if (project is null)
        {
            SelectedDetails = null;
            IsDetailsLoading = false;
            return;
        }

        SelectedDetails = null;
        IsDetailsLoading = true;

        try
        {
            var details = await Task.Run(() => new OpenCodeDatabase(DatabasePath).LoadDetails(project.Id));

            if (version != _detailsVersion || SelectedProject?.Id != project.Id)
            {
                return;
            }

            SelectedDetails = details;
            if (IsDeleteConfirmationVisible)
            {
                DeletePreviewText = BuildDeletePreview(project, details);
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

        if (SelectedDetails is not null)
        {
            return true;
        }

        var project = SelectedProject;
        IsDetailsLoading = true;

        try
        {
            var details = await Task.Run(() => new OpenCodeDatabase(DatabasePath).LoadDetails(project.Id));
            if (SelectedProject?.Id != project.Id)
            {
                return false;
            }

            SelectedDetails = details;
            return true;
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

        FilteredProjects.Clear();
        foreach (var project in Projects)
        {
            if (query.Length > 0 && !Matches(project, query))
            {
                continue;
            }

            FilteredProjects.Add(project);
        }

        if (selectedId is not null && FilteredProjects.All(project => project.Id != selectedId))
        {
            SelectedProject = FilteredProjects.FirstOrDefault();
        }

        OnPropertyChanged(nameof(ProjectCountDisplay));
        OnPropertyChanged(nameof(IsEmptyProjectsVisible));
        NotifyState();
    }

    private static bool Matches(ProjectRecord project, string query) =>
        project.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        project.CanonicalPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        project.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (project.Vcs?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private string BuildDeleteSessionsPreview(ProjectRecord project, ProjectDetails? details)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Project: {project.DisplayName}");
        builder.AppendLine("Type: built-in global project");
        builder.AppendLine();
        builder.AppendLine($"Sessions (V2): {project.SessionCount:N0}");
        builder.AppendLine($"Legacy sessions: {project.LegacySessionCount:N0}");
        builder.AppendLine($"Messages: {(details?.TotalMessageCount ?? 0):N0}");
        builder.AppendLine($"Schedules: {(details?.ScheduleCount ?? 0):N0}");
        builder.AppendLine($"Jobs: {(details?.JobCount ?? 0):N0}");
        builder.AppendLine($"Event streams: {(details?.EventSequenceCount ?? 0):N0}");
        builder.AppendLine();
        builder.AppendLine("The global project row, its worktrees, permissions and directories are kept.");
        builder.AppendLine("The folder on disk is not touched.");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(LastBackupPath)
            ? "A one-time backup will be created before the deletion."
            : $"Existing backup for this run: {LastBackupPath}");

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
            ? "A one-time backup will be created before the deletion."
            : $"Existing backup for this run: {LastBackupPath}");

        return builder.ToString();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasDatabase));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanConfirmDelete));
        OnPropertyChanged(nameof(CanDeleteSessions));
        OnPropertyChanged(nameof(CanConfirmDeleteSessions));
        OnPropertyChanged(nameof(CanEnableWriteMode));
        OnPropertyChanged(nameof(IsNoDatabaseVisible));
        OnPropertyChanged(nameof(IsDetailsPlaceholderVisible));
        OnPropertyChanged(nameof(IsEmptyProjectsVisible));
        OnPropertyChanged(nameof(ProjectCountDisplay));
        OnPropertyChanged(nameof(DatabaseSizeDisplay));
        OnPropertyChanged(nameof(BackupStatusDisplay));
    }
}
