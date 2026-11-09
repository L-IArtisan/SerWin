using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using SerWin.App.Infrastructure;
using SerWin.Core.Actions;
using SerWin.Core.Platform;
using SerWin.Core.Services;
using Wpf.Ui.Controls;

namespace SerWin.App.ViewModels;

/// <summary>v0.2 — Agir en sécurité : actions, confirmation, historique, annulation, restauration.</summary>
public sealed partial class MainViewModel
{
    private void InitActions()
    {
        ElevateCommand = new RelayCommand(Elevate, _ => !IsElevated);
        StartServiceCommand = new RelayCommand(async () => await ActOnSelected(ActionKind.Start), _ => CanActOnSelected && SelectedService!.IsStoppedLike);
        StopServiceCommand = new RelayCommand(async () => await ActOnSelected(ActionKind.Stop), _ => CanActOnSelected && SelectedService!.IsRunning);
        ApplyStartModeCommand = new RelayCommand(async () => await ActOnSelected(ActionKind.SetStartMode),
            _ => CanActOnSelected && SelectedService!.CanChangeStartMode && SelectedStartMode is not null && SelectedStartMode.Value != SelectedService.Info.StartMode);
        UndoCommand = new RelayCommand(async p => { if (p is Guid id) await UndoAsync(id); }, _ => IsElevated && !IsBusy);
        RestoreAllCommand = new RelayCommand(async () => await RestoreAllAsync(), _ => IsElevated && !IsBusy && _snapshot is not null);
        CreateRestorePointCommand = new RelayCommand(async () => await CreateRestorePointAsync(), _ => IsElevated && !IsBusy);
        ExportDataCommand = new RelayCommand(ExportData);
        DeleteDataCommand = new RelayCommand(DeleteData, _ => !IsBusy);
    }

