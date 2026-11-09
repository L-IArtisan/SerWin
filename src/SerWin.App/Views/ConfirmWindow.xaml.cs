using System.Windows;
using SerWin.App.Infrastructure;
using Wpf.Ui.Controls;

namespace SerWin.App.Views;

public partial class ConfirmWindow : FluentWindow
{
    private readonly ConfirmRequest _request;

    public bool CreateRestorePoint => RestorePointBox.IsChecked == true;

    public ConfirmWindow(ConfirmRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = request.Title;
        TitleText.Text = request.Title;
        IntroText.Text = request.Intro ?? "";
        IntroText.Visibility = request.Intro is null ? Visibility.Collapsed : Visibility.Visible;
        ChangesList.ItemsSource = request.Changes;
        WarningsList.ItemsSource = request.Warnings;
        RestorePointBox.Visibility = request.OfferRestorePoint ? Visibility.Visible : Visibility.Collapsed;
        RestorePointBox.IsChecked = request.RestorePointDefault;
        AcknowledgeBox.Visibility = request.RequireAcknowledgement ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = request.ConfirmLabel;
        if (request.Danger) ConfirmButton.Appearance = ControlAppearance.Danger;
        UpdateConfirm();
    }

    private void UpdateConfirm() =>
        ConfirmButton.IsEnabled = !_request.RequireAcknowledgement || AcknowledgeBox.IsChecked == true;

    private void OnAckChanged(object sender, RoutedEventArgs e) => UpdateConfirm();

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
