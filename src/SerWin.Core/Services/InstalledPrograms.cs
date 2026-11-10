using Microsoft.Win32;

namespace SerWin.Core.Services;

/// <summary>Un logiciel installé (Programmes et fonctionnalités).</summary>
public sealed record InstalledProgram(string Name, string? Publisher, string? Version, string? Folder);

/// <summary>
/// Logiciels installés, lus dans les clés Uninstall (64 bits, 32 bits et utilisateur) — sans droits administrateur.
/// Sert à dire « ce service a été installé avec tel logiciel ».
/// </summary>
public sealed class InstalledPrograms
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> Programs { get; }

    public InstalledPrograms(IEnumerable<InstalledProgram> programs) => Programs = programs.ToList();

    public static InstalledPrograms Load()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    list.Add(new InstalledProgram(name.Trim(), k.GetValue("Publisher") as string, k.GetValue("DisplayVersion") as string,
                        FolderOf(k.GetValue("InstallLocation") as string)
                        ?? FolderOf(ExePath(k.GetValue("DisplayIcon") as string))
                        ?? FolderOf(ExePath(k.GetValue("UninstallString") as string))));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Branche illisible : on fait avec le reste.
            }
        }
        return new InstalledPrograms(list.DistinctBy(p => (p.Name, p.Folder)));
    }

    /// <summary>
    /// Logiciel dont le dossier d'installation contient l'exécutable du service (le plus précis gagne).
    /// Les dossiers système (Windows, System32, Program Files lui-même) ne comptent pas.
    /// </summary>
    public InstalledProgram? FindFor(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var exeDir = FolderOf(executable);
        if (exeDir is null) return null;
        return Programs
            .Where(p => p.Folder is { } f && !IsGenericFolder(f)
                        && (exeDir.Equals(f, StringComparison.OrdinalIgnoreCase)
                            || exeDir.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Folder!.Length)
            .FirstOrDefault();
    }

    /// <summary>Extrait le chemin d'un exe d'une ligne de commande ou d'une icône (« "C:\x\a.exe",0 », « C:\x\a.exe /u »).</summary>
    internal static string? ExePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            return end > 1 ? v[1..end] : null;
        }
        var exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return v[..(exe + 4)];
        var comma = v.IndexOf(',');
        return comma > 0 ? v[..comma] : v;
    }

    internal static string? FolderOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd('\\', '/');
            if (p.Length < 4) return null;
            // Un chemin de fichier → son dossier ; un dossier → lui-même.
            var dir = Path.HasExtension(p) && !Directory.Exists(p) ? Path.GetDirectoryName(p) : p;
            return string.IsNullOrEmpty(dir) ? null : Path.GetFullPath(dir).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsGenericFolder(string folder)
    {
        string[] generic =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        return generic.Any(g => g.Length > 0 && folder.TrimEnd('\\').Equals(g.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }
}
