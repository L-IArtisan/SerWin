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
}
