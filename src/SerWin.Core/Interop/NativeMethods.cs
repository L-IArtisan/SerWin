using System.Runtime.InteropServices;

namespace SerWin.Core.Interop;

/// <summary>Déclarations P/Invoke advapi32 pour le gestionnaire de services (SCM).</summary>
internal static class NativeMethods
{
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;

    public const uint SERVICE_WIN32 = 0x00000030;
    public const uint SERVICE_STATE_ALL = 0x00000003;
    public const int SC_ENUM_PROCESS_INFO = 0;

    public const uint SERVICE_CONFIG_DESCRIPTION = 1;
    public const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    public const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    public const uint SERVICE_CONFIG_TRIGGER_INFO = 8;

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ENUM_SERVICE_STATUS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        // SERVICE_STATUS (7 DWORD)
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_DESCRIPTION
    {
        public IntPtr lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_DELAYED_AUTO_START_INFO
    {
        public int fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_TRIGGER_INFO
    {
        public uint cTriggers;
        public IntPtr pTriggers;
        public IntPtr pReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_TRIGGER
    {
        public uint dwTriggerType;
        public uint dwAction;
        public IntPtr pTriggerSubtype;
        public uint cDataItems;
        public IntPtr pDataItems;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_TRIGGER_SPECIFIC_DATA_ITEM
    {
        public uint dwDataType;
        public uint cbData;
        public IntPtr pData;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumServicesStatusExW(
        IntPtr hSCManager, int infoLevel, uint dwServiceType, uint dwServiceState,
        IntPtr lpServices, uint cbBufSize, out uint pcbBytesNeeded, out uint lpServicesReturned,
        ref uint lpResumeHandle, string? pszGroupName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfigW(IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceConfig2W(IntPtr hService, uint dwInfoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDependentServicesW(IntPtr hService, uint dwServiceState, IntPtr lpServices, uint cbBufSize, out uint pcbBytesNeeded, out uint lpServicesReturned);

    // ── Écriture (v0.2) ──────────────────────────────────────
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_START = 0x0010;
    public const uint READ_CONTROL = 0x00020000;
    public const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    public const uint DACL_SECURITY_INFORMATION = 0x4;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId, string? lpDependencies,
        string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref SERVICE_DELAYED_AUTO_START_INFO lpInfo);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2Ptr(IntPtr hService, uint dwInfoLevel, IntPtr lpInfo);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceObjectSecurity(IntPtr hService, uint dwSecurityInformation, IntPtr lpSecurityDescriptor, uint cbBufSize, out uint pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr securityDescriptor, uint requestedRevision, uint securityInformation, out IntPtr stringSecurityDescriptor, out uint stringLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Lit une liste de chaînes MULTI_SZ (terminée par un double NUL).</summary>
    public static List<string> ReadMultiSz(IntPtr ptr)
    {
        var result = new List<string>();
        if (ptr == IntPtr.Zero) return result;
        while (true)
        {
            var s = Marshal.PtrToStringUni(ptr);
            if (string.IsNullOrEmpty(s)) break;
            result.Add(s);
            ptr += (s.Length + 1) * 2;
        }
        return result;
    }
}
