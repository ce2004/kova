using System;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>Windows calls for the service, its session agent and the secure desktop.</summary>
    internal static class NativeService
    {
        // ---- Service control ----

        public delegate void ServiceMainProc(int argc, IntPtr argv);
        public delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_TABLE_ENTRY { public IntPtr Name; public ServiceMainProc? Proc; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRY[] table);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string? machine, string? database, int access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string name, int access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>A service's state (SERVICE_STOPPED and so on), or 0 if it is not there or cannot be asked.</summary>
        public static int ServiceState(string name)
        {
            IntPtr scm = OpenSCManagerW(null, null, 0x0001); // SC_MANAGER_CONNECT
            if (scm == IntPtr.Zero) return 0;
            try
            {
                IntPtr svc = OpenServiceW(scm, name, 0x0004); // SERVICE_QUERY_STATUS
                if (svc == IntPtr.Zero) return 0;
                try { return QueryServiceStatus(svc, out var s) ? s.CurrentState : 0; }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public const int SERVICE_WIN32_OWN_PROCESS = 0x10;
        public const int SERVICE_START_PENDING = 2, SERVICE_RUNNING = 4, SERVICE_STOP_PENDING = 3, SERVICE_STOPPED = 1;
        public const int SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
        public const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_SHUTDOWN = 5, SERVICE_CONTROL_SESSIONCHANGE = 14;

        // ---- Starting the agent in the console session, as SYSTEM ----

        [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUserW(IntPtr token, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetUserProfileDirectoryW(IntPtr token, System.Text.StringBuilder? path, ref int size);

        public const uint TOKEN_ALL_ACCESS = 0xF01FF;
        public const int TokenSessionId = 12, SecurityImpersonation = 2, TokenPrimary = 1;
        public const uint CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x400;

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        public static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
        public static readonly Guid FolderLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        /// <summary>
        /// Where the signed-in user's own folder really is (Downloads moved to another drive or
        /// OneDrive included), or null if nobody is signed in or Windows does not say.
        /// </summary>
        public static string? UserFolder(uint session, Guid folder)
        {
            if (!WTSQueryUserToken(session, out IntPtr token)) return null;
            try
            {
                if (SHGetKnownFolderPath(ref folder, 0, token, out IntPtr p) != 0) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            finally { CloseHandle(token); }
        }

        /// <summary>A known folder of the user this thread runs as (the impersonated one, if any), or null.</summary>
        public static string? ThreadUserFolder(Guid folder)
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (SHGetKnownFolderPath(ref folder, 0, me.Token, out IntPtr p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        /// <summary>This user's own folder, wherever it really is, or null if Windows does not say.</summary>
        public static string? MyFolder(Guid folder)
        {
            if (SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        /// <summary>
        /// The user signed in at the screen, to act as them, or null if nobody is. The service runs as
        /// SYSTEM, which may read and write anywhere: what a controlling PC does with files must have
        /// only that user's rights.
        /// </summary>
        public static System.Security.Principal.WindowsIdentity? ConsoleUser()
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out IntPtr token)) return null;
            try { return new System.Security.Principal.WindowsIdentity(token); }
            catch { return null; }
            finally { CloseHandle(token); }
        }

        /// <summary>The signed-in user's profile folder in a session, or null if nobody is signed in.</summary>
        public static string? UserProfile(uint session)
        {
            if (!WTSQueryUserToken(session, out IntPtr token)) return null;
            try
            {
                int size = 260;
                var sb = new System.Text.StringBuilder(size);
                return GetUserProfileDirectoryW(token, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(token); }
        }

        // ---- The secure desktop: lock screen, sign-in and UAC prompts ----

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetUserObjectInformationW(IntPtr obj, int index, System.Text.StringBuilder? info, int length, out int needed);

        public static string DesktopName(IntPtr desktop)
        {
            var sb = new System.Text.StringBuilder(256);
            return GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, sb, sb.Capacity * 2, out _) ? sb.ToString() : "";
        }

        // ---- Ctrl+Alt+Del, which only a service may send ----

        [DllImport("sas.dll")] public static extern void SendSAS(bool asUser);
    }
}
