namespace SerWin.Core.Services;

/// <summary>Correspondances des types et GUID de triggers documentés par Microsoft (winsvc.h).</summary>
public static class TriggerCatalog
{
    public static TriggerKind MapKind(uint type) => type switch
    {
        1 => TriggerKind.DeviceInterfaceArrival,
        2 => TriggerKind.IpAddressAvailability,
        3 => TriggerKind.DomainJoin,
        4 => TriggerKind.FirewallPortEvent,
        5 => TriggerKind.GroupPolicy,
        6 => TriggerKind.NetworkEndpoint,
        7 => TriggerKind.CustomSystemStateChange,
        20 => TriggerKind.Custom,
        30 => TriggerKind.Aggregate,
        _ => TriggerKind.Unknown,
    };

    /// <summary>GUID connus → clé de traduction (résolue par l'UI).</summary>
    private static readonly Dictionary<Guid, string> Known = new()
    {
        [new Guid("4f27f2de-14e2-430b-a549-7cd48cbc8245")] = "FirstIpAddressArrival",
        [new Guid("cc4ba62a-162e-4648-847a-b6bdf993e335")] = "LastIpAddressRemoval",
        [new Guid("1ce20aba-9851-4421-9430-1ddeb766e809")] = "DomainJoin",
        [new Guid("ddaf516e-58c2-4866-9574-c3b615d42ea1")] = "DomainLeave",
        [new Guid("b7569e07-8421-4ee0-ad10-86915afdad09")] = "FirewallPortOpen",
        [new Guid("a144ed38-8e12-4de4-9d96-e64740b1a524")] = "FirewallPortClose",
        [new Guid("659fcae6-5bdb-4da9-b1ff-ca2a178d46e0")] = "MachinePolicyPresent",
        [new Guid("54fb46c8-f089-464c-b1fd-59d1b62c3b50")] = "UserPolicyPresent",
        [new Guid("bc90d167-9470-4139-a9ba-be0bbbf5b74d")] = "RpcInterfaceEvent",
        [new Guid("1f81d131-3fac-4537-9e0c-7e7b0c2f4b55")] = "NamedPipeEvent",
    };

    public static string? NameOf(Guid g) => Known.TryGetValue(g, out var n) ? n : null;

    /// <summary>Forme invariante et stable d'un trigger, utilisée dans les fiches : « GroupPolicy:MachinePolicyPresent ».</summary>
    public static string Describe(ServiceTrigger t)
    {
        var sub = t.SubtypeName ?? t.Subtype?.ToString();
        var s = sub is null ? t.Kind.ToString() : $"{t.Kind}:{sub}";
        return t.Action == TriggerAction.Stop ? s + " (stop)" : s;
    }

    /// <summary>
    /// Vrai si un trigger observé correspond à une cible « trigger » de la base
    /// (group-policy, rpc, named-pipe, device-arrival, network, domain, firewall, custom).
    /// </summary>
    public static bool MatchesKnowledgeTarget(ServiceTrigger t, string target) => target switch
    {
        "group-policy" => t.Kind == TriggerKind.GroupPolicy,
        "rpc" => t.Kind == TriggerKind.NetworkEndpoint && t.SubtypeName == "RpcInterfaceEvent",
        "named-pipe" => t.Kind == TriggerKind.NetworkEndpoint && t.SubtypeName == "NamedPipeEvent",
        "device-arrival" => t.Kind == TriggerKind.DeviceInterfaceArrival,
        "network" => t.Kind == TriggerKind.IpAddressAvailability,
        "domain" => t.Kind == TriggerKind.DomainJoin,
        "firewall" => t.Kind == TriggerKind.FirewallPortEvent,
        "custom" => t.Kind is TriggerKind.Custom or TriggerKind.CustomSystemStateChange,
        _ => false,
    };
}
