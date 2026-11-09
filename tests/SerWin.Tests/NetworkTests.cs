using System.Net;
using SerWin.Core.Actions;
using SerWin.Core.Network;
using SerWin.Knowledge;

namespace SerWin.Tests;

public class NetAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("192.168.1.10", AddressScope.LocalNetwork)]
    [InlineData("10.0.0.2", AddressScope.LocalNetwork)]
    [InlineData("172.20.1.1", AddressScope.LocalNetwork)]
    [InlineData("172.32.1.1", AddressScope.Internet)]
    [InlineData("169.254.3.4", AddressScope.LocalNetwork)]
    [InlineData("fe80::1", AddressScope.LocalNetwork)]
    [InlineData("fd12:3456::1", AddressScope.LocalNetwork)]
    [InlineData("20.42.65.92", AddressScope.Internet)]
    [InlineData("2603:1020:805:3::402", AddressScope.Internet)]
    [InlineData("::ffff:192.168.0.1", AddressScope.LocalNetwork)]
    public void Classifies_addresses(string address, AddressScope expected) =>
        Assert.Equal(expected, NetAddress.Classify(IPAddress.Parse(address)));

    [Fact]
    public void Ports_are_read_in_network_byte_order() => Assert.Equal(443, TcpConnections.Port(0xBB01));
}

public class DnsLogParsingTests
{
    [Fact]
    public void Parses_addresses_and_cname_aliases()
    {
        var (addresses, aliases) = DnsLog.ParseResults(
            "type:  5 v10.events.data.microsoft.com.trafficmanager.net;type:  5 onedscolprdwus07.westus.cloudapp.azure.com;::ffff:20.189.173.1;2603:1030:7::1;");
        Assert.Equal([IPAddress.Parse("20.189.173.1"), IPAddress.Parse("2603:1030:7::1")], addresses);
        Assert.Equal(["v10.events.data.microsoft.com.trafficmanager.net", "onedscolprdwus07.westus.cloudapp.azure.com"], aliases);
    }

    [Fact]
    public void Empty_results_are_handled() => Assert.Empty(DnsLog.ParseResults(null).Addresses);

    [Fact]
    public void Event_log_setting_round_trips()
    {
        var s = new EventLogSetting(true, 20971520);
        Assert.Equal(s, EventLogSetting.Parse(s.Encode()));
        Assert.Null(EventLogSetting.Parse("n'importe quoi"));
    }
}

public class DestinationCatalogTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadEmbedded();

    [Theory]
    [InlineData("v10.events.data.microsoft.com", "windows-telemetry")]
    [InlineData("events.data.microsoft.com", "windows-telemetry")]       // « *.x » couvre aussi x
    [InlineData("ctldl.windowsupdate.com", "microsoft-pki")]              // plus précis que *.windowsupdate.com
    [InlineData("download.windowsupdate.com", "windows-update")]
    [InlineData("c.bing.com", "microsoft-tracking")]                      // plus précis que *.bing.com
    [InlineData("www.bing.com", "bing")]
    [InlineData("config.edge.skype.com", "microsoft-experimentation")]    // plus précis que *.skype.com
    [InlineData("www.msftconnecttest.com", "connectivity-check")]
    [InlineData("WWW.MSFTCONNECTTEST.COM.", "connectivity-check")]        // casse et point final
    [InlineData("something.unknown.microsoft.com", "microsoft-other")]
    [InlineData("ocsp.digicert.com", "certificate-authorities")]
    public void Most_precise_pattern_wins(string host, string expected) =>
        Assert.Equal(expected, Kb.FindDestination(host)?.Name);

    [Fact]
    public void Unknown_host_has_no_entry() => Assert.Null(Kb.FindDestination("example.org"));

    [Fact]
    public void Suffix_does_not_match_inside_a_label() => Assert.Null(Kb.FindDestination("notbing.com"));

    [Fact]
    public void Falls_back_to_cname_aliases() =>
        Assert.Equal("microsoft-cdn", Kb.FindDestination("cdn.example.org", ["example.azureedge.net"])?.Name);

    [Fact]
    public void Base_contains_enough_general_destinations()
    {
        Assert.True(Kb.Destinations.Count >= 50, $"{Kb.Destinations.Count} fiches destinations");
        Assert.Empty(KnowledgeValidator.Validate(Kb).Where(i => i.Severity == "error"));
    }

    [Fact]
    public void File_name_matches_destination_name()
    {
        var dir = Path.Combine(KnowledgeTests.RepoRoot(), "knowledge", "destinations");
        var files = Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.Equal(files, Kb.Destinations.Select(d => d.Name).Order().ToList());
    }

    [Fact]
    public void Security_essentials_are_never_marked_unwanted()
    {
        foreach (var name in new[] { "windows-update", "microsoft-pki", "certificate-authorities", "microsoft-defender", "smartscreen" })
            Assert.Equal(DestinationNeed.Essential, Kb.Destinations.Single(d => d.Name == name).Need);
    }
}

