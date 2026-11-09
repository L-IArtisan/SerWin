using System.Windows.Input;
using SerWin.App.Infrastructure;

namespace SerWin.App.ViewModels;

/// <summary>v0.6 — Avancé / Expert : neutralisation complète, verrouillage ACL, écran d'accueil.</summary>
public sealed partial class MainViewModel
{
    public ICommand NeutralizeCommand { get; private set; } = null!;
    public ICommand LockCommand { get; private set; } = null!;
    public ICommand UnlockCommand { get; private set; } = null!;

    public bool ShowNeutralize => _ctx.Level != ComplexityLevel.Simple;
    public bool ShowLock => _ctx.Level == ComplexityLevel.Expert;

    private void InitExpert()
    {
        NeutralizeCommand = new RelayCommand(async () => await NeutralizeSelectedAsync(),
            _ => CanActOnSelected && _ctx.Level != ComplexityLevel.Simple && SelectedService!.CanChangeStartMode);
        LockCommand = new RelayCommand(async () => await LockSelectedAsync(),
            _ => CanActOnSelected && _ctx.Level == ComplexityLevel.Expert && SelectedService!.CanChangeStartMode && !SelectedService.Info.IsAclLocked);
        // Le déverrouillage reste toujours possible, quel que soit le niveau : on ne doit jamais rester coincé.
        UnlockCommand = new RelayCommand(async () => await UnlockSelectedAsync(),
            _ => IsElevated && !IsBusy && SelectedService is { Info.IsAclLocked: true });
    }

    private void RefreshExpertLabels()
    {
        Raise(nameof(ShowNeutralize));
        Raise(nameof(ShowLock));
    }

    private async Task NeutralizeSelectedAsync()
    {
        if (SelectedService is not { } svc) return;
        var plan = await Task.Run(() => _engine.PlanNeutralize(svc.Name));
        var warnings = new List<string> { L["Warn_Neutralize"] };
        if (svc.IfDisabled.Count > 0) warnings.Add(L.Format("Warn_IfDisabled", string.Join(" · ", svc.IfDisabled)));
        if (_taskIndex.Tasks.Count > 0 && !IsElevated) warnings.Add(L["Warn_TasksPartial"]);
        await RunPlanAsync(plan, L.Format("History_Neutralize", svc.DisplayName), L["Act_Confirm_Neutralize"], warnings,
            danger: true, intro: L["Neutralize_Intro"], restorePointDefault: true);
    }

    private async Task LockSelectedAsync()
    {
        if (SelectedService is not { } svc) return;
        var plan = await Task.Run(() => _engine.PlanLock(svc.Name));
        var warnings = new List<string> { L["Warn_Lock"] };
        if (svc.HasKnownRevivers) warnings.Add(L.Format("Warn_LockRevivers", svc.RevivedByText));
        await RunPlanAsync(plan, L.Format("History_LockAcl", svc.DisplayName), L["Act_Confirm_Lock"], warnings,
            danger: true, intro: L["Lock_Intro"], restorePointDefault: true);
    }

    private async Task UnlockSelectedAsync()
    {
        if (SelectedService is not { } svc) return;
        var plan = await Task.Run(() => _engine.PlanUnlock(svc.Name));
        await RunPlanAsync(plan, L.Format("History_UnlockAcl", svc.DisplayName), L["Act_Confirm_Unlock"], [], danger: false);
    }

    /// <summary>Écran d'accueil (spec §2) : applique le niveau correspondant au profil choisi.</summary>
    public void ApplyWelcomeChoice(ComplexityLevel level)
    {
        SelectedLevel = Levels.First(l => l.Value == level);
        _settings.WelcomeDone = true;
        _settings.Save();
    }
}
