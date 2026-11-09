using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace SerWin.Core.Processes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignatureStatus
{
    /// <summary>Signature Authenticode intégrée au fichier, valide.</summary>
    Signed,
    /// <summary>Signé via un catalogue système (cas de la plupart des fichiers de Windows), valide.</summary>
    SignedCatalog,
    /// <summary>Aucune signature.</summary>
    Unsigned,
    /// <summary>Signature présente mais invalide (fichier modifié, certificat non approuvé ou révoqué).</summary>
    Invalid,
    /// <summary>Fichier inaccessible : impossible de vérifier.</summary>
    Unknown,
}

public sealed record SignatureInfo(SignatureStatus Status, string? Signer = null, int ErrorCode = 0)
{
    public bool IsTrusted => Status is SignatureStatus.Signed or SignatureStatus.SignedCatalog;
    public bool IsMicrosoft => Signer?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Vérification Authenticode via WinVerifyTrust, signature intégrée puis catalogue.
/// Zéro réseau : pas de contrôle de révocation en ligne (WTD_REVOKE_NONE + WTD_CACHE_ONLY_URL_RETRIEVAL).
/// </summary>
public static class SignatureVerifier
{
    private static readonly Dictionary<string, SignatureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SignatureInfo Verify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new SignatureInfo(SignatureStatus.Unknown);
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }
        SignatureInfo result;
        try
        {
            result = VerifyUncached(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            result = new SignatureInfo(SignatureStatus.Unknown);
        }
        lock (Cache) Cache[path] = result;
        return result;
    }

    private static SignatureInfo VerifyUncached(string path)
    {
        var embedded = VerifyEmbedded(path);
        if (embedded == 0) return new SignatureInfo(SignatureStatus.Signed, SignerOf(path));
        // Fichier signé mais altéré / non approuvé : inutile de chercher un catalogue.
        if (embedded is not (TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN))
            return new SignatureInfo(SignatureStatus.Invalid, SignerOf(path), embedded);

        var (catalogResult, catalogFile) = VerifyCatalog(path);
        if (catalogFile is null) return new SignatureInfo(SignatureStatus.Unsigned);
        return catalogResult == 0
            ? new SignatureInfo(SignatureStatus.SignedCatalog, SignerOf(catalogFile))
            : new SignatureInfo(SignatureStatus.Invalid, SignerOf(catalogFile), catalogResult);
    }

    private static string? SignerOf(string signedFile)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }

    // ── WinVerifyTrust ───────────────────────────────────────
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    private static int VerifyEmbedded(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            return Run(WTD_CHOICE_FILE, filePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static (int Result, string? CatalogFile) VerifyCatalog(string path)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0)) return (-1, null);
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint size = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
            if (size == 0) return (-1, null);
            var hash = new byte[size];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0)) return (-1, null);

            var catInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
            if (catInfo == IntPtr.Zero) return (-1, null);
            try
            {
                var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (!CryptCATCatalogInfoFromContext(catInfo, ref ci, 0)) return (-1, null);

                var catPtr = Marshal.StringToHGlobalUni(ci.wszCatalogFile);
                var tagPtr = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
                var memberPtr = Marshal.StringToHGlobalUni(path);
                var hashPtr = Marshal.AllocHGlobal(hash.Length);
                var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                try
                {
                    Marshal.Copy(hash, 0, hashPtr, hash.Length);
                    Marshal.StructureToPtr(new WINTRUST_CATALOG_INFO
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                        pcwszCatalogFilePath = catPtr,
                        pcwszMemberTag = tagPtr,
                        pcwszMemberFilePath = memberPtr,
                        pbCalculatedFileHash = hashPtr,
                        cbCalculatedFileHash = size,
                        hCatAdmin = admin,
                    }, infoPtr, false);
                    return (Run(WTD_CHOICE_CATALOG, infoPtr), ci.wszCatalogFile);
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                    Marshal.FreeHGlobal(hashPtr);
                    Marshal.FreeHGlobal(memberPtr);
                    Marshal.FreeHGlobal(tagPtr);
                    Marshal.FreeHGlobal(catPtr);
                }
            }
            finally
            {
                CryptCATAdminReleaseCatalogContext(admin, catInfo, 0);
            }
        }
        finally
        {
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static int Run(uint unionChoice, IntPtr union)
    {
        var action = GenericVerifyV2;
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pUnion = union,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        return result;
    }
}

/// <summary>Emplacements d'où un programme légitime ne devrait pas s'exécuter (spec §5.6).</summary>
public static class SuspiciousLocation
{
    private static readonly Lazy<string[]> Roots = new(() =>
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "Téléchargements"),
            Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
            @"C:\$Recycle.Bin",
            @"C:\PerfLogs",
        };
        return list.Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('\\') + "\\").ToArray();
    });

    public static bool IsSuspicious(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Roots.Value.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))) return true;
        // Un exécutable posé directement à la racine de ProgramData ou d'AppData est inhabituel.
        var dir = Path.GetDirectoryName(path)?.TrimEnd('\\');
        return dir is not null && (dir.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)
                                   || dir.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase));
    }
}
