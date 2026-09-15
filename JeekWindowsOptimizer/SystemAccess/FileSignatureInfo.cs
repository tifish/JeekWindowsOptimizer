using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>How much a file's Authenticode signature can be trusted.</summary>
public enum SignatureTrust
{
    /// <summary>The file carries no signature, embedded or catalog.</summary>
    Unsigned,

    /// <summary>A signature exists but did not verify (tampered, expired, untrusted root...).</summary>
    Invalid,

    /// <summary>The signature verified successfully.</summary>
    Valid,
}

/// <summary>The result of verifying one file's Authenticode signature.</summary>
/// <param name="Trust">Whether the signature verified.</param>
/// <param name="SignerName">The CN of the signing certificate, or null when it could not be read.</param>
/// <param name="IsWindowsComponent">True only for a valid signature whose CN starts with "Microsoft Windows".</param>
/// <param name="IsMicrosoft">True only for a valid signature whose CN mentions Microsoft.</param>
public sealed record FileSignature(
    SignatureTrust Trust,
    string? SignerName,
    bool IsWindowsComponent,
    bool IsMicrosoft)
{
    /// <summary>The result used for unsigned files, missing files and unexpected failures.</summary>
    public static readonly FileSignature None = new(SignatureTrust.Unsigned, null, false, false);
}

