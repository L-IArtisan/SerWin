namespace SerWin.Knowledge;

/// <summary>Contrôles de cohérence d'une fiche (utilisés par SerWin.Verify et les tests).</summary>
public static class KnowledgeValidator
{
    private static readonly HashSet<string> RiskValues = ["faible", "moyen", "critique"];
    private static readonly HashSet<string> StatusValues = ["draft", "config-verified", "behavior-observed"];
    private static readonly HashSet<string> ReviverTypes = ["service", "task", "trigger", "policy", "software", "update"];
    private static readonly string[] Languages = ["fr", "en"];

    public sealed record Issue(string Entry, string Severity, string Message)
    {
        public override string ToString() => $"[{Severity}] {Entry}: {Message}";
    }

    public static List<Issue> Validate(KnowledgeEntry e)
    {
        var issues = new List<Issue>();
        void Err(string m) => issues.Add(new(e.Name, "error", m));
        void Warn(string m) => issues.Add(new(e.Name, "warning", m));

        if (string.IsNullOrWhiteSpace(e.Name)) Err("name manquant");
        if (!RiskValues.Contains(e.RiskRaw)) Err($"risk invalide « {e.RiskRaw} » (faible | moyen | critique)");
        if (!KnowledgeCategories.All.Contains(e.Category)) Err($"category inconnue « {e.Category} »");
        if (!StatusValues.Contains(e.Verification.StatusRaw)) Err($"verification.status invalide « {e.Verification.StatusRaw} »");
        if (e.Verification.Status != VerificationStatus.Draft && (e.Verification.Build is null || e.Verification.Edition is null))
            Err("une fiche vérifiée doit indiquer build et edition");
        if (e.BootCritical && e.Risk != RiskLevel.Critical) Err("bootCritical implique risk = critique");

        // Le français (langue source) est obligatoire.
        Require(e.DisplayName, "displayName");
        Require(e.Summary, "summary");
        Require(e.Levels.Simple, "levels.simple");
        Require(e.Levels.Avance, "levels.avance");
        Require(e.Levels.Expert, "levels.expert");
        for (var i = 0; i < e.IfDisabled.Count; i++) Require(e.IfDisabled[i], $"ifDisabled[{i}]");
        if (e.VerdictIfNot is not null) Require(e.VerdictIfNot, "verdictIfNot");

        foreach (var r in e.Revivers)
            if (!ReviverTypes.Contains(r.Type)) Err($"reviver de type inconnu « {r.Type} »");

        return issues;

        void Require(LocalizedText t, string field)
        {
            if (!t.IsTranslated(LocalizedText.SourceLanguage)) Err($"{field}: texte français manquant");
            foreach (var lang in Languages)
                if (lang != LocalizedText.SourceLanguage && !t.IsTranslated(lang)) Warn($"{field}: non traduit ({lang})");
        }
    }

    public static List<Issue> Validate(KnowledgeBase kb)
    {
        var issues = kb.LoadErrors.Select(m => new Issue("(chargement)", "error", m)).ToList();
        foreach (var e in kb.Services.Concat(kb.Processes)) issues.AddRange(Validate(e));
        foreach (var d in kb.Destinations) issues.AddRange(Validate(d));

        // Un même motif dans deux fiches rendrait le résultat dépendant de l'ordre de chargement.
        foreach (var dup in kb.Destinations.SelectMany(d => d.Patterns.Select(p => (Pattern: p.ToLowerInvariant(), d.Name)))
                     .GroupBy(x => x.Pattern).Where(g => g.Count() > 1))
            issues.Add(new(string.Join(", ", dup.Select(x => x.Name)), "error", $"motif « {dup.Key} » présent dans plusieurs fiches"));
        return issues;
    }

    private static readonly System.Text.RegularExpressions.Regex HostPattern =
        new(@"^(\*\.)?([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z0-9-]{2,}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Contrôles d'une fiche « destination » (v1.1).</summary>
    public static List<Issue> Validate(DestinationEntry d)
    {
        var issues = new List<Issue>();
        void Err(string m) => issues.Add(new(d.Name, "error", m));
        void Warn(string m) => issues.Add(new(d.Name, "warning", m));

        if (string.IsNullOrWhiteSpace(d.Name)) Err("name manquant");
        if (d.Patterns.Count == 0) Err("aucun motif (patterns)");
        foreach (var p in d.Patterns)
            if (!HostPattern.IsMatch(p)) Err($"motif invalide « {p} » (nom d'hôte en minuscules, éventuellement précédé de « *. »)");
        if (string.IsNullOrWhiteSpace(d.Owner)) Err("owner manquant");
        if (!DestinationCategories.All.Contains(d.Category)) Err($"category inconnue « {d.Category} »");
        if (!DestinationCategories.NeedValues.Contains(d.NeedRaw)) Err($"need invalide « {d.NeedRaw} » (essentiel | utile | optionnel | indesirable)");
        if (!StatusValues.Contains(d.Verification.StatusRaw)) Err($"verification.status invalide « {d.Verification.StatusRaw} »");

        Require(d.DisplayName, "displayName");
        Require(d.Summary, "summary");
        Require(d.Levels.Simple, "levels.simple");
        Require(d.Levels.Avance, "levels.avance");
        Require(d.Levels.Expert, "levels.expert");
        for (var i = 0; i < d.IfBlocked.Count; i++) Require(d.IfBlocked[i], $"ifBlocked[{i}]");
        if (d.Control is not null) Require(d.Control, "control");
        if (d.VerdictIfNot is not null) Require(d.VerdictIfNot, "verdictIfNot");
        return issues;

        void Require(LocalizedText t, string field)
        {
            if (!t.IsTranslated(LocalizedText.SourceLanguage)) Err($"{field}: texte français manquant");
            foreach (var lang in Languages)
                if (lang != LocalizedText.SourceLanguage && !t.IsTranslated(lang)) Warn($"{field}: non traduit ({lang})");
        }
    }
}