    // ── État ─────────────────────────────────────────────────
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); } }

    private bool CanActOnSelected => IsElevated && !IsBusy && SelectedService is { IsBlocked: false };
    public bool ShowElevationBanner => !IsElevated;

    public ICommand ElevateCommand { get; private set; } = null!;
    public ICommand StartServiceCommand { get; private set; } = null!;
    public ICommand StopServiceCommand { get; private set; } = null!;
    public ICommand ApplyStartModeCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RestoreAllCommand { get; private set; } = null!;
    public ICommand CreateRestorePointCommand { get; private set; } = null!;
    public ICommand ExportDataCommand { get; private set; } = null!;
    public ICommand DeleteDataCommand { get; private set; } = null!;

    /// <summary>Choix du type de démarrage. « Automatique (différé) » n'apparaît qu'à partir du mode Avancé.</summary>
    public ObservableCollection<Option<ServiceStartMode>> StartModeOptions { get; } = [];

    private Option<ServiceStartMode>? _selectedStartMode;
    public Option<ServiceStartMode>? SelectedStartMode
    {
        get => _selectedStartMode;
        set { Set(ref _selectedStartMode, value); CommandManager.InvalidateRequerySuggested(); }
    }

    private void OnSelectedServiceChanged()
    {
        RebuildStartModeOptions();
        _ = AnalyzeSelectedAsync();
    }

    private void RebuildStartModeOptions()
    {
        StartModeOptions.Clear();
        var current = SelectedService?.Info.StartMode;
        foreach (var m in new[] { ServiceStartMode.Automatic, ServiceStartMode.AutomaticDelayed, ServiceStartMode.Manual, ServiceStartMode.Disabled })
        {
            if (m == ServiceStartMode.AutomaticDelayed && _ctx.Level == ComplexityLevel.Simple && current != m) continue;
            StartModeOptions.Add(new Option<ServiceStartMode>(m, $"StartMode_{m}"));
        }
        SelectedStartMode = StartModeOptions.FirstOrDefault(o => o.Value == current);
    }

    private void RefreshActionLabels()
    {
        RebuildStartModeOptions();
        foreach (var h in History) h.Refresh();
        Raise(nameof(SnapshotDiffText));
    }

    // ── Notifications ────────────────────────────────────────
    private bool _isNotificationOpen;
    public bool IsNotificationOpen { get => _isNotificationOpen; set => Set(ref _isNotificationOpen, value); }
    private string _notificationTitle = "";
    public string NotificationTitle { get => _notificationTitle; private set => Set(ref _notificationTitle, value); }
    private string _notificationMessage = "";
    public string NotificationMessage { get => _notificationMessage; private set => Set(ref _notificationMessage, value); }
    private InfoBarSeverity _notificationSeverity;
    public InfoBarSeverity NotificationSeverity { get => _notificationSeverity; private set => Set(ref _notificationSeverity, value); }

    private void Notify(InfoBarSeverity severity, string title, string message = "")
    {
        NotificationSeverity = severity;
        NotificationTitle = title;
        NotificationMessage = message;
        IsNotificationOpen = true;
    }

    // ── Élévation ────────────────────────────────────────────
    /// <summary>Relance SerWin en administrateur en conservant le contexte (service sélectionné, page).</summary>
    private void Elevate()
    {
        var args = new List<string>();
        if (SelectedService is { } s) args.Add($"--select \"{s.Name}\"");
        if (CurrentPage != "services") args.Add($"--page {CurrentPage}");
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args)) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // L'utilisateur a refusé l'invite UAC : on reste en mode consultation.
        }
    }

    private void TryHardenDataDirectory()
    {
        try
        {
            DataDirectory.Harden(DataDirectory.Default);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            Notify(InfoBarSeverity.Warning, L["Data_HardenFailed"], ex.Message);
        }
    }

    // ── Actions sur le service sélectionné ───────────────────
    private async Task ActOnSelected(ActionKind kind)
    {
        if (SelectedService is not { } svc) return;
        var request = new ActionRequest(svc.Name, kind, kind == ActionKind.SetStartMode ? SelectedStartMode?.Value : null);
        var plan = await Task.Run(() => _engine.Plan(request));

        var title = kind switch
        {
            ActionKind.Start => L.Format("History_Start", svc.DisplayName),
            ActionKind.Stop => L.Format("History_Stop", svc.DisplayName),
            _ => L.Format("History_SetStartMode", svc.DisplayName, SelectedStartMode?.Label),
        };

        var warnings = new List<string>();
        if (kind == ActionKind.SetStartMode && request.StartMode == ServiceStartMode.Disabled)
        {
            if (svc.IfDisabled.Count > 0) warnings.Add(L.Format("Warn_IfDisabled", string.Join(" · ", svc.IfDisabled)));
            if (svc.HasKnownRevivers) warnings.Add(L.Format("Warn_Revivers", svc.RevivedByText));
        }
        if (kind == ActionKind.Stop && svc.Info.StartMode != ServiceStartMode.Disabled)
            warnings.Add(L["Warn_StopTemporary"]);

        await RunPlanAsync(plan, title, L[$"Act_Confirm_{kind}"], warnings, danger: false);
    }

    private async Task UndoAsync(Guid id)
    {
        var item = History.FirstOrDefault(h => h.Id == id);
        var plan = await Task.Run(() => _engine.PlanUndo(id));
        await RunPlanAsync(plan, L.Format("Undo_Title", item?.Title), L["Act_Confirm_Undo"], [], danger: false);
    }

    private async Task RestoreAllAsync()
    {
        if (_snapshot is null) return;
        var snapshot = _snapshot;
        var plan = await Task.Run(() => _engine.PlanRestoreAll(snapshot));
        var intro = L.Format("RestoreAll_Intro", snapshot.TakenAt.LocalDateTime.ToString("g", L.Culture));
        await RunPlanAsync(plan, L["RestoreAll_Title"], L["Act_Confirm_RestoreAll"], [L["RestoreAll_Warning"]], danger: true,
            intro: intro, restorePointDefault: true);
    }

    /// <summary>Aperçu → confirmation → (point de restauration) → exécution → notification.</summary>
    private async Task RunPlanAsync(ActionPlan plan, string title, string confirmLabel, List<string> warnings, bool danger,
        string? intro = null, bool restorePointDefault = false)
    {
        if (plan.IsBlocked)
        {
            Notify(InfoBarSeverity.Error, L["Act_BlockedTitle"], L.Format($"Blocked_{plan.Blocked}", plan.BlockedDetail));
            return;
        }
        if (plan.IsNoOp)
        {
            Notify(InfoBarSeverity.Informational, L["Act_NoOp"]);
            return;
        }

        if (plan.DependentsToStop.Count > 0)
            warnings.Insert(0, L.Format("Warn_Dependents", string.Join(", ", plan.DependentsToStop.Select(DisplayNameOf))));
        if (plan.RequiresAcknowledgement) warnings.Insert(0, L["Warn_Orange"]);

        const int maxLines = 30;
        var lines = plan.Changes.SelectMany(c => HistoryItemViewModel.DescribeDetailed(c, DisplayNameOf)).Take(maxLines).ToList();
        var total = plan.Changes.Sum(c => HistoryItemViewModel.DescribeDetailed(c, DisplayNameOf).Count());
        if (total > maxLines) lines.Add(L.Format("Confirm_More", total - maxLines));

        var response = _dialogs.Confirm(new ConfirmRequest
        {
            Title = title,
            Intro = intro,
            Changes = lines,
            Warnings = warnings,
            RequireAcknowledgement = plan.RequiresAcknowledgement,
            OfferRestorePoint = true,
            RestorePointDefault = restorePointDefault || plan.RequiresAcknowledgement,
            ConfirmLabel = confirmLabel,
            Danger = danger,
        });
        if (!response.Confirmed) return;

        IsBusy = true;
        try
        {
            string? restoreNote = null;
            if (response.CreateRestorePoint)
            {
                var rp = await Task.Run(() => RestorePoint.Create($"SerWin : {title}"));
                restoreNote = L.Format($"RestorePoint_{rp.Outcome}", rp.Detail);
                if (rp.Outcome is RestorePointOutcome.Failed or RestorePointOutcome.Disabled)
                {
                    var go = _dialogs.Confirm(new ConfirmRequest
                    {
                        Title = L["RestorePoint_ContinueTitle"],
                        Intro = restoreNote,
                        ConfirmLabel = L["Action_Continue"],
                    });
                    if (!go.Confirmed) return;
                }
            }

            var result = await Task.Run(() => _engine.Execute(plan));
            await RefreshServicesAsync(plan.Changes.Select(c => c.Service));
            await RefreshHistoryAsync();
            await RefreshReviverDataAfterActionAsync();
            ReportResult(result, title, restoreNote);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportResult(ActionResult result, string title, string? restoreNote)
    {
        var extra = new List<string>();
        if (restoreNote is not null) extra.Add(restoreNote);
        if (result.Drifted.Count > 0)
            extra.Add(L.Format("Act_Drifted", string.Join(", ", result.Drifted.Select(d => $"{DisplayNameOf(d.Service)} ({L[$"StartMode_{d.Before}"]})"))));

        if (result.Error is { } error)
        {
            var reason = error == ServiceActionError.AccessDenied && IsElevated ? L["ActError_Protected"] : L[$"ActError_{error}"];
            var partial = result.Entry is not null ? " " + L["Act_PartiallyApplied"] : "";
            Notify(InfoBarSeverity.Error, L.Format("Act_Failed", DisplayNameOf(result.ErrorService ?? "")), reason + partial);
            return;
        }
        if (result.NotEffective.Count > 0)
        {
            Notify(InfoBarSeverity.Warning, L["Act_NotEffectiveTitle"],
                L.Format("Act_NotEffective", string.Join(", ", result.NotEffective.Select(c => DisplayNameOf(c.Service)))));
            return;
        }
        Notify(InfoBarSeverity.Success, L.Format("Act_Done", title), string.Join(" ", extra));
    }

    private async Task RefreshServicesAsync(IEnumerable<string> names)
    {
        var reader = new ServiceReader();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = await Task.Run(() => reader.Read(name));
            var item = Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (info is not null && item is not null) item.Update(info);
        }
        RebuildStartModeOptions();
        ApplyFilter();
        BuildSuggestions();
    }

    private string DisplayNameOf(string name) =>
        Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? name;

    // ── Historique ───────────────────────────────────────────
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];
    public bool HasHistory => History.Count > 0;

    private int? _snapshotDiffCount;
    public string SnapshotDiffText => _snapshotDiffCount switch
    {
        null => "",
        0 => L["Snapshot_NoDiff"],
        var n => L.Format("Snapshot_Diff", n),
    };

    public async Task RefreshHistoryAsync()
    {
        var entries = await Task.Run(() =>
        {
            try { return _engine.History.All(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
        });
        History.Clear();
        foreach (var e in entries) History.Add(new HistoryItemViewModel(e, DisplayNameOf, IsElevated));
        Raise(nameof(HasHistory));

        if (_snapshot is { } snap)
        {
            _snapshotDiffCount = await Task.Run(() => _engine.PlanRestoreAll(snap).Changes.Count);
            Raise(nameof(SnapshotDiffText));
        }
        RecomputeAlerts();
    }

    // ── Point de restauration, export, suppression ───────────
    private async Task CreateRestorePointAsync()
    {
        IsBusy = true;
        try
        {
            var rp = await Task.Run(() => RestorePoint.Create(L["RestorePoint_ManualDescription"]));
            Notify(rp.Outcome == RestorePointOutcome.Created ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                L.Format($"RestorePoint_{rp.Outcome}", rp.Detail));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ExportData()
    {
        if (!Directory.Exists(DataDirectory.Default))
        {
            Notify(InfoBarSeverity.Informational, L["Export_Nothing"]);
            return;
        }
        var path = _dialogs.SaveFile($"SerWin-{DateTime.Now:yyyyMMdd-HHmm}.zip", "ZIP (*.zip)|*.zip");
        if (path is null) return;
        try
        {
            DataDirectory.ExportZip(DataDirectory.Default, path);
            Notify(InfoBarSeverity.Success, L["Export_Done"], path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(InfoBarSeverity.Error, L["Export_Failed"], ex.Message);
        }
    }

    private void DeleteData()
    {
        var response = _dialogs.Confirm(new ConfirmRequest
        {
            Title = L["Delete_Title"],
            Intro = L["Delete_Intro"],
            Changes = [DataDirectory.Default, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerWin")],
            Warnings = [L["Delete_Warning"]],
            RequireAcknowledgement = true,
            ConfirmLabel = L["Delete_Confirm"],
            Danger = true,
        });
        if (!response.Confirmed) return;
        try
        {
            DataDirectory.DeleteAll(DataDirectory.Default);
            DataDirectory.DeleteAll(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerWin"));
            System.Windows.MessageBox.Show(L["Delete_Done"], "SerWin");
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(InfoBarSeverity.Error, L["Delete_Failed"], IsElevated ? ex.Message : L["Delete_NeedAdmin"]);
        }
    }
}
