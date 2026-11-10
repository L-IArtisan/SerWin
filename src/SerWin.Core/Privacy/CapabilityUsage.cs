using System.Diagnostics;
using Microsoft.Win32;

namespace SerWin.Core.Privacy;

/// <summary>
/// Une application qui a utilisé une fonction sensible (caméra, micro, localisation), d'après le journal que Windows
/// tient lui-même (CapabilityAccessManager\ConsentStore) — celui qui alimente l'icône « micro utilisé » de la barre des tâches.
/// </summary>
/// <param name="AppKey">Nom de la clé : famille de paquet (« Claude_pzs8… ») ou chemin d'exe avec « # » à la place de « \ ».</param>
/// <param name="Permission">« Allow », « Deny » ou null (applications de bureau : réglage global seulement).</param>
/// <param name="MachineWide">Relevé dans HKLM (services, autres comptes) plutôt que pour l'utilisateur courant.</param>
public sealed record CapabilityUse(
    string Capability,
    string AppKey,
    string DisplayName,
    string? ExecutablePath,
    bool IsPackaged,
    string? Permission,
    DateTime? LastStart,
    DateTime? LastStop,
    bool MachineWide)
{
    /// <summary>En cours d'utilisation : un début plus récent que la dernière fin.</summary>
    public bool InUse => LastStart is { } s && (LastStop is null || LastStop < s);

    public TimeSpan? Duration => LastStart is { } s && LastStop is { } e && e >= s ? e - s : null;

    public DateTime? LastUsed => InUse ? DateTime.Now : LastStop ?? LastStart;
}

public static class CapabilityUsage
{
    public const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>Fonctions suivies par SerWin.</summary>
    public static readonly IReadOnlyList<string> Tracked = ["webcam", "microphone", "location"];

    /// <summary>Réglage global (« Allow » / « Deny ») : pour l'appareil (HKLM) et pour l'utilisateur (HKCU).</summary>
    public static (string? Device, string? User) GlobalPermission(string capability) =>
        (ReadValue(Registry.LocalMachine, $@"{ConsentStore}\{capability}"), ReadValue(Registry.CurrentUser, $@"{ConsentStore}\{capability}"));

    public static List<CapabilityUse> Read()
    {
        var list = new List<CapabilityUse>();
        foreach (var cap in Tracked)
        {
            ReadHive(list, Registry.CurrentUser, cap, machineWide: false);
            ReadHive(list, Registry.LocalMachine, cap, machineWide: true);
        }
        return list;
    }

    private static void ReadHive(List<CapabilityUse> list, RegistryKey hive, string cap, bool machineWide)
    {
        try
        {
            using var key = hive.OpenSubKey($@"{ConsentStore}\{cap}");
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var np = key.OpenSubKey(sub);
                    if (np is null) continue;
                    foreach (var app in np.GetSubKeyNames())
                    {
                        using var k = np.OpenSubKey(app);
                        if (k is not null && FromKey(cap, app, k, packaged: false, machineWide) is { } use) list.Add(use);
                    }
                }
                else
                {
                    using var k = key.OpenSubKey(sub);
                    if (k is not null && FromKey(cap, sub, k, packaged: true, machineWide) is { } use) list.Add(use);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Branche illisible (HKLM sans droits suffisants) : on passe.
        }
    }

    private static CapabilityUse? FromKey(string cap, string appKey, RegistryKey k, bool packaged, bool machineWide) =>
        Create(cap, appKey, packaged, k.GetValue("Value") as string, k.GetValue("LastUsedTimeStart") as long?,
            k.GetValue("LastUsedTimeStop") as long?, machineWide);

    /// <summary>Construit une utilisation à partir des valeurs brutes ; null si l'application n'a jamais utilisé la fonction.</summary>
    public static CapabilityUse? Create(string cap, string appKey, bool packaged, string? permission, long? start, long? stop, bool machineWide = false)
    {
        var s = FileTime(start);
        if (s is null) return null;
        var path = packaged ? null : appKey.Replace('#', '\\');
        return new CapabilityUse(cap, appKey, DisplayNameOf(appKey, path, packaged), path, packaged, permission, s, FileTime(stop), machineWide);
    }

    /// <summary>« Claude_pzs8sxrjxfjjc » → « Claude » ; un exe → sa description de fichier, sinon son nom.</summary>
    internal static string DisplayNameOf(string appKey, string? path, bool packaged)
    {
        if (packaged)
        {
            var family = appKey.Split('_')[0];
            return family.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? family["Microsoft.".Length..] : family;
        }
        if (path is not null && File.Exists(path))
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch (FileNotFoundException)
            {
                // Fichier disparu : on garde le nom.
            }
        }
        return Path.GetFileName(path ?? appKey);
    }

    private static DateTime? FileTime(long? ft)
    {
        if (ft is not { } v || v <= 0) return null;
        try
        {
            return DateTime.FromFileTime(v);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadValue(RegistryKey hive, string path)
    {
        try
        {
            using var k = hive.OpenSubKey(path);
            return k?.GetValue("Value") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
