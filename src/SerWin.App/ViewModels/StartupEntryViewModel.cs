using System.Diagnostics;
using System.Windows.Media;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Services;
using SerWin.Core.Startup;
using SerWin.Core.Tasks;
using SerWin.Knowledge;

namespace SerWin.App.ViewModels;

public enum StartupEntryKind
{
    /// <summary>Programme lancé à l'ouverture de session (clé Run ou dossier Démarrage).</summary>
    Program,
    /// <summary>Tâche planifiée déclenchée au démarrage ou à l'ouverture de session.</summary>
    Task,
    /// <summary>Service d'un éditeur tiers en démarrage automatique.</summary>
    Service,
}

/// <summary>
/// Un élément lancé au démarrage, avec ce que SerWin en sait (fiche processus, logiciel d'origine, éditeur, rôle probable)
/// et une piste d'optimisation éventuelle : désactiver (programme, tâche) ou différer (service).
/// </summary>
public sealed class StartupEntryViewModel : ObservableObject
{
    private static readonly Loc L = Loc.Instance;
    private readonly DisplayContext _ctx;

    public StartupEntryKind Kind { get; }
    public string Name { get; }
    public string? Executable { get; }
    public string Command { get; }
    public string SourceText { get; }

    /// <summary>Programme : valeur StartupApproved ; tâche : activée ; service : démarrage automatique.</summary>
    public bool Enabled { get; }
    public DateTime? DisabledAt { get; }

    public StartupItem? Item { get; }
    public ScheduledTaskInfo? Task { get; }
    public ServiceItemViewModel? Service { get; }

    public string? FileDescription { get; }
    public string? Publisher { get; }
    public InstalledProgram? InstalledProgram { get; }
    public ServiceRole Role { get; }
    public string? RoleClue { get; }
    public bool IsSecurity { get; }
    public KnowledgeEntry? ProcessEntry { get; }

    /// <summary>Exécutable dans le dossier Windows (barre de Sécurité Windows…) : jamais proposé à la désactivation.</summary>
    public bool IsWindowsComponent { get; }

    /// <summary>Ralentissement attribué par Windows lors des derniers démarrages (mode administrateur).</summary>
    public TimeSpan? Degradation { get; set; }

