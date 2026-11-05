using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace LlamaTree.Updates;

/// <summary>Windows' own check of a file's Authenticode signature (WinVerifyTrust), as Explorer and SmartScreen make it.</summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    /// <summary>
    /// The chain of the certificate that signed <paramref name="path"/>, signer first, if its signature is valid: the
    /// file is unchanged since it was signed, the chain ends in a root Windows trusts, the certificate is for code
    /// signing, valid when signed (by its timestamp) and not revoked. Null otherwise, including for an unsigned file.
    /// The file is held open, so it can't change while it's checked.
    /// </summary>
    public static X509Certificate2[]? VerifiedSignerChain(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Revocation is checked; only when it can't be (offline, or the list unreachable) is the signature taken without.
        var result = Verify(path, file, RevokeWholeChain, out var chain);
        if (result is RevocationFailure or RevocationOffline) result = Verify(path, file, RevokeNone, out chain);
        return result == 0 ? chain : null;
    }

    private static unsafe int Verify(string path, SafeFileHandle file, uint revocation, out X509Certificate2[]? chain)
    {
        chain = null;
        fixed (char* name = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = name,
                hFile = file.DangerousGetHandle(),
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = UiNone,
                fdwRevocationChecks = revocation,
                dwUnionChoice = ChoiceFile,
                pFile = &fileInfo,
                dwStateAction = StateVerify,
                dwProvFlags = revocation == RevokeNone ? RevocationCheckNone : RevocationCheckChainExcludeRoot,
            };
            var action = GenericVerifyV2;
            var added = false;
            try
            {
                file.DangerousAddRef(ref added);
                var result = WinVerifyTrust(-1, ref action, ref data);   // INVALID_HANDLE_VALUE: no window, no UI
                if (result == 0) chain = SignerChain(data.hWVTStateData);
                return result == 0 && chain == null ? -1 : result;
            }
            finally
            {
                data.dwStateAction = StateClose;
                WinVerifyTrust(-1, ref action, ref data);
                if (added) file.DangerousRelease();
            }
        }
    }

    /// <summary>The primary signer's certificates as WinVerifyTrust built the chain.</summary>
    private static X509Certificate2[]? SignerChain(IntPtr state)
    {
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return null;
        // CRYPT_PROVIDER_SGNR: cbStruct (4), sftVerifyAsOf (FILETIME, 8), csCertChain (4), ...
        var count = Marshal.ReadInt32(signer, 12);
        var certificates = new List<X509Certificate2>();
        for (var i = 0; i < count; i++)
        {
            // CRYPT_PROVIDER_CERT: cbStruct (4, padded to a pointer), pCert, ...
            var entry = WTHelperGetProvCertFromChain(signer, i);
            if (entry == IntPtr.Zero) break;
            var context = Marshal.ReadIntPtr(entry, IntPtr.Size);
            if (context == IntPtr.Zero) break;
            certificates.Add(new X509Certificate2(context));   // a copy: the context goes with the state
        }
        return certificates.Count > 0 ? [.. certificates] : null;
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    private const uint RevocationCheckNone = 0x10;
    private const uint RevocationCheckChainExcludeRoot = 0x80;
    private const int RevocationFailure = unchecked((int)0x800B010E);   // CERT_E_REVOCATION_FAILURE
    private const int RevocationOffline = unchecked((int)0x80092013);   // CRYPT_E_REVOCATION_OFFLINE

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, int idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, int idxCounterSigner);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, int idxCert);
}
