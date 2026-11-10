using System.Text.RegularExpressions;
using SerWin.Core.Services;
using SerWin.Knowledge;

namespace SerWin.Tests;

/// <summary>Vérifie la table de traductions (src/SerWin.App/Resources/Strings.tsv) sans charger WPF.</summary>
public class LocalizationTests
{
    private static readonly Dictionary<string, (string Fr, string En)> Strings = LoadTsv();

    private static Dictionary<string, (string, string)> LoadTsv()
    {
        var path = Path.Combine(KnowledgeTests.RepoRoot(), "src", "SerWin.App", "Resources", "Strings.tsv");
        var dict = new Dictionary<string, (string, string)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var p = line.Split('\t');
            Assert.True(p.Length == 3, $"Ligne mal formée : {line}");
            Assert.True(dict.TryAdd(p[0], (p[1], p[2])), $"Clé en double : {p[0]}");
        }
        return dict;
    }

    [Fact]
    public void Every_string_is_translated_in_both_languages()
    {
        foreach (var (key, (fr, en)) in Strings)
        {
            Assert.False(string.IsNullOrWhiteSpace(fr), $"{key} : français vide");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} : anglais vide");
        }
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        static string[] Holes(string s) => Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        foreach (var (key, (fr, en)) in Strings)
            Assert.True(Holes(fr).SequenceEqual(Holes(en)), $"{key} : paramètres différents entre fr et en");
    }

    public static TheoryData<string> EnumKeys()
    {
        var keys = new TheoryData<string>();
        foreach (var v in Enum.GetNames<ServiceRunState>()) keys.Add($"State_{v}");
        foreach (var v in Enum.GetNames<ServiceStartMode>()) keys.Add($"StartMode_{v}");
        foreach (var v in Enum.GetNames<TriggerKind>()) keys.Add($"TriggerKind_{v}");
        foreach (var v in Enum.GetNames<TriggerAction>()) keys.Add($"TriggerAction_{v}");
        foreach (var v in Enum.GetNames<RecoveryActionType>()) keys.Add($"RecoveryAction_{v}");
        foreach (var v in Enum.GetNames<VerificationStatus>()) keys.Add($"Verification_{v}");
        foreach (var c in KnowledgeCategories.All) keys.Add($"Category_{c.Replace('-', '_')}");
        foreach (var r in new[] { "service", "task", "trigger", "policy", "software", "update" }) keys.Add($"Reviver_{r}");
        foreach (var v in Enum.GetNames<SerWin.Core.Actions.BlockReason>()) keys.Add($"Blocked_{v}");
        foreach (var v in Enum.GetNames<SerWin.Core.Actions.ServiceActionError>()) keys.Add($"ActError_{v}");
        foreach (var v in Enum.GetNames<SerWin.Core.Platform.RestorePointOutcome>()) keys.Add($"RestorePoint_{v}");
        foreach (var v in new[] { "Start", "Stop", "SetStartMode" }) keys.Add($"Act_Confirm_{v}");
        foreach (var v in Enum.GetNames<SerWin.Core.Revivers.ReviverStrength>()) keys.Add($"RevStrength_{v}");
        foreach (var v in Enum.GetNames<SerWin.Core.Tasks.TaskLinkReason>()) keys.Add($"TaskReason_{v}");
        foreach (var v in Enum.GetNames<SerWin.Core.Processes.TerminateBlock>()) keys.Add($"TermBlocked_{v}");
        foreach (var v in Enum.GetNames<SerWin.Core.Context.Usefulness>()) keys.Add($"Verdict_{v}");
        foreach (var k in SerWin.Core.Context.MachineContext.Keys.All) keys.Add($"FactName_{k}");
        foreach (var v in Enum.GetNames<SerWin.Core.Processes.SignatureStatus>()) keys.Add($"Sig_{v}");
        foreach (var v in new[] { "Tree", "ByCpu", "ByMemory", "Warnings" }) keys.Add($"ProcView_{v}");
        foreach (var n in SerWin.Core.Processes.ProcessPathResolver.KernelPseudoProcesses)
            keys.Add($"Proc_Pseudo_{n.Replace(' ', '_').Trim('[', ']')}");
        // Chaque mécanisme « update » / « software » cité par une fiche doit avoir un libellé.
        foreach (var t in KnowledgeBase.LoadEmbedded().Services.SelectMany(e => e.Revivers)
                     .Where(r => r.Type is "update" or "software").Select(r => r.Target).Distinct())
            keys.Add($"Known_{t.Replace('-', '_')}");
        foreach (var t in KnowledgeBase.LoadEmbedded().Services.Concat(KnowledgeBase.LoadEmbedded().Processes).SelectMany(e => e.Revivers)
                     .Where(r => r.Type == "trigger").Select(r => r.Target).Distinct())
            keys.Add($"TriggerTarget_{t.Replace('-', '_')}");
        // v1.1 — Réseau
        foreach (var c in DestinationCategories.All) keys.Add($"DestCat_{c.Replace('-', '_')}");
        foreach (var v in Enum.GetNames<DestinationNeed>()) keys.Add($"Need_{v}");
        keys.Add("Need_Unknown");
        foreach (var v in new[] { "All", "Privacy", "Undocumented", "Active", "LocalNetwork" }) keys.Add($"NetFilter_{v}");
        foreach (var v in new[] { "Day", "Week" }) keys.Add($"TimelineRange_{v}");
        foreach (var v in Enum.GetNames<ServiceRole>()) { keys.Add($"Role_{v}"); keys.Add($"Deduced_Advice_{v}"); }
        foreach (var v in Enum.GetNames<SerWin.Core.Startup.StartupSource>()) keys.Add($"StartupSource_{v}");
        foreach (var v in new[] { "Program", "Task", "Service" }) keys.Add($"StartupKind_{v}");
        foreach (var v in new[] { "All", "Suggested", "Programs", "Tasks", "Services", "Disabled" }) keys.Add($"StartupFilter_{v}");
        foreach (var n in new[] { "FirstIpAddressArrival", "LastIpAddressRemoval", "DomainJoin", "DomainLeave", "FirewallPortOpen",
                     "FirewallPortClose", "MachinePolicyPresent", "UserPolicyPresent", "RpcInterfaceEvent", "NamedPipeEvent" })
            keys.Add($"TriggerSub_{n}");
        return keys;
    }

    [Theory]
    [MemberData(nameof(EnumKeys))]
    public void Every_enum_value_has_a_label(string key) =>
        Assert.True(Strings.ContainsKey(key), $"Clé manquante dans Strings.tsv : {key}");

    [Fact]
    public void Every_literal_key_used_in_the_app_exists()
    {
        var appDir = Path.Combine(KnowledgeTests.RepoRoot(), "src", "SerWin.App");
        var patterns = new[]
        {
            new Regex(@"\{loc:T (\w+)\}"),              // XAML
            new Regex(@"\bL\[""(\w+)""\]"),               // L["Clé"]
            new Regex(@"\bL\.Format\(""(\w+)"""),         // L.Format("Clé", …)
            new Regex(@"new\(\w+(?:\.\w+)?, ""(\w+)""\)"), // Option(valeur, "Clé")
        };
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                    if (!Strings.ContainsKey(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)} : {m.Groups[1].Value}");
        }
        Assert.True(missing.Count == 0, "Clés absentes de Strings.tsv :\n" + string.Join("\n", missing.Distinct()));
    }

    [Fact]
    public void Generated_resx_files_are_up_to_date()
    {
        var dir = Path.Combine(KnowledgeTests.RepoRoot(), "src", "SerWin.App", "Resources");
        var fr = File.ReadAllText(Path.Combine(dir, "Strings.resx"));
        var en = File.ReadAllText(Path.Combine(dir, "Strings.en.resx"));
        foreach (var key in Strings.Keys)
        {
            Assert.Contains($"name=\"{key}\"", fr);
            Assert.Contains($"name=\"{key}\"", en);
        }
    }
}
