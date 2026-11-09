using System.Diagnostics;
using System.Net;
using SerWin.Core.Services;

namespace SerWin.Core.Network;

/// <summary>Programme ou service à l'origine d'un échange réseau.</summary>
/// <param name="Key">Clé de regroupement : un service, ou un exécutable (tous ses processus confondus).</param>
/// <param name="ProcessName">Exécutable (ex. « svchost.exe », « msedge.exe ») ; null si le processus est terminé et inconnu.</param>
/// <param name="Service">Service Windows exact, quand il est identifiable.</param>
/// <param name="HostedServices">Services hébergés par le processus quand le service exact n'est pas identifiable.</param>
public sealed record SenderIdentity(string Key, string? ProcessName, string? Service, IReadOnlyList<string> HostedServices, int ProcessId)
{
    public bool IsKnown => ProcessName is not null || Service is not null;

    public static SenderIdentity Unknown(int pid) => new($"pid:{pid}", null, null, [], pid);
}

/// <summary>Échanges d'un programme avec une destination (nom d'hôte, ou adresse IP si aucun nom n'est connu).</summary>
public sealed class NetworkFlow
{
    public required SenderIdentity Sender { get; init; }
    public required string Destination { get; set; }
    public bool HasHostName { get; set; }
    public AddressScope Scope { get; set; } = AddressScope.Internet;
    public HashSet<IPAddress> Addresses { get; } = [];
    public SortedSet<int> Ports { get; } = [];
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime FirstSeen { get; set; } = DateTime.MaxValue;
    public DateTime LastSeen { get; set; } = DateTime.MinValue;

    /// <summary>Requêtes DNS relevées dans le journal.</summary>
    public int Queries { get; set; }

    /// <summary>Connexions TCP distinctes observées depuis l'ouverture de la page.</summary>
    public int Connections { get; set; }

    /// <summary>Connexions ouvertes lors du dernier relevé.</summary>
    public int ActiveConnections { get; set; }

    internal void Seen(DateTime t)
    {
        if (t < FirstSeen) FirstSeen = t;
        if (t > LastSeen) LastSeen = t;
    }
}

/// <summary>Identifie le programme derrière un numéro de processus.</summary>
public interface ISenderResolver
{
    /// <param name="ownerModule">Module fourni par la table TCP (nom du service pour un svchost).</param>
    /// <param name="at">Heure de l'événement : un processus démarré après n'en est pas l'auteur (numéro réutilisé).</param>
    SenderIdentity Resolve(int pid, string? ownerModule, DateTime? at);
}

/// <summary>
/// Agrège ce que voit Windows : requêtes DNS (qui a demandé quel nom) et connexions TCP (qui parle à quelle adresse).
/// Aucune donnée ne quitte la machine et SerWin n'envoie rien : il ne fait que lire.
/// </summary>
public sealed class NetworkObserver(ISenderResolver resolver)
{
    private readonly Dictionary<string, NetworkFlow> _flows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, List<(string Name, string SenderKey, DateTime Time)>> _names = [];
    private readonly HashSet<(long, int, string)> _dnsSeen = [];
    private readonly HashSet<string> _connectionsSeen = [];

    public IReadOnlyCollection<NetworkFlow> Flows => _flows.Values;

    /// <summary>Nombre de requêtes DNS déjà intégrées (pour l'affichage).</summary>
    public int DnsQueryCount => _dnsSeen.Count;

    public void AddDns(IEnumerable<DnsQuery> queries)
    {
        foreach (var q in queries)
        {
            if (!_dnsSeen.Add((q.Time.Ticks, q.ProcessId, q.Name))) continue;
            var sender = resolver.Resolve(q.ProcessId, null, q.Time);
            foreach (var ip in q.Addresses) Remember(ip, q.Name, sender.Key, q.Time);

            var flow = GetFlow(sender, q.Name, hostName: true);
            flow.Queries++;
            flow.Seen(q.Time);
            foreach (var ip in q.Addresses) flow.Addresses.Add(ip);
            foreach (var alias in q.Aliases) flow.Aliases.Add(alias);
            if (q.Addresses.Count > 0) flow.Scope = q.Addresses.Select(NetAddress.Classify).Max();
        }
        Rekey();
    }

    /// <summary>Noms connus par le cache DNS : servent seulement à nommer des adresses.</summary>
    public void AddCache(IEnumerable<(string Name, IPAddress Address)> entries, DateTime now)
    {
        foreach (var (name, ip) in entries) Remember(ip, name, "", now);
        Rekey();
    }

    public void AddConnections(IEnumerable<TcpConnection> connections, DateTime now)
    {
        foreach (var f in _flows.Values) f.ActiveConnections = 0;
        foreach (var c in connections)
        {
            if (c.Scope == AddressScope.Loopback) continue;
            var sender = resolver.Resolve(c.ProcessId, c.OwnerModule, null);
            var name = NameOf(c.RemoteAddress, sender.Key);
            var flow = GetFlow(sender, name ?? c.RemoteAddress.ToString(), hostName: name is not null);
            flow.Scope = c.Scope;
            flow.Addresses.Add(c.RemoteAddress);
            flow.Ports.Add(c.RemotePort);
            flow.ActiveConnections++;
            if (_connectionsSeen.Add(c.Key)) flow.Connections++;
            flow.Seen(c.Since is { } s && s <= now ? s : now);
            flow.Seen(now);
        }
    }

