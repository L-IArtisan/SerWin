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
        return issues;
    }
}
