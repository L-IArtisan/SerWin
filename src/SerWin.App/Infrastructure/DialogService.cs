using System.Windows;
using Microsoft.Win32;
using SerWin.App.Views;

namespace SerWin.App.Infrastructure;

/// <summary>Contenu d'une demande de confirmation : aperçu détaillé avant toute modification.</summary>
public sealed class ConfirmRequest
{
    public required string Title { get; init; }
    public string? Intro { get; init; }
    /// <summary>Ce qui va changer, ligne par ligne.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Liste orange : case « Je comprends » obligatoire.</summary>
    public bool RequireAcknowledgement { get; init; }
    public bool OfferRestorePoint { get; init; }
    public bool RestorePointDefault { get; init; }
    public required string ConfirmLabel { get; init; }
    public bool Danger { get; init; }
}

public sealed record ConfirmResponse(bool Confirmed, bool CreateRestorePoint);

public interface IDialogService
{
    ConfirmResponse Confirm(ConfirmRequest request);
    string? SaveFile(string defaultName, string filter);
}

public sealed class DialogService : IDialogService
{
    public ConfirmResponse Confirm(ConfirmRequest request)
    {
        var w = new ConfirmWindow(request) { Owner = Application.Current.MainWindow };
        var ok = w.ShowDialog() == true;
        return new ConfirmResponse(ok, ok && w.CreateRestorePoint);
    }

    public string? SaveFile(string defaultName, string filter)
    {
        var d = new SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true };
        return d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null;
    }
}