    /// <summary>
    /// Nom d'hôte d'une adresse. Une même adresse peut servir plusieurs noms (CDN) : on préfère le nom demandé par
    /// le même programme, puis le plus récent.
    /// </summary>
    public string? NameOf(IPAddress address, string? senderKey = null)
    {
        if (!_names.TryGetValue(NetAddress.Normalize(address), out var names) || names.Count == 0) return null;
        if (senderKey is not null)
        {
            var own = names.Where(n => n.SenderKey == senderKey).MaxBy(n => n.Time);
            if (own.Name is not null) return own.Name;
        }
        return names.MaxBy(n => n.Time).Name;
    }

    private void Remember(IPAddress ip, string name, string senderKey, DateTime time)
    {
        ip = NetAddress.Normalize(ip);
        if (!_names.TryGetValue(ip, out var list)) _names[ip] = list = [];
        var i = list.FindIndex(n => n.Name == name && n.SenderKey == senderKey);
        if (i >= 0)
        {
            if (list[i].Time < time) list[i] = (name, senderKey, time);
        }
        else
        {
            list.Add((name, senderKey, time));
        }
    }

    private NetworkFlow GetFlow(SenderIdentity sender, string destination, bool hostName)
    {
        var key = $"{sender.Key}→{destination}";
        if (!_flows.TryGetValue(key, out var flow))
        {
            flow = new NetworkFlow { Sender = sender, Destination = destination, HasHostName = hostName };
            _flows[key] = flow;
        }
        return flow;
    }

    /// <summary>Fusionne les échanges vus par adresse IP dans ceux du nom d'hôte correspondant, dès qu'il est connu.</summary>
    private void Rekey()
    {
        foreach (var (key, flow) in _flows.Where(kv => !kv.Value.HasHostName).ToList())
        {
            var name = flow.Addresses.Select(a => NameOf(a, flow.Sender.Key)).FirstOrDefault(n => n is not null);
            if (name is null) continue;
            _flows.Remove(key);
            var target = GetFlow(flow.Sender, name, hostName: true);
            foreach (var a in flow.Addresses) target.Addresses.Add(a);
            foreach (var p in flow.Ports) target.Ports.Add(p);
            target.Connections += flow.Connections;
            target.ActiveConnections += flow.ActiveConnections;
            target.Queries += flow.Queries;
            target.Scope = flow.Scope;
            if (flow.FirstSeen != DateTime.MaxValue) target.Seen(flow.FirstSeen);
            if (flow.LastSeen != DateTime.MinValue) target.Seen(flow.LastSeen);
        }
    }
}

/// <summary>
/// Résolution réelle : processus en cours (nom, heure de démarrage) et services hébergés par chaque processus.
/// À rafraîchir avant chaque relevé.
/// </summary>
public sealed class SenderResolver : ISenderResolver
{
    private Dictionary<int, (string Name, DateTime? Start)> _processes = [];
    private Dictionary<int, List<string>> _servicesByPid = [];

    public void Refresh(IEnumerable<ServiceInfo> services)
    {
        var processes = new Dictionary<int, (string, DateTime?)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                DateTime? start = null;
                try
                {
                    start = p.StartTime;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Processus d'un autre compte : heure inconnue.
                }
                processes[p.Id] = (p.ProcessName + ".exe", start);
            }
        }
        _processes = processes;
        _servicesByPid = services.Where(s => s.ProcessId != 0)
            .GroupBy(s => (int)s.ProcessId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public SenderIdentity Resolve(int pid, string? ownerModule, DateTime? at)
    {
        if (pid is 0 or 4) return new SenderIdentity("exe:system", "System", null, [], pid);

        var alive = _processes.TryGetValue(pid, out var proc)
                    && !(at is { } t && proc.Start is { } start && start > t.AddSeconds(1));
        var hosted = alive ? _servicesByPid.GetValueOrDefault(pid) ?? [] : [];

        string? service = null;
        if (ownerModule is not null && hosted.Contains(ownerModule, StringComparer.OrdinalIgnoreCase))
            service = hosted.First(h => h.Equals(ownerModule, StringComparison.OrdinalIgnoreCase));
        else if (hosted.Count == 1)
            service = hosted[0];

        var exe = alive ? proc.Name
            : ownerModule is not null && ownerModule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ownerModule
            : null;
        if (service is not null) return new SenderIdentity($"svc:{service.ToLowerInvariant()}", exe, service, [], pid);
        if (exe is null) return SenderIdentity.Unknown(pid);

        // svchost partagé sans module : on regroupe par processus (les services hébergés sont listés).
        var key = hosted.Count > 1 ? $"pid:{pid}:{exe.ToLowerInvariant()}" : $"exe:{exe.ToLowerInvariant()}";
        return new SenderIdentity(key, exe, null, hosted.Count > 1 ? hosted : [], pid);
    }
}
