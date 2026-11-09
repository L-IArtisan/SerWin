using System.Windows.Media;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Context;
using SerWin.Core.Platform;
using SerWin.Core.Safety;
using SerWin.Core.Services;
using SerWin.Knowledge;

namespace SerWin.App.ViewModels;

/// <summary>Pastille affichée : 🟢 sûr · 🟠 prudence · 🔴 bloqué · ⚪ non évalué.</summary>
public enum DisplayRisk
{
    Safe,
    Caution,
    Blocked,
    Unknown,
}

/// <summary>Contexte partagé par toutes les lignes : niveau de complexité et système détecté.</summary>
public sealed class DisplayContext
{
    public ComplexityLevel Level { get; set; } = ComplexityLevel.Simple;
    public required OsInfo Os { get; init; }
    /// <summary>Contexte détecté (v0.5) ; null tant que la détection n'est pas terminée.</summary>
    public MachineContext? Machine { get; set; }
    public string Language => Loc.Instance.Language;
}

public sealed record CommandLine(string Label, string Command);

/// <summary>Un service tel qu'affiché : données observées + fiche de connaissance éventuelle.</summary>
public sealed class ServiceItemViewModel(ServiceInfo info, KnowledgeEntry? entry, DisplayContext ctx) : ObservableObject
{
    private static readonly Loc L = Loc.Instance;

    public ServiceInfo Info { get; private set; } = info;
    public KnowledgeEntry? Entry { get; } = entry;

    /// <summary>Remplace les données observées (après une action) et rafraîchit l'affichage.</summary>
    public void Update(ServiceInfo info)
    {
        Info = info;
        Raise(null);
    }

    // ── Onglet Agir ──────────────────────────────────────────
    public bool IsBlocked => Risk == DisplayRisk.Blocked;
    public bool IsStoppedLike => !IsRunning && Info.State != ServiceRunState.StartPending;
    public bool CanChangeStartMode => !IsBlocked && !Info.IsPerUserInstance &&
        Info.StartMode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed or ServiceStartMode.Manual or ServiceStartMode.Disabled;
    public bool HasKnownRevivers => Entry?.Revivers.Count > 0;
    public string RevivedByText => string.Join(", ", Entry?.Revivers.Select(ReviverLabel).Distinct() ?? []);

    /// <summary>Libellé lisible d'un mécanisme de relance de la base (« device-arrival » → « arrivée d'un périphérique »).</summary>
    internal static string ReviverLabel(Reviver r)
    {
        var key = r.Target.Replace('-', '_');
        return r.Type switch
        {
            "trigger" => L.TryGet($"TriggerTarget_{key}") ?? r.Target,
            "update" or "software" => L.TryGet($"Known_{key}") ?? r.Target,
            "policy" when r.Target is "domain-gpo" or "mdm" => L[$"Rev_Managed_{key}"],
            _ => r.Target,
        };
    }
    public string PerUserNote => Info.IsPerUserInstance ? L.Format("Act_PerUserInstance", Info.TemplateName) : "";
    public bool HasPerUserNote => Info.IsPerUserInstance;

    // ── v0.3 : désactivé puis relancé ─────────────────────────
    private bool _isRevived;
    public bool IsRevived { get => _isRevived; set => Set(ref _isRevived, value); }

    // ── v0.5 : « Chez toi » et réglage d'usine ────────────────
    public UsefulnessVerdict Verdict => Entry is null || ctx.Machine is null
        ? UsefulnessVerdict.NoRule
        : UsefulnessRules.Evaluate(Entry.UsefulIf, ctx.Machine);

    public bool HasVerdict => Verdict.Verdict != Usefulness.Unknown;
    public bool IsNotUseful => Verdict.Verdict == Usefulness.NotUseful;
    public string VerdictTitle => L[$"Verdict_{Verdict.Verdict}"];
    public Brush VerdictBackground => Verdict.Verdict == Usefulness.Useful ? Palette.SafeSoft : Palette.InfoSoft;