/// <summary>Authenticode verification for arbitrary files, with a per-file cache for scans.</summary>
public static class FileSignatureInfo
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(FileSignatureInfo));

    private static readonly ConcurrentDictionary<CacheKey, FileSignature> Cache = new(CacheKeyComparer.Instance);

    /// <summary>Verifies a file's Authenticode signature, embedded or via a security catalog. Cached.</summary>
    public static FileSignature Get(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return FileSignature.None;

            var file = new FileInfo(path);
            if (!file.Exists)
                return FileSignature.None;

            var key = new CacheKey(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
            return Cache.GetOrAdd(key, static k => Verify(k.Path));
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to verify the signature of {path}");
            return FileSignature.None;
        }
    }

    /// <summary>Drops every cached verification result, so the next <see cref="Get" /> re-verifies.</summary>
    public static void ClearCache() => Cache.Clear();

    #region Cache key

    private readonly record struct CacheKey(string Path, long Length, long LastWriteTicks);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static readonly CacheKeyComparer Instance = new();

        public bool Equals(CacheKey x, CacheKey y) =>
            x.Length == y.Length
            && x.LastWriteTicks == y.LastWriteTicks
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(CacheKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
                obj.Length,
                obj.LastWriteTicks);
    }

    #endregion

    #region Verification

    private static FileSignature Verify(string path)
    {
        try
        {
            var (result, signer) = VerifyEmbedded(path);

            // Most files under %SystemRoot% carry no embedded signature; they are covered by a
            // security catalog instead, so this fallback is what keeps them from looking unsigned.
            if (result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
                (result, signer) = VerifyCatalog(path);

            return Classify(result, signer);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to verify the signature of {path}");
            return FileSignature.None;
        }
    }

    private static FileSignature Classify(int result, string? signer)
    {
        var trust = result switch
        {
            0 => SignatureTrust.Valid,
            TrustENoSignature => SignatureTrust.Unsigned,
            _ => SignatureTrust.Invalid,
        };

        // An unverified signature must never be allowed to claim it is Microsoft.
        if (trust != SignatureTrust.Valid)
            return new FileSignature(trust, signer, false, false);

        var isWindowsComponent = signer is not null
            && signer.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
        var isMicrosoft = isWindowsComponent
            || (signer is not null && signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

        return new FileSignature(trust, signer, isWindowsComponent, isMicrosoft);
    }

    private static (int Result, string? Signer) VerifyEmbedded(string path)
    {
        var size = Marshal.SizeOf<WinTrustFileInfo>();
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)size,
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var block = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(fileInfo, block, false);
            try
            {
                return RunWinVerifyTrust(WtdChoiceFile, block);
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(block);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private static (int Result, string? Signer) VerifyCatalog(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var hFile = file.DangerousGetHandle();

            if (!CryptCatAdminAcquireContext2(out var catalogAdmin, IntPtr.Zero, Sha256AlgorithmName, IntPtr.Zero, 0)
                && !CryptCatAdminAcquireContext(out catalogAdmin, IntPtr.Zero, 0))
                return (TrustENoSignature, null);

            try
            {
                uint hashSize = 0;
                var modernHash = true;
                SeekToStart(hFile);
                if (!CryptCatAdminCalcHashFromFileHandle2(catalogAdmin, hFile, ref hashSize, IntPtr.Zero, 0)
                    && hashSize == 0)
                {
                    modernHash = false;
                    CryptCatAdminCalcHashFromFileHandle(hFile, ref hashSize, IntPtr.Zero, 0);
                }

                if (hashSize == 0)
                    return (TrustENoSignature, null);

                var hashBuffer = Marshal.AllocHGlobal((int)hashSize);
                try
                {
                    SeekToStart(hFile);
                    var hashed = modernHash
                        ? CryptCatAdminCalcHashFromFileHandle2(catalogAdmin, hFile, ref hashSize, hashBuffer, 0)
                        : CryptCatAdminCalcHashFromFileHandle(hFile, ref hashSize, hashBuffer, 0);
                    if (!hashed || hashSize == 0)
                        return (TrustENoSignature, null);

                    var hash = new byte[hashSize];
                    Marshal.Copy(hashBuffer, hash, 0, hash.Length);

                    var catalogContext =
                        CryptCatAdminEnumCatalogFromHash(catalogAdmin, hashBuffer, hashSize, 0, IntPtr.Zero);
                    if (catalogContext == IntPtr.Zero)
                        return (TrustENoSignature, null);

                    try
                    {
                        var catalogInfo = new CatalogInfo
                        {
                            cbStruct = (uint)Marshal.SizeOf<CatalogInfo>(),
                            wszCatalogFile = string.Empty,
                        };
                        if (!CryptCatCatalogInfoFromContext(catalogContext, ref catalogInfo, 0)
                            || string.IsNullOrEmpty(catalogInfo.wszCatalogFile))
                            return (TrustENoSignature, null);

                        return VerifyAgainstCatalog(
                            path, hFile, catalogAdmin, catalogInfo.wszCatalogFile,
                            Convert.ToHexString(hash), hashBuffer, hashSize);
                    }
                    finally
                    {
                        CryptCatAdminReleaseCatalogContext(catalogAdmin, catalogContext, 0);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(hashBuffer);
                }
            }
            finally
            {
                CryptCatAdminReleaseContext(catalogAdmin, 0);
            }
        }
        catch (Exception ex)
        {
            // A file we cannot even open cannot be proven signed; report it as unsigned.
            Log.ZLogWarning(ex, $"Failed catalog verification for {path}");
            return (TrustENoSignature, null);
        }
    }

    private static (int Result, string? Signer) VerifyAgainstCatalog(
        string path,
        IntPtr hFile,
        IntPtr catalogAdmin,
        string catalogPath,
        string memberTag,
        IntPtr hashBuffer,
        uint hashSize)
    {
        var size = Marshal.SizeOf<WinTrustCatalogInfo>();
        var info = new WinTrustCatalogInfo
        {
            cbStruct = (uint)size,
            dwCatalogVersion = 0,
            pcwszCatalogFilePath = catalogPath,
            pcwszMemberTag = memberTag,
            pcwszMemberFilePath = path,
            hMemberFile = hFile,
            pbCalculatedFileHash = hashBuffer,
            cbCalculatedFileHash = hashSize,
            pcCatalogContext = IntPtr.Zero,
            hCatAdmin = catalogAdmin,
        };

        var block = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, block, false);
            try
            {
                return RunWinVerifyTrust(WtdChoiceCatalog, block);
            }
            finally
            {
                Marshal.DestroyStructure<WinTrustCatalogInfo>(block);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private static (int Result, string? Signer) RunWinVerifyTrust(uint unionChoice, IntPtr unionData)
    {
        var verified = RunWinVerifyTrustCore(unionChoice, unionData, WtdSaferFlag | WtdCacheOnlyUrlRetrieval);

        // WTD_SAFER_FLAG collapses every failure onto TRUST_E_NOSIGNATURE, which would make a tampered
        // binary indistinguishable from an unsigned one. Only then, re-run without it to recover the
        // real status code; a file that really is unsigned answers TRUST_E_NOSIGNATURE either way.
        if (verified.Result != TrustENoSignature)
            return verified;

        var retried = RunWinVerifyTrustCore(unionChoice, unionData, WtdCacheOnlyUrlRetrieval);
        return (retried.Result, retried.Signer ?? verified.Signer);
    }

    private static (int Result, string? Signer) RunWinVerifyTrustCore(
        uint unionChoice,
        IntPtr unionData,
        uint provFlags)
    {
        var action = GenericVerifyV2;
        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            pPolicyCallbackData = IntPtr.Zero,
            pSipClientData = IntPtr.Zero,
            dwUIChoice = WtdUiNone,
            // Revocation checks would hit the network once per file; a scan touches hundreds.
            fdwRevocationChecks = WtdRevokeNone,
            dwUnionChoice = unionChoice,
            pUnion = unionData,
            dwStateAction = WtdStateActionVerify,
            hWVTStateData = IntPtr.Zero,
            pwszUrlReference = IntPtr.Zero,
            dwProvFlags = provFlags,
            dwUIContext = 0,
            pSignatureSettings = IntPtr.Zero,
        };

        var result = WinVerifyTrust(InvalidHandleValue, ref action, ref data);

        string? signer = null;
        try
        {
            if (data.hWVTStateData != IntPtr.Zero)
                signer = ReadSignerName(data.hWVTStateData);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read the signer name from the trust state data");
        }
        finally
        {
            // Mandatory: leaking one state handle per file adds up fast over a full scan.
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(InvalidHandleValue, ref action, ref data);
        }

        return (result, signer);
    }

    private static string? ReadSignerName(IntPtr stateData)
    {
        var providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
            return null;

        var signer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signer == IntPtr.Zero)
            return null;

        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero)
            return null;

        // CRYPT_PROVIDER_CERT (x64): DWORD cbStruct @0, PCCERT_CONTEXT pCert @8.
        var certContext = Marshal.ReadIntPtr(providerCert, 8);
        if (certContext == IntPtr.Zero)
            return null;

        // CERT_CONTEXT (x64): DWORD dwCertEncodingType @0, BYTE* pbCertEncoded @8, DWORD cbCertEncoded @16.
        var encoded = Marshal.ReadIntPtr(certContext, 8);
        var encodedLength = Marshal.ReadInt32(certContext, 16);
        if (encoded == IntPtr.Zero || encodedLength <= 0)
            return null;

        // Copy the certificate out: the context dies with the WTD_STATEACTION_CLOSE below.
        var raw = new byte[encodedLength];
        Marshal.Copy(encoded, raw, 0, encodedLength);

        using var certificate = X509CertificateLoader.LoadCertificate(raw);
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static void SeekToStart(IntPtr hFile) => SetFilePointerEx(hFile, 0, IntPtr.Zero, FileBegin);

    #endregion

    #region Native constants

    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustENoSignature = unchecked((int)0x800B0100);

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    private const uint FileBegin = 0;

    private const string Sha256AlgorithmName = "SHA256";

    private static readonly IntPtr InvalidHandleValue = new(-1);

    // WINTRUST_ACTION_GENERIC_VERIFY_V2 {00AAC56B-CD44-11d0-8CC2-00C04FC295EE}
    private static readonly Guid GenericVerifyV2 =
        new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

    #endregion

    #region Native structures

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    /// <summary>Blittable so it can cross <see cref="WinVerifyTrust" /> by reference.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSipClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszUrlReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    #endregion

    #region Native methods

    // LibraryImport is not usable here: its generated marshalling stubs require AllowUnsafeBlocks,
    // which this project does not enable, so every native entry point below uses DllImport.

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WinTrustData pWvtData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr pProvData,
        uint idxSigner,
        [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner,
        uint idxCounterSigner);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminAcquireContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatAdminAcquireContext(
        out IntPtr phCatAdmin,
        IntPtr pgSubsystem,
        uint dwFlags);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminAcquireContext2", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatAdminAcquireContext2(
        out IntPtr phCatAdmin,
        IntPtr pgSubsystem,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwszHashAlgorithm,
        IntPtr pStrongHashPolicy,
        uint dwFlags);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminCalcHashFromFileHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatAdminCalcHashFromFileHandle(
        IntPtr hFile,
        ref uint pcbHash,
        IntPtr pbHash,
        uint dwFlags);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminCalcHashFromFileHandle2", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatAdminCalcHashFromFileHandle2(
        IntPtr hCatAdmin,
        IntPtr hFile,
        ref uint pcbHash,
        IntPtr pbHash,
        uint dwFlags);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminEnumCatalogFromHash", SetLastError = true)]
    private static extern IntPtr CryptCatAdminEnumCatalogFromHash(
        IntPtr hCatAdmin,
        IntPtr pbHash,
        uint cbHash,
        uint dwFlags,
        IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminReleaseCatalogContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatAdminReleaseCatalogContext(
        IntPtr hCatAdmin,
        IntPtr hCatInfo,
        uint dwFlags);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATAdminReleaseContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("wintrust.dll", EntryPoint = "CryptCATCatalogInfoFromContext", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCatCatalogInfoFromContext(
        IntPtr hCatInfo,
        ref CatalogInfo psCatInfo,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        IntPtr hFile,
        long liDistanceToMove,
        IntPtr lpNewFilePointer,
        uint dwMoveMethod);

    #endregion
}