public class NetworkObserverTests
{
    private sealed class FakeResolver : ISenderResolver
    {
        public SenderIdentity Resolve(int pid, string? ownerModule, DateTime? at) => pid switch
        {
            100 => new SenderIdentity("svc:diagtrack", "svchost.exe", "DiagTrack", [], pid),
            200 => new SenderIdentity("exe:msedge.exe", "msedge.exe", null, [], pid),
            _ => SenderIdentity.Unknown(pid),
        };
    }

    private static readonly DateTime T0 = new(2026, 11, 10, 10, 0, 0);
    private static readonly IPAddress Ip = IPAddress.Parse("20.42.65.92");

    private static DnsQuery Query(int pid, string name, params IPAddress[] ips) => new(T0, pid, name, ips, [], 0);
    private static TcpConnection Conn(int pid, IPAddress ip, int localPort = 50000) => new(pid, null, ip, 443, localPort, T0);

    [Fact]
    public void Connection_is_named_from_the_dns_log()
    {
        var o = new NetworkObserver(new FakeResolver());
        o.AddDns([Query(100, "v10.events.data.microsoft.com", Ip)]);
        o.AddConnections([Conn(100, Ip)], T0.AddSeconds(5));

        var flow = Assert.Single(o.Flows);
        Assert.Equal("v10.events.data.microsoft.com", flow.Destination);
        Assert.Equal("DiagTrack", flow.Sender.Service);
        Assert.Equal(1, flow.Queries);
        Assert.Equal(1, flow.Connections);
        Assert.Equal(1, flow.ActiveConnections);
    }

    [Fact]
    public void Ip_only_flow_is_renamed_once_the_name_is_known()
    {
        var o = new NetworkObserver(new FakeResolver());
        o.AddConnections([Conn(200, Ip)], T0);
        Assert.Equal(Ip.ToString(), Assert.Single(o.Flows).Destination);

        o.AddDns([Query(200, "www.bing.com", Ip)]);
        var flow = Assert.Single(o.Flows);
        Assert.Equal("www.bing.com", flow.Destination);
        Assert.True(flow.HasHostName);
        Assert.Equal(1, flow.Connections);
    }

    [Fact]
    public void Shared_address_prefers_the_name_asked_by_the_same_program()
    {
        var o = new NetworkObserver(new FakeResolver());
        o.AddDns([Query(100, "v10.events.data.microsoft.com", Ip), Query(200, "www.bing.com", Ip) with { Time = T0.AddSeconds(1) }]);
        Assert.Equal("v10.events.data.microsoft.com", o.NameOf(Ip, "svc:diagtrack"));
        Assert.Equal("www.bing.com", o.NameOf(Ip, "exe:msedge.exe"));
        Assert.Equal("www.bing.com", o.NameOf(Ip)); // le plus récent
    }

    [Fact]
    public void Address_resolved_by_another_program_is_still_named()
    {
        // Régression v1.1 : le navigateur se connecte à une adresse que seul DiagTrack a résolue
        // (« Sequence contains no elements »).
        var o = new NetworkObserver(new FakeResolver());
        o.AddDns([Query(100, "v10.events.data.microsoft.com", Ip)]);
        Assert.Equal("v10.events.data.microsoft.com", o.NameOf(Ip, "exe:msedge.exe"));

        o.AddConnections([Conn(200, Ip)], T0.AddSeconds(5));
        Assert.Contains(o.Flows, f => f.Sender.Key == "exe:msedge.exe" && f.Destination == "v10.events.data.microsoft.com");
    }

