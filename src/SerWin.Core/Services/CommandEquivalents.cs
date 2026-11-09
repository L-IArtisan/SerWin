namespace SerWin.Core.Services;

/// <summary>Commandes `sc` / `reg` équivalentes, affichées dans l'onglet Technique.</summary>
public static class CommandEquivalents
{
    public static IReadOnlyList<(string Purpose, string Command)> ForRead(ServiceInfo s) =>
    [
        ("config", $"sc qc {Quote(s.Name)}"),
        ("description", $"sc qdescription {Quote(s.Name)}"),
        ("triggers", $"sc qtriggerinfo {Quote(s.Name)}"),
        ("recovery", $"sc qfailure {Quote(s.Name)}"),
        ("status", $"sc queryex {Quote(s.Name)}"),
        ("registry", $"reg query \"{s.RegistryPath}\""),
        ("sddl", $"sc sdshow {Quote(s.Name)}"),
    ];

    /// <summary>Commande de changement de type de démarrage (affichée à titre informatif).</summary>
    public static string SetStartMode(ServiceInfo s, ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => $"sc config {Quote(s.Name)} start= auto",
        ServiceStartMode.AutomaticDelayed => $"sc config {Quote(s.Name)} start= delayed-auto",
        ServiceStartMode.Manual => $"sc config {Quote(s.Name)} start= demand",
        ServiceStartMode.Disabled => $"sc config {Quote(s.Name)} start= disabled",
        _ => "",
    };

    private static string Quote(string n) => n.Contains(' ') ? $"\"{n}\"" : n;
}
