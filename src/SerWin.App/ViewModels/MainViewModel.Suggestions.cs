using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Actions;
using SerWin.Core.Context;
using SerWin.Core.Services;

namespace SerWin.App.ViewModels;

/// <summary>Une suggestion à cocher (spec §5.7) : un service jugé inutile ici, avec son explication.</summary>
public sealed class SuggestionItemViewModel(ServiceItemViewModel service, bool privacy, Action onChanged) : ObservableObject
{
    private static readonly Loc L = Loc.Instance;

    public ServiceItemViewModel Service { get; } = service;
    public string Name => Service.Name;
    public string Title => Service.DisplayName;
    public bool IsPrivacy { get; } = privacy;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value)) onChanged();
        }
    }

    /// <summary>Pourquoi SerWin le suggère : verdict « inutile chez toi », ou résumé vie privée.</summary>
    public string Reason => Service.IsNotUseful ? Service.VerdictText : Service.Summary;
    public string Consequences => Service.IfDisabled.Count == 0 ? "" : L.Format("Sugg_Consequences", string.Join(" · ", Service.IfDisabled));
    public bool HasConsequences => Consequences.Length > 0;
    public string RevivedWarning => Service.HasKnownRevivers ? L.Format("Sugg_Revivers", Service.RevivedByText) : "";
    public bool HasRevivedWarning => Service.HasKnownRevivers;
    public string StateText => $"{Service.StateLabel} · {Service.StartModeLabel}";
    public Brush RiskBrush => Service.RiskBrush;
    public string RiskLabel => Service.RiskLabel;

    public void Refresh() => Raise(null);
}

/// <summary>Un fait détecté, affiché en toute transparence.</summary>
public sealed record FactItem(string Name, string Value);

/// <summary>v0.5 — Chez toi : contexte, verdicts, suggestions, comparaison avec les réglages d'usine.</summary>
public sealed partial class MainViewModel
{
    private MachineContext? _machine;

    public ObservableCollection<SuggestionItemViewModel> UselessSuggestions { get; } = [];
    public ObservableCollection<SuggestionItemViewModel> PrivacySuggestions { get; } = [];
    public ObservableCollection<FactItem> DetectedFacts { get; } = [];

    public bool IsSuggestionsPage => CurrentPage == "suggestions";
    public bool IsContextLoaded => _machine is not null;
    public bool HasUselessSuggestions => UselessSuggestions.Count > 0;
    public bool HasPrivacySuggestions => PrivacySuggestions.Count > 0;
    public int CheckedSuggestionCount => UselessSuggestions.Concat(PrivacySuggestions).Count(s => s.IsChecked);
    public string ApplySuggestionsLabel => L.Format("Sugg_Apply", CheckedSuggestionCount);
    public string SuggestionsNavLabel => UselessSuggestions.Count + PrivacySuggestions.Count is var n and > 0 ? $"{L["Nav_Suggestions"]} ({n})" : L["Nav_Suggestions"];

    private bool _stopNow = true;
    public bool StopNow { get => _stopNow; set => Set(ref _stopNow, value); }

    public string DefaultsSummaryText
    {
        get
        {
            var known = Services.Count(s => s.HasDefault);
            if (known == 0) return L.Format("Default_NoneKnown", $"{Os.EditionFamily}:{Os.Build}");
            var differ = Services.Count(s => s.DiffersFromDefault);
            return differ == 0 ? L.Format("Default_AllMatch", known) : L.Format("Default_SomeDiffer", differ, known);
        }
    }

    public ICommand ApplySuggestionsCommand { get; private set; } = null!;

    private void InitSuggestions()
    {
        ApplySuggestionsCommand = new RelayCommand(async () => await ApplySuggestionsAsync(),
            _ => IsElevated && !IsBusy && CheckedSuggestionCount > 0);
    }

    /// <summary>Détection du contexte en arrière-plan (≈ 1 s), puis verdicts et suggestions.</summary>
    private async Task LoadContextAsync()
    {
        _machine = await Task.Run(() => MachineContext.Detect());
        _ctx.Machine = _machine;
        foreach (var s in Services) s.Refresh();
        RebuildFacts();
        Raise(nameof(IsContextLoaded));
        BuildSuggestions();
        if (SelectedFilter.Value is ServiceFilter.NotUseful or ServiceFilter.DiffersFromDefault) ApplyFilter();
    }

    private void RebuildFacts()
    {
        DetectedFacts.Clear();
        if (_machine is null) return;
        foreach (var key in MachineContext.Keys.All)
        {
            var text = ServiceItemViewModel.DescribeFact(new RuleCheck(key, "", _machine[key], null));
            var sep = text.IndexOf(" : ", StringComparison.Ordinal);
            DetectedFacts.Add(new FactItem(text[..sep], text[(sep + 3)..]));
        }
    }

    /// <summary>(Re)construit les listes en conservant les cases déjà cochées.</summary>
    private void BuildSuggestions()
    {
        if (_machine is null) return;
        var checkedNames = UselessSuggestions.Concat(PrivacySuggestions).Where(s => s.IsChecked).Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Eligible(ServiceItemViewModel s) => !s.IsBlocked && !s.IsDisabled && !s.Info.IsPerUserInstance && s.CanChangeStartMode;
        var useless = Services.Where(s => s.IsNotUseful && Eligible(s)).OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var privacy = Services.Where(s => s.IsPrivacy && Eligible(s) && s.Verdict.Verdict != Usefulness.Useful && !useless.Contains(s))
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

        void Fill(ObservableCollection<SuggestionItemViewModel> target, IEnumerable<ServiceItemViewModel> items, bool isPrivacy)
        {
            target.Clear();
            foreach (var s in items)
                target.Add(new SuggestionItemViewModel(s, isPrivacy, OnSuggestionChecked) { IsChecked = checkedNames.Contains(s.Name) });
        }
        Fill(UselessSuggestions, useless, false);
        Fill(PrivacySuggestions, privacy, true);

        Raise(nameof(HasUselessSuggestions));
        Raise(nameof(HasPrivacySuggestions));
        Raise(nameof(SuggestionsNavLabel));
        Raise(nameof(DefaultsSummaryText));
        OnSuggestionChecked();
    }

    private void OnSuggestionChecked()
    {
        Raise(nameof(CheckedSuggestionCount));
        Raise(nameof(ApplySuggestionsLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ApplySuggestionsAsync()
    {
        var names = UselessSuggestions.Concat(PrivacySuggestions).Where(s => s.IsChecked).Select(s => s.Name).ToList();
        if (names.Count == 0) return;
        var stopNow = StopNow;
        List<(string Service, BlockReason Reason)> skipped = [];
        var plan = await Task.Run(() => _engine.PlanDisableMany(names, stopNow, out skipped));

        var warnings = new List<string>();
        if (skipped.Count > 0)
            warnings.Add(L.Format("Sugg_Skipped", string.Join(", ", skipped.Select(s => DisplayNameOf(s.Service)))));
        var revived = UselessSuggestions.Concat(PrivacySuggestions).Where(s => s.IsChecked && s.HasRevivedWarning).Select(s => s.Title).ToList();
        if (revived.Count > 0) warnings.Add(L.Format("Sugg_RevivedSome", string.Join(", ", revived)));
        if (!stopNow) warnings.Add(L["Sugg_NotStoppedNow"]);

        await RunPlanAsync(plan, L.Format("History_Suggestions", names.Count - skipped.Count), L["Sugg_Confirm"], warnings, danger: false,
            intro: L["Sugg_Intro"]);
        BuildSuggestions();
    }
}
