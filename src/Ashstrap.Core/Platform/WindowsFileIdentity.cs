using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Ashstrap.Core.Platform;

/// <summary>
/// Decides whether an executable/DLL really belongs to Roblox:
///   1. Its version resource says CompanyName = "Roblox Corporation", and
///   2. It has a valid Authenticode signature (verified offline, no revocation lookup), and
///   3. The signing certificate's subject is Roblox Corporation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileIdentity : IFileIdentity
{
    private const string RobloxCompany = "Roblox Corporation";
    private readonly ConcurrentDictionary<(string, long, DateTime), bool> _cache = new();

    public bool IsRobloxBinary(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var key = (info.FullName.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc);
            return _cache.GetOrAdd(key, _ => Evaluate(info.FullName));
        }
        catch
        {
            return false;
        }
    }

    private static bool Evaluate(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.CompanyName?.Trim(), RobloxCompany, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!WinTrust.IsSignatureValid(path))
                return false;

#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the supported way to read the Authenticode signer
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var simpleName = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.Equals(simpleName, RobloxCompany, StringComparison.OrdinalIgnoreCase)
                || signer.Subject.Contains("O=Roblox Corporation", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
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
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        public static bool IsSignatureValid(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    // Offline-friendly: never go to the network for revocation lists.
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
                };
                var action = GenericVerifyV2;
                int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result == 0;
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