    private StartupEntryViewModel(StartupEntryKind kind, string name, string? exe, string command, string source, bool enabled,
        DisplayContext ctx, KnowledgeBase kb, DateTime? disabledAt = null, StartupItem? item = null, ScheduledTaskInfo? task = null,
        ServiceItemViewModel? service = null)
    {
        _ctx = ctx;
        Kind = kind;
        Name = name;
        Executable = exe;
        Command = command;
        SourceText = source;
        Enabled = enabled;
        DisabledAt = disabledAt;
        Item = item;
        Task = task;
        Service = service;

        if (exe is not null && File.Exists(exe))
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                FileDescription = string.IsNullOrWhiteSpace(v.FileDescription) ? null : v.FileDescription.Trim();
                Publisher = string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim();
            }
            catch (FileNotFoundException)
            {
                // Fichier disparu entre-temps.
            }
        }
        Publisher ??= service?.Info.Publisher;
        InstalledProgram = ctx.Programs?.FindFor(exe);
        ProcessEntry = exe is null ? null : kb.FindProcess(Path.GetFileName(exe));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        IsWindowsComponent = exe is not null && exe.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)
                             && Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

        // Rôle probable et produit de sécurité : même logique que les fiches déduites des services.
        var probe = new ServiceInfo
        {
            Name = name, DisplayName = FileDescription ?? name, BinaryPath = exe, Publisher = Publisher,
            Description = service?.Info.Description,
        };
        var deduced = ServiceDeduction.Deduce(probe, ctx.Programs);
        Role = deduced.Role;
        RoleClue = deduced.RoleClue;
        IsSecurity = deduced.IsSecurityProduct || Role == ServiceRole.Security || service?.Entry?.Category == "security";
    }

    public static StartupEntryViewModel FromProgram(StartupItem item, DisplayContext ctx, KnowledgeBase kb) =>
        new(StartupEntryKind.Program, item.Name, item.Executable, item.Command, L[$"StartupSource_{item.Source}"], item.Enabled, ctx, kb,
            item.DisabledAt, item: item);

    public static StartupEntryViewModel FromTask(ScheduledTaskInfo task, DisplayContext ctx, KnowledgeBase kb)
    {
        var exec = task.Actions.FirstOrDefault(a => a.Kind == TaskActionKind.Exec);
        var command = exec is null ? "" : string.IsNullOrWhiteSpace(exec.Arguments) ? exec.Path ?? "" : $"\"{exec.Path}\" {exec.Arguments}";
        return new(StartupEntryKind.Task, task.Name, exec?.Path, command, task.Path, task.Enabled, ctx, kb, task: task);
    }

    public static StartupEntryViewModel FromService(ServiceItemViewModel svc, DisplayContext ctx, KnowledgeBase kb) =>
        new(StartupEntryKind.Service, svc.DisplayName, ExecutablePath.Extract(svc.Info.BinaryPath), svc.Info.BinaryPath ?? "",
            svc.Name, svc.Info.StartMode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed, ctx, kb, service: svc);

    // ── Liste ────────────────────────────────────────────────
    public string Title => Kind == StartupEntryKind.Service ? Name : FileDescription is { } d && !d.Equals(Name, StringComparison.OrdinalIgnoreCase) ? $"{Name} — {d}" : Name;
    public string KindLabel => L[$"StartupKind_{Kind}"];
    public string Subtitle => string.Join(" · ", new[] { KindLabel, Publisher ?? InstalledProgram?.Publisher, StateLabel }.Where(s => !string.IsNullOrEmpty(s)));
    public bool IsDelayed => Service?.Info.StartMode == ServiceStartMode.AutomaticDelayed;
    public string StateLabel => Kind == StartupEntryKind.Service
        ? L[IsDelayed ? "Startup_StateDelayed" : "Startup_StateAuto"]
        : L[Enabled ? "Startup_StateOn" : "Startup_StateOff"];
    public Brush StateBrush => !Enabled ? Palette.Unknown : IsSuggested ? Palette.Caution : Palette.Safe;

    // ── Piste d'optimisation ─────────────────────────────────
    private static readonly HashSet<ServiceRole> NeededAtStartup =
        [ServiceRole.Security, ServiceRole.Hardware, ServiceRole.Audio, ServiceRole.Bluetooth, ServiceRole.Graphics, ServiceRole.Printing, ServiceRole.Vpn];

    /// <summary>
    /// Proposé : programme ou tâche actifs qui ne relèvent ni de la sécurité, ni du matériel, ni de Windows ;
    /// service tiers en démarrage automatique non différé (hors sécurité). Un ralentissement mesuré par Windows suffit.
    /// </summary>
    public bool IsSuggested => Kind switch
    {
        StartupEntryKind.Service => Enabled && !IsDelayed && !IsSecurity,
        _ => Enabled && !IsSecurity && !IsWindowsComponent && (!NeededAtStartup.Contains(Role) || Degradation is { TotalSeconds: >= 1 }),
    };
    public string SuggestionText => L[Kind == StartupEntryKind.Service ? "Startup_SuggestDelay" : "Startup_SuggestDisable"];

    // ── Fiche ────────────────────────────────────────────────
    public string Summary => ProcessEntry?.Summary.Resolve(_ctx.Language)
        ?? Service?.Summary
        ?? (InstalledProgram is { } p ? L.Format("Startup_SummaryProgram", L[$"Role_{Role}"], p.Name)
            : Publisher is { } pub ? L.Format("Startup_SummaryPublisher", L[$"Role_{Role}"], pub)
            : L["Startup_SummaryUnknown"]);

    public IReadOnlyList<string> Facts
    {
        get
        {
            var lines = new List<string>();
            if (InstalledProgram is { } p) lines.Add(L.Format("Deduced_Program", string.IsNullOrWhiteSpace(p.Version) ? p.Name : $"{p.Name} {p.Version}"));
            if (Publisher is { } pub) lines.Add(L.Format("Deduced_Publisher", pub));
            if (Role != ServiceRole.Unknown && ProcessEntry is null && Service?.Entry is null) lines.Add(L.Format("Deduced_Role", L[$"Role_{Role}"], RoleClue));
            if (Degradation is { } d && d.TotalSeconds >= 0.1) lines.Add(L.Format("Startup_Degradation", d.TotalSeconds.ToString("0.0", L.Culture)));
            if (!Enabled && DisabledAt is { } at) lines.Add(L.Format("Startup_DisabledSince", at.ToString("g", L.Culture)));
            return lines;
        }
    }

    public string Advice => IsSecurity ? L["Startup_Advice_Security"]
        : IsWindowsComponent ? L["Startup_Advice_Windows"]
        : Kind == StartupEntryKind.Service ? L["Startup_Advice_Service"]
        : Role switch
        {
            ServiceRole.Updater => L["Startup_Advice_Updater"],
            ServiceRole.Hardware or ServiceRole.Audio or ServiceRole.Bluetooth or ServiceRole.Graphics or ServiceRole.Printing => L["Startup_Advice_Hardware"],
            ServiceRole.Sync => L["Startup_Advice_Sync"],
            ServiceRole.Vpn => L["Startup_Advice_Vpn"],
            ServiceRole.RemoteAccess => L["Startup_Advice_Remote"],
            _ => L["Startup_Advice_Default"],
        };

    public bool ShowCommand => _ctx.Level != ComplexityLevel.Simple;
    public bool ShowSource => _ctx.Level == ComplexityLevel.Expert;
    public string SourceDetail => Item?.ApprovalTarget.Replace("|", " → ") ?? Task?.Path ?? Service?.Info.RegistryPath ?? "";

    public bool CanToggle => Kind != StartupEntryKind.Service;
    public bool CanDelay => Kind == StartupEntryKind.Service && Enabled;
    public string ToggleLabel => L[Kind == StartupEntryKind.Task
        ? Enabled ? "Startup_DisableTask" : "Startup_EnableTask"
        : Enabled ? "Startup_Disable" : "Startup_Enable"];
    public string DelayLabel => L[IsDelayed ? "Startup_Undelay" : "Startup_Delay"];
    public bool HasService => Service is not null;
    public string? ExecutableName => Executable is null ? null : Path.GetFileName(Executable);

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        bool Has(string? s) => s?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
        return Has(Name) || Has(FileDescription) || Has(Publisher) || Has(Command) || Has(InstalledProgram?.Name);
    }

    public void Refresh() => Raise(null);
}
