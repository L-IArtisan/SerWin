namespace SerWin.Core.Services;

/// <summary>Extrait le chemin du fichier exécutable d'une ligne de commande de service (ImagePath).</summary>
public static class ExecutablePath
{
    public static string? Extract(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var s = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        string path;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            path = end > 0 ? s[1..end] : s.Trim('"');
        }
        else
        {
            // Chemin sans guillemets : on coupe après le premier « .exe » suivi d'un espace ou de la fin.
            path = s;
            var idx = 0;
            var found = false;
            while ((idx = s.IndexOf(".exe", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (idx + 4 == s.Length || s[idx + 4] == ' ')
                {
                    path = s[..(idx + 4)];
                    found = true;
                    break;
                }
                idx += 4;
            }
            if (!found && s.Contains(' ') && !File.Exists(s)) path = s.Split(' ')[0];
        }

        return NormalizeNtPath(path);
    }

    /// <summary>Convertit les formes \SystemRoot\…, \??\C:\…, system32\… en chemin Win32.</summary>
    public static string NormalizeNtPath(string path)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windir, path[@"\SystemRoot\".Length..]);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];
        if (path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windir, path);
        return path;
    }
}