    public string VerdictText
    {
        get
        {
            var v = Verdict;
            if (v.Verdict == Usefulness.Useful)
                return L.Format("Verdict_Because", string.Join(" · ", v.Checks.Where(c => c.Satisfied == true).Select(DescribeFact)));
            if (v.Verdict == Usefulness.NotUseful)
                return Entry?.VerdictIfNot?.Resolve(ctx.Language)
                       ?? L.Format("Verdict_NotBecause", string.Join(" · ", v.Checks.Select(DescribeFact)));
            return "";
        }
    }

    internal static string DescribeFact(RuleCheck c)
    {
        var name = L[$"FactName_{c.Key}"];
        var value = c.Fact.Count switch
        {
            null => L["Fact_Unknown"],
            0 => L["Fact_No"],
            1 => L["Fact_Yes"],
            var n => n.ToString(),
        };
        return string.IsNullOrWhiteSpace(c.Fact.Detail) ? $"{name} : {value}" : $"{name} : {value} ({c.Fact.Detail})";
    }

    private bool _defaultExact;
    public DefaultConfig? Default => Entry?.DefaultFor(ctx.Os.EditionFamily, ctx.Os.Build, out _defaultExact);
    public bool HasDefault => Default is not null;
    public bool DiffersFromDefault => Default?.StartType is { } st && CanChangeStartMode && !string.Equals(st, Info.StartMode.ToString(), StringComparison.OrdinalIgnoreCase);
    public string DefaultText
    {
        get
        {
            if (Default?.StartType is not { } st) return L["Default_Unknown"];
            var text = L.Format(_defaultExact ? "Default_Exact" : "Default_Approx", L[$"StartMode_{st}"]);
            return DiffersFromDefault ? text + " " + L["Default_Differs"] : text;
        }
    }

    // ── v0.4 : consommation (processus hôte) ──────────────────
    private string? _consumption;
    public string? Consumption { get => _consumption; set { if (Set(ref _consumption, value)) Raise(nameof(HasConsumption)); } }
    public bool HasConsumption => Consumption is not null;

    // ── Identité ─────────────────────────────────────────────
    public string Name => Info.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(Info.DisplayName)
        ? Entry?.DisplayName.Resolve(ctx.Language) ?? Info.Name
        : Info.DisplayName;

    public bool IsDocumented => Entry is not null;
    public bool IsUndocumented => Entry is null;
    public bool IsRunning => Info.State == ServiceRunState.Running;
    public bool IsDisabled => Info.StartMode == ServiceStartMode.Disabled;
    public bool IsThirdParty => Info.IsThirdParty;
    public bool IsPrivacy => Entry?.Privacy == true || Entry?.Category == "privacy";
    public bool IsPerUser => Info.IsPerUserInstance || Info.IsPerUserTemplate;

    public string StateLabel => L[$"State_{Info.State}"];
    public string StartModeLabel => L[$"StartMode_{Info.StartMode}"] +
        (Info.TriggerStart && Info.StartMode is ServiceStartMode.Manual or ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed
            ? " " + L["StartMode_TriggerSuffix"] : "");

    public Brush StateBrush => Info.State switch
    {
        ServiceRunState.Running => Palette.Running,
        ServiceRunState.Stopped => Palette.Neutral,
        _ => Palette.Caution,
    };

    // ── Risque ───────────────────────────────────────────────
    public SafetyLevel Safety => SafetyLists.ForService(Info.TemplateName);

    public DisplayRisk Risk
    {
        get
        {
            if (Safety == SafetyLevel.Red || Entry?.Risk == RiskLevel.Critical) return DisplayRisk.Blocked;
            if (Entry is not null) return Entry.Risk == RiskLevel.Low ? DisplayRisk.Safe : DisplayRisk.Caution;
            return Safety == SafetyLevel.Orange ? DisplayRisk.Caution : DisplayRisk.Unknown;
        }
    }

    public Brush RiskBrush => Risk switch
    {
        DisplayRisk.Safe => Palette.Safe,
        DisplayRisk.Caution => Palette.Caution,
        DisplayRisk.Blocked => Palette.Blocked,
        _ => Palette.Neutral,
    };

    public string RiskLabel => L[$"Risk_{Risk}"];
    public string RiskExplanation => L[$"RiskExplain_{Risk}"];