    [Fact]
    public void Address_named_only_by_the_cache_is_named_for_everyone()
    {
        var o = new NetworkObserver(new FakeResolver());
        o.AddCache([("www.msftconnecttest.com", Ip)], T0);
        o.AddConnections([Conn(100, Ip), Conn(300, Ip, 50001)], T0);
        Assert.All(o.Flows, f => Assert.Equal("www.msftconnecttest.com", f.Destination));
        Assert.Null(o.NameOf(IPAddress.Parse("1.2.3.4"), "svc:diagtrack"));
    }

    [Fact]
    public void Same_connection_seen_twice_counts_once_and_loopback_is_ignored()
    {
        var o = new NetworkObserver(new FakeResolver());
        var c = Conn(200, Ip);
        o.AddConnections([c, Conn(200, IPAddress.Loopback)], T0);
        o.AddConnections([c], T0.AddSeconds(3));
        var flow = Assert.Single(o.Flows);
        Assert.Equal(1, flow.Connections);
        Assert.Equal(1, flow.ActiveConnections);

        o.AddConnections([], T0.AddSeconds(6));
        Assert.Equal(0, flow.ActiveConnections);
    }

    [Fact]
    public void Dns_events_are_not_counted_twice_when_read_again()
    {
        var o = new NetworkObserver(new FakeResolver());
        var q = Query(100, "settings-win.data.microsoft.com", Ip);
        o.AddDns([q]);
        o.AddDns([q]);
        Assert.Equal(1, Assert.Single(o.Flows).Queries);
    }
}

