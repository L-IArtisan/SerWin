using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SerWin.Core.Platform;

/// <summary>Dossier de données %ProgramData%\SerWin\ (photographie, sauvegardes, historique).</summary>
public static class DataDirectory
{
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SerWin");

    /// <summary>
    /// Restreint l'écriture aux administrateurs et à SYSTEM (lecture pour tous).
    /// Empêche un compte standard de falsifier une sauvegarde qu'un administrateur restaurerait ensuite.
    /// </summary>
    public static void Harden(string path)
    {
        if (!OsInfo.IsElevated()) return;
        Directory.CreateDirectory(path);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.SetOwner(admins);
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(sec);
    }

    /// <summary>« Exporter l'historique » (spec §5.4) : ZIP de tout le dossier de données.</summary>
    public static void ExportZip(string path, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(path, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
    }

    /// <summary>« Supprimer toutes les données SerWin » (spec §5.4).</summary>
    public static void DeleteAll(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