    public string? SafetyNote => Safety switch
    {
        SafetyLevel.Red => L["Safety_Red"],
        SafetyLevel.Orange => L["Safety_Orange"],
        _ => null,
    };
    public bool HasSafetyNote => SafetyNote is not null;
    public Brush SafetyBackground => Safety == SafetyLevel.Red ? Palette.BlockedSoft : Palette.CautionSoft;

    // ── Onglet Comprendre ────────────────────────────────────
    public string Summary => Entry?.Summary.Resolve(ctx.Language) ?? Info.Description ?? L["Undocumented_NoDescription"];

    public string LevelText
    {
        get
        {
            if (Entry is null) return "";
            var t = ctx.Level switch
            {
                ComplexityLevel.Expert => Entry.Levels.Expert,
                ComplexityLevel.Avance => Entry.Levels.Avance,
                _ => Entry.Levels.Simple,
            };
            return t.Resolve(ctx.Language);
        }
    }

    public IReadOnlyList<string> IfDisabled => Entry?.IfDisabled.Select(t => t.Resolve(ctx.Language)).ToList() ?? [];
    public bool HasIfDisabled => IfDisabled.Count > 0;

    /// <summary>Mécanismes de relance connus de la base (aperçu de l'onglet « Qui le relance », v0.3).</summary>
    public IReadOnlyList<string> KnownRevivers => Entry?.Revivers.Select(FormatReviver).ToList() ?? [];
    public bool ShowRevivers => KnownRevivers.Count > 0 && ctx.Level != ComplexityLevel.Simple;

    private string FormatReviver(Reviver r)
    {
        var label = L[$"Reviver_{r.Type}"];
        var note = r.Note?.Resolve(ctx.Language);
        return note is null ? $"{label} : {r.Target}" : $"{label} : {r.Target} — {note}";
    }

    public bool IsUntranslated
    {
        get
        {
            if (Entry is null || ctx.Language == LocalizedText.SourceLanguage) return false;
            return !Entry.Summary.IsTranslated(ctx.Language)
                || !Entry.Levels.Simple.IsTranslated(ctx.Language)
                || !Entry.Levels.Avance.IsTranslated(ctx.Language)
                || !Entry.Levels.Expert.IsTranslated(ctx.Language);
        }
    }

    public string VerificationLabel => Entry is null ? "" : L[$"Verification_{Entry.Verification.Status}"];

    public string VerificationDetail => Entry?.Verification is { Build: not null } v
        ? L.Format("Verification_Detail", v.Edition, v.Build, v.Date)
        : "";

    /// <summary>« ⚠️ Fiche non vérifiée sur ta version » si le build ou l'édition diffère.</summary>
    public bool ShowVersionWarning => Entry is not null && !Entry.IsVerifiedFor(ctx.Os.EditionFamily, ctx.Os.Build);

    public string CategoryLabel => Entry is null ? "" : L[$"Category_{Entry.Category.Replace('-', '_')}"];

    // ── Données observées (toujours visibles, essentielles si non documenté) ───
    public string? WindowsDescription => Info.Description;
    public string? Executable => Info.ServiceDll ?? ExecutablePath.Extract(Info.BinaryPath);
    public string Publisher => Info.Publisher ?? L["Value_Unknown"];
    public string AccountLabel => Info.Account ?? L["Value_Unknown"];
    public string DependenciesText => Info.Dependencies.Count == 0 ? L["Value_None"] : string.Join(", ", Info.Dependencies);
    public string DependentsText => Info.DependentServices.Count == 0 ? L["Value_None"] : string.Join(", ", Info.DependentServices);
    public IReadOnlyList<string> TriggerLines => Info.Triggers.Select(FormatTrigger).ToList();
    public bool HasTriggers => Info.Triggers.Count > 0;
    public string TriggerSummary => HasTriggers ? L.Format("Triggers_Count", Info.Triggers.Count) : L["Value_None"];

