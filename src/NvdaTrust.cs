using System;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>Telling the real NVDA from a program that only calls itself nvda.exe (the Kova Helper add-on's pipe trusts only NVDA).</summary>
    internal static class NvdaTrust
    {

        /// <summary>
        /// Whether a program is really NVDA. Its name alone is not enough: any program can be called nvda.exe, and it
        /// could then hand controlling PCs made-up "NVDA speech" through the Kova Helper pipe. So it must be nvda.exe in
        /// Program Files, NVDA (only administrators can put files there), or, for a
        /// portable copy anywhere else, signed by NV Access.
        /// </summary>
        internal static bool IsTrustedNvda(string exe) => TrustedNvdaPath(exe, SignedByNvAccess);

        /// <summary>The rule, with the signature check passed in (so tests can check the rule without signed files).</summary>
        internal static bool TrustedNvdaPath(string exe, Func<string, bool> signedByNvAccess)
        {
            if (!System.IO.Path.IsPathFullyQualified(exe) || exe.Contains("..", StringComparison.Ordinal)) return false;
            if (!System.IO.Path.GetFileName(exe).Equals("nvda.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string dir = System.IO.Path.GetDirectoryName(exe) ?? "";
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string pf = Environment.GetFolderPath(root);
                if (pf.Length > 0 && dir.Equals(System.IO.Path.Combine(pf, "NVDA"), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return signedByNvAccess(exe);
        }

        /// <summary>True when Windows says the file's signature is good and NV Access signed it (nothing is fetched from the internet).</summary>
        internal static bool SignedByNvAccess(string path)
        {
            try
            {
                var file = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
                IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                try
                {
                    Marshal.StructureToPtr(file, pFile, false);
                    var data = new WINTRUST_DATA
                    {
                        cbStruct = Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */,
                        pFile = pFile, dwStateAction = 1 /* verify */, dwProvFlags = 0x1000 /* cached URLs only */ | 0x10 /* no revocation */,
                    };
                    var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                    int rc = WinVerifyTrust((IntPtr)(-1), ref action, ref data);
                    data.dwStateAction = 2; // close
                    WinVerifyTrust((IntPtr)(-1), ref action, ref data);
                    Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                    if (rc != 0) return false;
                }
                finally { Marshal.FreeHGlobal(pFile); }
#pragma warning disable SYSLIB0057
                using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                return cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) == "NV Access Limited";
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile, pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public int dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    }
}
