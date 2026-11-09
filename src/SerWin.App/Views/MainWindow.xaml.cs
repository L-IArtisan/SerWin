using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SerWin.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ApplicationThemeManager.ApplySystemTheme();
        Loaded += (_, _) => SystemThemeWatcher.Watch(this);

        // Garde le service sélectionné visible (sélection initiale, changement de filtre…).
        ServiceList.SelectionChanged += (_, _) =>
        {
            if (ServiceList.SelectedItem is { } item) ServiceList.ScrollIntoView(item);
        };
    }

    /// <summary>Lien « Processus » depuis un service ou la page Réseau : on fait défiler l'arbre jusqu'au processus.</summary>
    private void OnProcessTreeItemSelected(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TreeViewItem item && ReferenceEquals(e.OriginalSource, item))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(item.BringIntoView));
    }
}
