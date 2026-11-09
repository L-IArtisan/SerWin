using SerWin.Core.Safety;
using SerWin.Knowledge;

namespace SerWin.Tests;

public class KnowledgeTests
{
    private static readonly KnowledgeBase Kb = KnowledgeBase.LoadEmbedded();

    [Fact]
    public void Embedded_base_loads_without_errors()
    {
        Assert.Empty(Kb.LoadErrors);
        Assert.True(Kb.ServiceCount >= 150, $"v1.0 exige 150 fiches, trouvé {Kb.ServiceCount}");
        Assert.Matches(@"^\d{4}\.\d{2}$", Kb.Metadata.Version);
    }

    [Fact]
    public void Every_red_and_orange_service_has_a_matching_entry()
    {
        // v1.0 : un service bloqué ou à risque doit toujours être expliqué, jamais seulement refusé.
        foreach (var name in SafetyLists.RedServices)
        {
            var e = Kb.FindService(name);
            Assert.True(e is not null, $"{name} (liste rouge) n'a pas de fiche");
            Assert.True(e!.Risk == RiskLevel.Critical, $"{name} est sur la liste rouge mais sa fiche n'est pas « critique »");
        }
        foreach (var name in SafetyLists.OrangeServices)
        {
            var e = Kb.FindService(name);
            Assert.True(e is not null, $"{name} (liste orange) n'a pas de fiche");
            Assert.True(e!.Risk != RiskLevel.Low, $"{name} est sur la liste orange mais sa fiche est « faible »");
        }
    }

    [Fact]
    public void All_entries_are_valid_and_fully_translated()
    {
        var issues = KnowledgeValidator.Validate(Kb);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues));
    }

    [Fact]
    public void File_name_matches_service_name()
    {
        var dir = Path.Combine(RepoRoot(), "knowledge", "services");
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var entry = Kb.FindService(Path.GetFileNameWithoutExtension(file));
            Assert.NotNull(entry);
            Assert.Equal(Path.GetFileNameWithoutExtension(file), entry!.Name);
        }
    }

    [Fact]
    public void Critical_entries_are_on_the_hardcoded_red_list()
    {
        // La liste rouge ne doit jamais dépendre de la base : toute fiche « critique » doit y figurer.
        foreach (var e in Kb.Services.Where(e => e.Risk == RiskLevel.Critical))
            Assert.True(SafetyLists.RedServices.Contains(e.Name), $"{e.Name} est critique mais absent de la liste rouge");
    }

    [Fact]
    public void Red_and_orange_lists_do_not_overlap()
    {
        Assert.Empty(SafetyLists.RedServices.Intersect(SafetyLists.OrangeServices, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aliases_resolve_to_the_same_entry()
    {
        var e = Kb.FindService("TabletInputService");
        Assert.NotNull(e);
        Assert.Equal("TextInputManagementService", e!.Name);
        Assert.Same(e, Kb.FindService("textinputmanagementservice"));
    }

    [Fact]
    public void Localized_text_falls_back_to_french()
    {
        var t = new LocalizedText { ["fr"] = "Bonjour" };
        Assert.Equal("Bonjour", t.Resolve("en", out var fallback));
        Assert.True(fallback);
        Assert.Equal("Bonjour", t.Resolve("fr", out fallback));
        Assert.False(fallback);
    }

    [Fact]
    public void Draft_entries_are_never_considered_verified()
    {
        var e = new KnowledgeEntry { Verification = new Verification { StatusRaw = "draft", Build = "26100.1", Edition = "Professional" } };
        Assert.False(e.IsVerifiedFor("Professional", 26100));

        e.Verification.StatusRaw = "config-verified";
        Assert.True(e.IsVerifiedFor("Professional", 26100));
        Assert.False(e.IsVerifiedFor("Professional", 26300));
        Assert.False(e.IsVerifiedFor("Core", 26100));
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SerWin.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable");
    }
}
