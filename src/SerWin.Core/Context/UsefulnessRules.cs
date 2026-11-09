using System.Text.Json.Serialization;

namespace SerWin.Core.Context;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Usefulness
{
    /// <summary>Au moins une condition d'utilité est remplie sur cette machine.</summary>
    Useful,
    /// <summary>Aucune condition remplie, toutes ont pu être vérifiées.</summary>
    NotUseful,
    /// <summary>Pas de règle, ou détection impossible : SerWin ne se prononce pas.</summary>
    Unknown,
}

/// <summary>Une condition évaluée : clé, attendu, fait observé, résultat.</summary>
public sealed record RuleCheck(string Key, string Expected, Fact Fact, bool? Satisfied);

public sealed record UsefulnessVerdict(Usefulness Verdict, IReadOnlyList<RuleCheck> Checks)
{
    public static UsefulnessVerdict NoRule { get; } = new(Usefulness.Unknown, []);
}

/// <summary>
/// Évalue les règles « usefulIf » d'une fiche (spec §5.7). Les conditions sont des alternatives :
/// le service est utile dès qu'un de ses usages est présent.
/// Valeurs reconnues : « >0 », « >=N », « 0 », « true », « false », « present », « absent », « installed », « none ».
/// </summary>
public static class UsefulnessRules
{
    public static UsefulnessVerdict Evaluate(IReadOnlyDictionary<string, string> usefulIf, MachineContext context)
    {
        if (usefulIf.Count == 0) return UsefulnessVerdict.NoRule;
        var checks = usefulIf.Select(r => new RuleCheck(r.Key, r.Value, context[r.Key], Check(r.Value, context[r.Key]))).ToList();
        var verdict =
            checks.Any(c => c.Satisfied == true) ? Usefulness.Useful
            : checks.All(c => c.Satisfied == false) ? Usefulness.NotUseful
            : Usefulness.Unknown;
        return new UsefulnessVerdict(verdict, checks);
    }

    /// <summary>Vrai / faux, ou null si le fait est inconnu ou la condition illisible.</summary>
    public static bool? Check(string expected, Fact fact)
    {
        if (fact.Count is not { } n) return null;
        var e = expected.Trim().ToLowerInvariant();
        return e switch
        {
            "true" or "present" or "installed" or "yes" => n > 0,
            "false" or "absent" or "none" or "no" => n == 0,
            _ when e.StartsWith(">=") && int.TryParse(e[2..], out var min) => n >= min,
            _ when e.StartsWith('>') && int.TryParse(e[1..], out var gt) => n > gt,
            _ when int.TryParse(e, out var eq) => n == eq,
            _ => null,
        };
    }

    public static bool IsKnownCondition(string expected) =>
        Check(expected, new Fact(1)) is not null;
}