    internal static string FormatTrigger(ServiceTrigger t)
    {
        var action = L[$"TriggerAction_{t.Action}"];
        var kind = L[$"TriggerKind_{t.Kind}"];
        var sub = t.SubtypeName is { } n ? L[$"TriggerSub_{n}"] : t.Subtype?.ToString();
        var data = t.Data.Count > 0 ? $" [{string.Join(", ", t.Data)}]" : "";
        return sub is null ? $"{action} — {kind}{data}" : $"{action} — {kind} : {sub}{data}";
    }

    // ── Onglet Technique ─────────────────────────────────────
    public bool ShowTechnical => ctx.Level != ComplexityLevel.Simple;
    public string? BinaryPath => Info.BinaryPath;
    public string? ServiceDll => Info.ServiceDll;
    public string RegistryPath => Info.RegistryPath;
    public string ServiceTypeText => $"0x{Info.ServiceType:X} ({(Info.IsSharedProcess ? L["ServiceType_Shared"] : L["ServiceType_Own"])}" +
        (Info.IsPerUserInstance ? ", " + L["ServiceType_PerUserInstance"] : Info.IsPerUserTemplate ? ", " + L["ServiceType_PerUserTemplate"] : "") + ")";
    public string ProcessIdText => Info.ProcessId == 0 ? "—" : Info.ProcessId.ToString();
    public string ErrorControlText => L[$"ErrorControl_{Math.Min(Info.ErrorControl, 3)}"];
    public string? LoadOrderGroup => Info.LoadOrderGroup;
    public string? Sddl => Info.RegistryKeySddl;
    public bool ConfigUnreadable => !Info.ConfigReadable;

    public IReadOnlyList<string> RecoveryLines
    {
        get
        {
            var r = Info.Recovery;
            if (r is null || r.Actions.Count == 0) return [L["Recovery_None"]];
            var lines = r.Actions.Select((a, i) => L.Format("Recovery_Line", i + 1, L[$"RecoveryAction_{a.Type}"], a.DelayMs / 1000.0)).ToList();
            lines.Add(L.Format("Recovery_Reset", FormatDuration(r.ResetPeriodSeconds)));
            if (r.Command is not null) lines.Add(L.Format("Recovery_Command", r.Command));
            return lines;
        }
    }

    /// <summary>Durée lisible : « 24 h », « 3 j », « 15 min », « 0 s ».</summary>
    internal static string FormatDuration(uint seconds)
    {
        if (seconds == uint.MaxValue) return "∞";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 2 && t.TotalDays % 1 == 0) return $"{t.TotalDays:0} {L["Unit_Days"]}";
        if (t.TotalHours >= 1 && t.TotalHours % 1 == 0) return $"{t.TotalHours:0} h";
        if (t.TotalMinutes >= 1 && t.TotalMinutes % 1 == 0) return $"{t.TotalMinutes:0} min";
        return $"{seconds} s";
    }

    public IReadOnlyList<CommandLine> Commands =>
        CommandEquivalents.ForRead(Info).Select(c => new CommandLine(L[$"Cmd_{c.Purpose}"], c.Command)).ToList();

    // ── Recherche ────────────────────────────────────────────
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        bool Has(string? s) => s?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
        return Has(Info.Name) || Has(Info.DisplayName) || Has(Info.Description) || Has(Info.Publisher)
            || (Entry is not null && (Entry.DisplayName.Values.Any(Has) || Entry.Summary.Values.Any(Has)));
    }

    /// <summary>Rafraîchit tous les libellés (changement de langue ou de niveau).</summary>
    public void Refresh() => Raise(null);
}

internal static class Palette
{
    public static readonly Brush Safe = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xA3, 0x4D)));
    public static readonly Brush Caution = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0x1A)));
    public static readonly Brush Blocked = Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)));
    public static readonly Brush Running = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x88, 0xD8)));
    public static readonly Brush Neutral = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    public static readonly Brush CautionSoft = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xE8, 0x8A, 0x1A)));
    public static readonly Brush BlockedSoft = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xD1, 0x34, 0x38)));
    public static readonly Brush SafeSoft = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x3F, 0xA3, 0x4D)));
    public static readonly Brush InfoSoft = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x2B, 0x88, 0xD8)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
