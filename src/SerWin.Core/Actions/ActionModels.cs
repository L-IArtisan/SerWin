using System.Text.Json.Serialization;
using SerWin.Core.Safety;
using SerWin.Core.Services;

namespace SerWin.Core.Actions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind
{
    /// <summary>Type de démarrage (Automatic, Manual, Disabled…).</summary>
    StartMode,
    /// <summary>État d'exécution (Running / Stopped).</summary>
    State,
    /// <summary>Déclencheurs du service (v0.6) : Before/After = liste JSON complète (données brutes incluses).</summary>
    Triggers,
    /// <summary>Actions de récupération (v0.6) : Before/After = configuration JSON.</summary>
    Recovery,
    /// <summary>Tâche planifiée liée activée / désactivée (v0.6) : Target = chemin de la tâche, Before/After = True/False.</summary>
    TaskEnabled,
    /// <summary>Sécurité de la clé de registre du service (v0.6, verrouillage) : Before/After = SDDL (section Access).</summary>
    RegistryAcl,
    /// <summary>Réglage d'un journal d'événements (v1.1, journal DNS) : Target = nom du journal, Before/After = EventLogSetting encodé.</summary>
    EventLog,
}

/// <summary>
/// Une modification élémentaire d'un service. Before/After sont des valeurs lisibles (noms d'énumération, JSON, SDDL).
/// <paramref name="Target"/> précise l'objet modifié quand ce n'est pas le service lui-même (chemin de tâche planifiée).
/// </summary>
public sealed record ServiceChange(string Service, ChangeKind Kind, string Before, string After, string? Target = null)
{
    public ServiceChange Inverse() => this with { Before = After, After = Before };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionKind
{
    Start,
    Stop,
    SetStartMode,
    Undo,
    RestoreAll,
    /// <summary>Processus terminé (v0.4) : non annulable.</summary>
    TerminateProcess,
    /// <summary>Suggestions « Chez toi » appliquées en lot (v0.5) : annulable d'un bloc.</summary>
    ApplySuggestions,
    /// <summary>Neutralisation complète (v0.6, Avancé) : désactivation + tâches + triggers + récupération.</summary>
    Neutralize,
    /// <summary>Verrouillage ACL de la clé du service (v0.6, Expert).</summary>
    LockAcl,
    /// <summary>Déverrouillage ACL.</summary>
    UnlockAcl,
    /// <summary>Activation ou désactivation du journal DNS (v1.1, page Réseau).</summary>
    ConfigureDnsLog,
}

public sealed record ActionRequest(string Service, ActionKind Kind, ServiceStartMode? StartMode = null);

/// <summary>Raison pour laquelle une action est refusée.</summary>
public enum BlockReason
{
    None,
    /// <summary>Service de la liste rouge : blocage absolu (spec §5.3).</summary>
    RedList,
    /// <summary>Un service dépendant à arrêter est sur la liste rouge.</summary>
    RedDependent,
    /// <summary>Instance par utilisateur : le type de démarrage se règle sur le modèle.</summary>
    PerUserInstance,
    /// <summary>Type de démarrage non modifiable (pilote, inconnu).</summary>
    UnsupportedStartMode,
    NotFound,
    AlreadyUndone,
    /// <summary>Action impossible à annuler (processus terminé).</summary>
    NotUndoable,
    /// <summary>Sécurité de la clé de registre illisible : verrouillage impossible.</summary>
    AclUnreadable,
}

/// <summary>Aperçu d'une action : ce qui va changer, avant toute modification.</summary>
public sealed class ActionPlan
{
    public required ActionRequest Request { get; init; }
    public List<ServiceChange> Changes { get; init; } = [];
    public SafetyLevel Safety { get; init; }
    public BlockReason Blocked { get; init; }
    /// <summary>Nom du service en cause pour <see cref="BlockReason.RedDependent"/>.</summary>
    public string? BlockedDetail { get; init; }
    /// <summary>Services dépendants qui seront arrêtés en même temps.</summary>
    public List<string> DependentsToStop { get; init; } = [];
    /// <summary>Pour une annulation : entrée d'historique annulée.</summary>
    public Guid? UndoOf { get; init; }

    public bool IsBlocked => Blocked != BlockReason.None;
    public bool IsNoOp => !IsBlocked && Changes.Count == 0;
    /// <summary>Liste orange touchée → avertissement renforcé et confirmation explicite.</summary>
    public bool RequiresAcknowledgement => Safety == SafetyLevel.Orange;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceActionError
{
    Unknown,
    AccessDenied,
    NotFound,
    ServiceDisabled,
    DependentsRunning,
    DependencyFailed,
    CannotAcceptControl,
    Timeout,
    LogonFailure,
}

public sealed class ServiceActionException(string service, ServiceActionError code, int win32Error, string message)
    : Exception(message)
{
    public string Service { get; } = service;
    public ServiceActionError Code { get; } = code;
    public int Win32Error { get; } = win32Error;

    public static ServiceActionError Map(int win32Error) => win32Error switch
    {
        5 => ServiceActionError.AccessDenied,
        1060 => ServiceActionError.NotFound,
        1058 => ServiceActionError.ServiceDisabled,
        1051 => ServiceActionError.DependentsRunning,
        1068 or 1075 => ServiceActionError.DependencyFailed,
        1052 or 1061 => ServiceActionError.CannotAcceptControl,
        1053 => ServiceActionError.Timeout,
        1069 => ServiceActionError.LogonFailure,
        _ => ServiceActionError.Unknown,
    };
}

/// <summary>Résultat d'une exécution : entrée d'historique créée, erreur éventuelle, écarts constatés.</summary>
public sealed class ActionResult
{
    public HistoryEntry? Entry { get; init; }
    public ServiceActionError? Error { get; init; }
    public string? ErrorService { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Changements demandés mais dont l'effet n'est pas constaté à la relecture (Windows a annulé…).</summary>
    public List<ServiceChange> NotEffective { get; init; } = [];
    /// <summary>Pour une annulation : services modifiés entre-temps par autre chose que SerWin.</summary>
    public List<ServiceChange> Drifted { get; init; } = [];

    public bool Success => Error is null && NotEffective.Count == 0;
}
