using System.Windows;
using SerWin.App.Infrastructure;
using Wpf.Ui.Controls;

namespace SerWin.App.Views;

/// <summary>Écran d'accueil (spec §2) : « Tu es plutôt curieux / bidouilleur / expert ? ».</summary>
public partial class WelcomeWindow : FluentWindow
{
    /// <summary>Niveau choisi ; Simple si la fenêtre est fermée sans choix (mode par défaut de la spec).</summary>
    public ComplexityLevel Choice { get; private set; } = ComplexityLevel.Simple;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ComplexityLevel>(tag, out var level)) Choice = level;
        DialogResult = true;
    }
}