public sealed class DnsLogActionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serwin-dnslog-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _scm = new();
    private readonly ActionEngine _engine;

    public DnsLogActionTests()
    {
        Directory.CreateDirectory(_dir);
        _scm.EventLogs[DnsLog.LogName] = new EventLogSetting(false, 1052672).Encode();
        _engine = new ActionEngine(_scm, new HistoryStore(_dir), new BackupStore(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Enabling_sets_size_and_can_be_undone_exactly()
    {
        var plan = _engine.PlanDnsLog(enable: true);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.EventLog, change.Kind);
        Assert.Equal(DnsLog.LogName, change.Target);

        var result = _engine.Execute(plan);
        Assert.True(result.Success);
        Assert.Empty(result.NotEffective);
        Assert.Equal(new EventLogSetting(true, DnsLog.RecommendedMaxSize), EventLogSetting.Parse(_scm.EventLogs[DnsLog.LogName]));

        var undo = _engine.Execute(_engine.PlanUndo(result.Entry!.Id));
        Assert.True(undo.Success);
        Assert.Equal(new EventLogSetting(false, 1052672), EventLogSetting.Parse(_scm.EventLogs[DnsLog.LogName]));
    }

    [Fact]
    public void Windows_size_rounding_is_not_reported_as_a_failure()
    {
        _scm.RoundLogSize = true;
        var result = _engine.Execute(_engine.PlanDnsLog(enable: true));
        var undo = _engine.Execute(_engine.PlanUndo(result.Entry!.Id));
        Assert.Empty(undo.NotEffective);
    }

    [Fact]
    public void Already_enabled_is_a_no_op_and_unreadable_log_is_blocked()
    {
        _engine.Execute(_engine.PlanDnsLog(enable: true));
        Assert.True(_engine.PlanDnsLog(enable: true).IsNoOp);

        _scm.EventLogs.Clear();
        Assert.Equal(BlockReason.NotFound, _engine.PlanDnsLog(enable: true).Blocked);
    }

    [Fact]
    public void Disabling_keeps_the_size()
    {
        _engine.Execute(_engine.PlanDnsLog(enable: true));
        _engine.Execute(_engine.PlanDnsLog(enable: false));
        Assert.Equal(new EventLogSetting(false, DnsLog.RecommendedMaxSize), EventLogSetting.Parse(_scm.EventLogs[DnsLog.LogName]));
    }
}

public class NetworkEventTests
{
    private sealed class Resolver : ISenderResolver
    {
        public SenderIdentity Resolve(int pid, string? ownerModule, DateTime? at) =>
            new($"exe:p{pid}", $"p{pid}.exe", null, [], pid);
    }

    private static readonly DateTime T0 = new(2026, 11, 10, 10, 30, 0);
    private static readonly IPAddress Ip = IPAddress.Parse("20.42.65.92");

    [Fact]
    public void Each_query_and_new_connection_is_recorded_once()
    {
        var o = new NetworkObserver(new Resolver());
        var q = new DnsQuery(T0, 1, "a.example.com", [Ip], [], 0);
        o.AddDns([q]);
        o.AddDns([q]);
        var c = new TcpConnection(2, null, IPAddress.Parse("1.1.1.1"), 443, 50000, T0.AddMinutes(1));
        o.AddConnections([c], T0.AddMinutes(2));
        o.AddConnections([c], T0.AddMinutes(3));

        Assert.Equal(2, o.Events.Count);
        Assert.Equal(NetworkEventKind.Dns, o.Events[0].Kind);
        Assert.Equal(T0.AddMinutes(1), o.Events[1].Time); // heure d'ouverture de la connexion
        Assert.Equal(2, o.LastSequence);
    }

    [Fact]
    public void Event_destination_follows_the_rename_from_ip_to_host()
    {
        var o = new NetworkObserver(new Resolver());
        o.AddConnections([new TcpConnection(2, null, Ip, 443, 50000, T0)], T0);
        Assert.Equal(Ip.ToString(), Assert.Single(o.Events).Destination);
        o.AddDns([new DnsQuery(T0.AddSeconds(1), 2, "b.example.com", [Ip], [], 0)]);
        Assert.All(o.Events, e => Assert.Equal("b.example.com", e.Destination));
    }

    [Fact]
    public void Timeline_groups_events_in_aligned_buckets()
    {
        var o = new NetworkObserver(new Resolver());
        o.AddDns([
            new DnsQuery(T0, 1, "a.example.com", [], [], 0),
            new DnsQuery(T0.AddMinutes(5), 1, "b.example.com", [], [], 0),
            new DnsQuery(T0.AddHours(-3), 1, "c.example.com", [], [], 0),
            new DnsQuery(T0.AddDays(-2), 1, "old.example.com", [], [], 0), // hors période
        ]);
        var buckets = NetworkTimeline.Build(o.Events, e => e.Destination == "a.example.com" ? "A" : "other", T0, TimeSpan.FromHours(1), 24);

        Assert.Equal(24, buckets.Count);
        Assert.Equal(new DateTime(2026, 11, 10, 10, 0, 0), buckets[^1].Start);   // tranche en cours, alignée sur l'heure
        Assert.Equal(new DateTime(2026, 11, 9, 11, 0, 0), buckets[0].Start);
        Assert.Equal(1, buckets[^1].Counts["A"]);
        Assert.Equal(1, buckets[^1].Counts["other"]);
        Assert.Equal(1, buckets[^4].Total);
        Assert.Equal(3, buckets.Sum(b => b.Total));
    }

    [Fact]
    public void Four_hour_buckets_start_at_midnight_multiples()
    {
        var buckets = NetworkTimeline.Build<string>([], _ => "", new DateTime(2026, 11, 10, 10, 30, 0), TimeSpan.FromHours(4), 42);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), buckets[^1].Start);
        Assert.Equal(new DateTime(2026, 11, 3, 12, 0, 0), buckets[0].Start);
    }
}

public class SenderResolverTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 0, 29, 41);

    [Fact]
    public void Ended_process_keeps_its_name()
    {
        var r = new SenderResolver();
        r.Seed(2488, "dotnet.exe", T0.AddSeconds(-2), T0.AddSeconds(1));
        r.Forget(2488); // le processus s'est terminé avant la lecture du journal
        var s = r.Resolve(2488, null, T0);
        Assert.Equal("dotnet.exe", s.ProcessName);
        Assert.Equal("exe:dotnet.exe", s.Key);
    }

    [Fact]
    public void Reused_pid_is_not_blamed_for_an_older_event()
    {
        var r = new SenderResolver();
        r.Seed(2488, "notepad.exe", T0.AddMinutes(5), T0.AddMinutes(6)); // démarré après l'événement
        Assert.False(r.Resolve(2488, null, T0).IsKnown);
    }

    [Fact]
    public void Single_service_process_is_identified_as_the_service()
    {
        var r = new SenderResolver();
        r.Seed(1200, "svchost.exe", null, T0, ["DiagTrack"]);
        Assert.Equal("DiagTrack", r.Resolve(1200, null, T0).Service);
    }
}
