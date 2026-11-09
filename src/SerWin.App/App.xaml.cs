using System.Windows;
using System.Windows.Threading;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.App.ViewModels;
using SerWin.App.Views;

namespace SerWin.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        Loc.Instance.SetLanguage(settings.Language);

        var vm = new MainViewModel(settings, new DialogService()) { InitialSelection = ArgValue(e.Args, "--select") };
        var page = ArgValue(e.Args, "--page");
        if (page is "history" or "settings" or "alerts" or "processes" or "suggestions" or "network") vm.CurrentPage = page;
        if (ArgValue(e.Args, "--search") is { } search) vm.SearchText = search;
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        _ = vm.LoadAsync();

        // Premier lancement : choix du profil (spec §2). Sans choix explicite, le mode Simple s'applique.
        if (!settings.WelcomeDone)
        {
            var welcome = new WelcomeWindow { Owner = window };
            welcome.ShowDialog();
            vm.ApplyWelcomeChoice(welcome.Choice);
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Loc.Instance.Format("Error_Unexpected", e.Exception.Message), "SerWin",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
