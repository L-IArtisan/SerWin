using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SerWin.Core.Services;

namespace SerWin.Core.Startup;

/// <summary>Où Windows a trouvé un programme lancé à l'ouverture de session.</summary>
public enum StartupSource
{
    /// <summary>HKCU\…\Run : pour cet utilisateur.</summary>
    RunUser,
    /// <summary>HKLM\…\Run : pour tous les utilisateurs (64 bits).</summary>
    RunMachine,
    /// <summary>HKLM\WOW6432Node\…\Run : pour tous les utilisateurs (programmes 32 bits).</summary>
    RunMachine32,
    /// <summary>Dossier Démarrage de l'utilisateur.</summary>
    FolderUser,
    /// <summary>Dossier Démarrage commun à tous les utilisateurs.</summary>
    FolderCommon,
}

/// <summary>
/// Un programme lancé à l'ouverture de session. Son état activé / désactivé est celui que gèrent le Gestionnaire des
/// tâches et Paramètres > Applications > Démarrage (clé StartupApproved) : désactiver ne supprime rien.
/// </summary>
/// <param name="ApprovalTarget">Valeur StartupApproved correspondante, au format « HIVE\sous-clé|nom ».</param>
public sealed record StartupItem(
    StartupSource Source,
    string Name,
    string Command,
    string? Executable,
    bool Enabled,
    DateTime? DisabledAt,
    string ApprovalTarget)
{
    public bool IsMachineWide => Source is StartupSource.RunMachine or StartupSource.RunMachine32 or StartupSource.FolderCommon;
    public string Id => $"{Source}|{Name}";
}

/// <summary>Encodage de la valeur StartupApproved (12 octets : état puis FILETIME de désactivation).</summary>
public static class StartupApproval
{
    public const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Premier octet pair (02, 06) = activé ; impair (03, 07) = désactivé. Valeur absente = activé.</summary>
    public static (bool Enabled, DateTime? DisabledAt) Parse(byte[]? value)
    {
        if (value is null || value.Length == 0) return (true, null);
        var enabled = (value[0] & 1) == 0;
        DateTime? at = null;
        if (!enabled && value.Length >= 12)
        {
            var ft = BitConverter.ToInt64(value, 4);
            if (ft > 0)
            {
                try
                {
                    at = DateTime.FromFileTime(ft);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Horodatage invalide : on l'ignore.
                }
            }
        }
        return (enabled, at);
    }

    /// <summary>Valeur écrite par Windows : 02 + zéros pour activer, 03 + date pour désactiver.</summary>
    public static byte[] Encode(bool enabled, DateTime now)
    {
        var bytes = new byte[12];
        bytes[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(now.ToFileTime()).CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>Représentation texte pour l'historique : hexadécimal, ou « absent ».</summary>
    public static string ToText(byte[]? value) => value is null ? "absent" : Convert.ToHexString(value);

    public static byte[]? FromText(string text) => text == "absent" ? null : Convert.FromHexString(text);

    /// <summary>État lisible depuis la représentation texte.</summary>
    public static bool IsEnabled(string text) => Parse(FromText(text)).Enabled;
}

/// <summary>Lecture des programmes lancés à l'ouverture de session (sans droits administrateur).</summary>
public static class StartupReader
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public static List<StartupItem> Read()
    {
        var list = new List<StartupItem>();
        ReadRun(list, Registry.CurrentUser, "HKCU", RunKey, StartupSource.RunUser, "Run");
        ReadRun(list, Registry.LocalMachine, "HKLM", RunKey, StartupSource.RunMachine, "Run");
        ReadRun(list, Registry.LocalMachine, "HKLM", Run32Key, StartupSource.RunMachine32, "Run32");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "HKCU", StartupSource.FolderUser);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "HKLM", StartupSource.FolderCommon);
        return list;
    }

    private static void ReadRun(List<StartupItem> list, RegistryKey hive, string hiveName, string path, StartupSource source, string approvedSub)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) continue;
                var (enabled, at) = ReadApproval(hive, approvedSub, name);
                list.Add(new StartupItem(source, name, command, ExecutablePath.Extract(Environment.ExpandEnvironmentVariables(command)),
                    enabled, at, $@"{hiveName}\{StartupApproval.ApprovedRoot}\{approvedSub}|{name}"));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Clé illisible : on passe.
        }
    }

    private static void ReadFolder(List<StartupItem> list, string folder, RegistryKey hive, string hiveName, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : null;
                var command = target is { } t ? (t.Arguments.Length > 0 ? $"\"{t.Path}\" {t.Arguments}" : t.Path) : file;
                var (enabled, at) = ReadApproval(hive, "StartupFolder", fileName);
                list.Add(new StartupItem(source, Path.GetFileNameWithoutExtension(fileName), command, target?.Path ?? file, enabled, at,
                    $@"{hiveName}\{StartupApproval.ApprovedRoot}\StartupFolder|{fileName}"));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Dossier illisible : on passe.
        }
    }

    private static (bool, DateTime?) ReadApproval(RegistryKey hive, string sub, string name)
    {
        try
        {
            using var k = hive.OpenSubKey($@"{StartupApproval.ApprovedRoot}\{sub}");
            return StartupApproval.Parse(k?.GetValue(name) as byte[]);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return (true, null);
        }
    }

    // ── Raccourcis (.lnk) via IShellLinkW, sans dépendance à WScript ──
    private static (string Path, string Arguments)? ShortcutTarget(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(2048);
            link.GetArguments(args, args.Capacity);
            Marshal.ReleaseComObject(link);
            return path.Length == 0 ? null : (path.ToString(), args.ToString());
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }
}
