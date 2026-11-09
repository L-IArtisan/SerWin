using System.Text.Json.Serialization;

namespace SerWin.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceStartMode
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceRunState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerKind
{
    DeviceInterfaceArrival,
    IpAddressAvailability,
    DomainJoin,
    FirewallPortEvent,
    GroupPolicy,
    NetworkEndpoint,
    CustomSystemStateChange,
    Custom,
    Aggregate,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriggerAction
{
    Start,
    Stop,
    Unknown,
}

/// <summary>Déclencheur de service (trigger) tel que lu dans la configuration SCM.</summary>
public sealed record ServiceTrigger
{
    public TriggerKind Kind { get; init; }
    public TriggerAction Action { get; init; }
    public Guid? Subtype { get; init; }
    /// <summary>Nom lisible du sous-type quand le GUID est connu (ex. « Première adresse IP disponible »).</summary>
    public string? SubtypeName { get; init; }
    public List<string> Data { get; init; } = [];

    // ── Données brutes (v0.6) : permettent de recréer le trigger à l'identique après une neutralisation ──
    public uint RawType { get; init; }
    public uint RawAction { get; init; }
    public List<TriggerDataRaw> RawData { get; init; } = [];
}

/// <summary>Élément de données d'un trigger, octet pour octet (SERVICE_TRIGGER_SPECIFIC_DATA_ITEM).</summary>
public sealed record TriggerDataRaw(uint DataType, string Base64)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Bytes => Convert.FromBase64String(Base64);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecoveryActionType
{
    None,
    Restart,
    Reboot,
    RunCommand,
}

public sealed record RecoveryAction(RecoveryActionType Type, uint DelayMs);

/// <summary>Actions de récupération (onglet « Récupération » de services.msc).</summary>
public sealed record RecoveryConfig
{
    public uint ResetPeriodSeconds { get; init; }
    public string? Command { get; init; }
    public string? RebootMessage { get; init; }
    public List<RecoveryAction> Actions { get; init; } = [];

    [JsonIgnore]
    public bool RestartsService => Actions.Any(a => a.Type == RecoveryActionType.Restart);
}

/// <summary>Instantané complet d'un service Windows.</summary>
public sealed record ServiceInfo
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public string? Description { get; init; }
    public ServiceRunState State { get; init; }
    public ServiceStartMode StartMode { get; init; } = ServiceStartMode.Unknown;
    public bool TriggerStart => Triggers.Count > 0;
    public uint ServiceType { get; init; }
    public uint ErrorControl { get; init; }
    public string? BinaryPath { get; init; }
    /// <summary>Pour les services hébergés par svchost : DLL réelle (Parameters\ServiceDll).</summary>
    public string? ServiceDll { get; init; }
    public string? Account { get; init; }
    public string? LoadOrderGroup { get; init; }
    public uint ProcessId { get; init; }
    public List<string> Dependencies { get; init; } = [];
    public List<string> DependentServices { get; init; } = [];
    public List<ServiceTrigger> Triggers { get; init; } = [];
    public RecoveryConfig? Recovery { get; init; }
    /// <summary>Éditeur (CompanyName du fichier binaire ou de la DLL).</summary>
    public string? Publisher { get; init; }
    /// <summary>Descripteur de sécurité SDDL de la clé de registre du service, si lisible.</summary>
    public string? RegistryKeySddl { get; init; }
    /// <summary>Descripteur de sécurité SDDL de l'objet service (sc sdshow), si lisible.</summary>
    public string? ServiceSddl { get; init; }
    /// <summary>Faux si la configuration n'a pas pu être lue (droits insuffisants).</summary>
    public bool ConfigReadable { get; init; } = true;

    [JsonIgnore]
    public string RegistryPath => $@"HKLM\SYSTEM\CurrentControlSet\Services\{Name}";

    /// <summary>Instance de service par utilisateur (ex. CDPUserSvc_1a2b3c) : type SERVICE_USERSERVICE_INSTANCE.</summary>
    [JsonIgnore]
    public bool IsPerUserInstance => (ServiceType & 0x80) != 0;

    /// <summary>Modèle de service par utilisateur (ex. CDPUserSvc) : type SERVICE_USER_SERVICE sans instance.</summary>
    [JsonIgnore]
    public bool IsPerUserTemplate => (ServiceType & 0x40) != 0 && (ServiceType & 0x80) == 0;

    /// <summary>Nom du modèle pour une instance par utilisateur (CDPUserSvc_1a2b3c → CDPUserSvc).</summary>
    [JsonIgnore]
    public string TemplateName
    {
        get
        {
            if (!IsPerUserInstance) return Name;
            var i = Name.LastIndexOf('_');
            return i > 0 ? Name[..i] : Name;
        }
    }

    [JsonIgnore]
    public bool IsMicrosoft => Publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

    [JsonIgnore]
    public bool IsThirdParty => !string.IsNullOrWhiteSpace(Publisher) && !IsMicrosoft;

    [JsonIgnore]
    public bool IsSharedProcess => (ServiceType & 0x20) != 0;

    /// <summary>Clé de registre verrouillée par SerWin (v0.6, mode Expert).</summary>
    [JsonIgnore]
    public bool IsAclLocked => Safety.AclLock.IsLocked(RegistryKeySddl);
}
