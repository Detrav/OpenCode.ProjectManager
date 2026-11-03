using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenCode.ProjectManager.ViewModels;
using OpenCode.ProjectManager.Views;

namespace OpenCode.ProjectManager;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();

            // Optional first argument: a path to a specific opencode.db.
            if (desktop.Args is { Length: > 0 } args && !string.IsNullOrWhiteSpace(args[0]))
            {
                viewModel.DatabasePath = args[0];
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}