using System.Security.Cryptography;
using IEM.Evidence.Crypto;
using IEM.Linux.Storage;

namespace IEM.Linux.Crypto;

/// <summary>
/// Supplies or provisions persistent ECDSA P-256 Linux signing identities.
/// Invariants:
/// 21. CRYPTOGRAPHIC_IDENTITY_PERSISTENCE
/// 22. SIGNING_IDENTITY_NEVER_SILENTLY_ROTATES_ON_FAILURE
/// 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR
/// </summary>
public sealed class LinuxEvidenceKeyProvider : IEvidenceKeyProvider
{
    public const int MaxKeyFileSizeBytes = 16384;
    public const string KeyStoreProviderName = "LinuxFileSystemKeyStore";

    private readonly LinuxSigningIdentityScope _scope;
    private readonly string? _customStateRoot;
    private readonly ILinuxPosixStorageApi _posix;
    private readonly LinuxStorageOwnershipPolicy _ownershipPolicy;
    private readonly Func<string, string?> _getEnv;

    public LinuxEvidenceKeyProvider(
        LinuxSigningIdentityScope scope = LinuxSigningIdentityScope.SystemInstallation,
        string? customStateRoot = null,
        ILinuxPosixStorageApi? posix = null,
        LinuxStorageOwnershipPolicy? ownershipPolicy = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _scope = scope;
        _customStateRoot = customStateRoot;
        _posix = posix ?? new LinuxNativePosixStorageApi();
        _ownershipPolicy = ownershipPolicy ?? LinuxStorageOwnershipPolicy.SystemDefault;
        _getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public Task<IEvidenceSigningIdentity> GetOrCreateIdentityAsync(CancellationToken ct = default)
    {
        var (stateRoot, ownership) = ResolveStateRootAndOwnership();
        var normStateRoot = NormalizePath(stateRoot);

        var protection = new KeyProtectionClaim(
            KeyProtectionLevel.SoftwareProtected,
            KeyProtectionEvidence.ProviderReported,
            KeyStoreProviderName,
            _scope == LinuxSigningIdentityScope.SystemInstallation
                ? "POSIX:0700/0600:exact-ownership:openat2:system-daemon"
                : "POSIX:0700/0600:exact-ownership:openat2:user-portable");

        // 1. Open and verify StateRoot (Verify-only for both System and Portable, R1-F)
        int rootFd = _posix.Open(normStateRoot, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_NOFOLLOW | LinuxPosixStorageConstants.O_CLOEXEC, 0);
        if (rootFd < 0)
        {
            throw new SigningIdentityUnavailableException($"StateRoot '{normStateRoot}' does not exist or cannot be opened.");
        }

        try
        {
            if (_posix.Fstat(rootFd, out var rootStat) != 0 || !rootStat.IsDirectory)
            {
                throw new SigningIdentityUnavailableException($"StateRoot '{normStateRoot}' is not a valid directory.");
            }

            if ((rootStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
            {
                throw new SigningIdentityUnavailableException($"StateRoot '{normStateRoot}' permissions 0{Convert.ToString(rootStat.PermissionBits, 8)} are invalid (must be 0700).");
            }

            if (!CheckOwnership(rootStat, ownership, out var rootOwnerErr))
            {
                throw new SigningIdentityUnavailableException($"StateRoot '{normStateRoot}' {rootOwnerErr}");
            }

            // 2. Open or create keys/ directory
            bool keysExisted = _posix.FstatAt(rootFd, LinuxStoragePaths.KeysDirName, out var keysStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0;
            if (!keysExisted)
            {
                if (_posix.MkdirAt(rootFd, LinuxStoragePaths.KeysDirName, LinuxPosixStorageConstants.Mode0700) != 0)
                {
                    throw new SigningIdentityUnavailableException($"Failed to create keys directory under '{normStateRoot}'.");
                }
            }

            var keysHow = new OpenHow
            {
                Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC,
                Mode = 0,
                Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
            };

            int keysFd = _posix.OpenAt2(rootFd, LinuxStoragePaths.KeysDirName, ref keysHow);
            if (keysFd < 0)
            {
                throw new SigningIdentityUnavailableException("Failed to open keys directory securely via openat2.");
            }

            try
            {
                if (_posix.Fstat(keysFd, out var kStat) != 0 || !kStat.IsDirectory)
                {
                    throw new SigningIdentityUnavailableException("Keys directory is not a valid directory descriptor.");
                }

                if ((kStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                {
                    throw new SigningIdentityUnavailableException($"Keys directory permissions 0{Convert.ToString(kStat.PermissionBits, 8)} are invalid (must be 0700).");
                }

                if (!CheckOwnership(kStat, ownership, out var keysOwnerErr))
                {
                    throw new SigningIdentityUnavailableException($"Keys directory {keysOwnerErr}");
                }

                if (!keysExisted)
                {
                    _posix.Fchmod(keysFd, LinuxPosixStorageConstants.Mode0700);
                }

                // 3. Check if evidence-signing-v1.p8 exists
                bool keyFileExisted = _posix.FstatAt(keysFd, LinuxStoragePaths.SigningKeyFileName, out _, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0;
                if (keyFileExisted)
                {
                    // Existing key: VERIFY-ONLY (NO SILENT ROTATION!)
                    var identity = OpenAndVerifyExistingKey(keysFd, ownership, protection);
                    return Task.FromResult<IEvidenceSigningIdentity>(identity);
                }

                // 4. Key does not exist: Provision new ECDSA P-256 identity atomically
                var newIdentity = ProvisionNewKeyAtomically(keysFd, ownership, protection);
                return Task.FromResult<IEvidenceSigningIdentity>(newIdentity);
            }
            finally
            {
                _posix.Close(keysFd);
            }
        }
        finally
        {
            _posix.Close(rootFd);
        }
    }

    private LinuxEvidenceSigningIdentity OpenAndVerifyExistingKey(
        int keysFd,
        LinuxStorageOwnershipPolicy ownership,
        KeyProtectionClaim protection)
    {
        var keyHow = new OpenHow
        {
            Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC,
            Mode = 0,
            Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
        };

        int keyFd = _posix.OpenAt2(keysFd, LinuxStoragePaths.SigningKeyFileName, ref keyHow);
        if (keyFd < 0)
        {
            throw new SigningIdentityUnavailableException($"Failed to open existing key file '{LinuxStoragePaths.SigningKeyFileName}' via openat2.");
        }

        byte[]? buffer = null;
        try
        {
            if (_posix.Fstat(keyFd, out var keyStat) != 0 || !keyStat.IsRegularFile)
            {
                throw new SigningIdentityUnavailableException($"Key file '{LinuxStoragePaths.SigningKeyFileName}' is not a regular file.");
            }

            if ((keyStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0600)
            {
                throw new SigningIdentityUnavailableException(
                    $"Key file '{LinuxStoragePaths.SigningKeyFileName}' permissions 0{Convert.ToString(keyStat.PermissionBits, 8)} are invalid (must be exact 0600).");
            }

            if (!CheckOwnership(keyStat, ownership, out var keyOwnerErr))
            {
                throw new SigningIdentityUnavailableException($"Key file '{LinuxStoragePaths.SigningKeyFileName}' {keyOwnerErr}");
            }

            if (keyStat.Size <= 0 || keyStat.Size > MaxKeyFileSizeBytes)
            {
                throw new SigningIdentityUnavailableException(
                    $"Key file '{LinuxStoragePaths.SigningKeyFileName}' has invalid size ({keyStat.Size} bytes).");
            }

            buffer = new byte[(int)keyStat.Size];
            if (!ReadExactly(keyFd, buffer))
            {
                throw new SigningIdentityUnavailableException($"Failed to read full key bytes from '{LinuxStoragePaths.SigningKeyFileName}'.");
            }

            var ecdsa = ECDsa.Create();
            bool success = false;
            try
            {
                ecdsa.ImportPkcs8PrivateKey(buffer, out int bytesRead);
                if (bytesRead != buffer.Length)
                {
                    throw new SigningIdentityUnavailableException("Trailing data found after PKCS#8 key payload.");
                }

                // Verify curve is NIST P-256 (OID 1.2.840.10045.3.1.7)
                var p = ecdsa.ExportParameters(false);
                if (p.Curve.Oid?.Value != "1.2.840.10045.3.1.7" && p.Curve.Oid?.FriendlyName != "nistP256")
                {
                    throw new SigningIdentityUnavailableException($"Key curve '{p.Curve.Oid?.FriendlyName}' is not supported (must be NIST P-256).");
                }

                // Self-test sign and verify
                byte[] testHash = SHA256.HashData("IEM_KEY_SELF_TEST"u8);
                byte[] testSig = ecdsa.SignHash(testHash, DSASignatureFormat.Rfc3279DerSequence);
                if (!ecdsa.VerifyHash(testHash, testSig, DSASignatureFormat.Rfc3279DerSequence))
                {
                    throw new SigningIdentityUnavailableException("ECDSA P-256 self-test sign/verify failed.");
                }

                success = true;
                return new LinuxEvidenceSigningIdentity(ecdsa, protection, _scope);
            }
            catch (Exception ex) when (ex is not SigningIdentityUnavailableException)
            {
                throw new SigningIdentityUnavailableException($"Failed to import or validate PKCS#8 ECDSA key: {ex.Message}", ex);
            }
            finally
            {
                if (!success)
                {
                    ecdsa.Dispose(); // R1-E: Dispose on EVERY validation failure
                }
            }
        }
        finally
        {
            _posix.Close(keyFd);
            if (buffer != null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private LinuxEvidenceSigningIdentity ProvisionNewKeyAtomically(
        int keysFd,
        LinuxStorageOwnershipPolicy ownership,
        KeyProtectionClaim protection)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[]? pkcs8 = null;
        var tempName = $".tmp.key.{Guid.NewGuid():N}.p8";

        try
        {
            pkcs8 = ecdsa.ExportPkcs8PrivateKey();

            var tempHow = new OpenHow
            {
                Flags = (ulong)(LinuxPosixStorageConstants.O_CREAT | LinuxPosixStorageConstants.O_EXCL | LinuxPosixStorageConstants.O_RDWR | LinuxPosixStorageConstants.O_CLOEXEC),
                Mode = (ulong)LinuxPosixStorageConstants.Mode0600,
                Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
            };

            int tempFd = _posix.OpenAt2(keysFd, tempName, ref tempHow);
            if (tempFd < 0)
            {
                throw new InvalidOperationException("Failed to create temporary key file via openat2.");
            }

            try
            {
                if (!WriteAll(tempFd, pkcs8))
                {
                    throw new InvalidOperationException("Failed to write full PKCS#8 key bytes to temporary file.");
                }

                // R1-B: Validate new temp key before publication
                if (_posix.Fchmod(tempFd, LinuxPosixStorageConstants.Mode0600) != 0)
                {
                    throw new InvalidOperationException("Failed to set 0600 permissions on temporary key file.");
                }

                if (_posix.Fstat(tempFd, out var tempStat) != 0 || !tempStat.IsRegularFile)
                {
                    throw new InvalidOperationException("Temporary key file is not a valid regular file descriptor.");
                }

                if ((tempStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0600)
                {
                    throw new InvalidOperationException(
                        $"Temporary key file permissions 0{Convert.ToString(tempStat.PermissionBits, 8)} are invalid (must be exact 0600).");
                }

                if (!CheckOwnership(tempStat, ownership, out var tempOwnerErr))
                {
                    throw new InvalidOperationException($"Temporary key file {tempOwnerErr}");
                }

                if (tempStat.Size != pkcs8.Length)
                {
                    throw new InvalidOperationException(
                        $"Temporary key file size {tempStat.Size} does not match expected length {pkcs8.Length}.");
                }

                if (_posix.Fsync(tempFd) != 0)
                {
                    throw new InvalidOperationException("Failed to fsync temporary key file.");
                }
            }
            finally
            {
                _posix.Close(tempFd);
            }

            // Atomic publish with RENAME_NOREPLACE (R1-C: preserve errno truth)
            int ren = _posix.RenameAt2(keysFd, tempName, keysFd, LinuxStoragePaths.SigningKeyFileName, LinuxPosixStorageConstants.RENAME_NOREPLACE);
            if (ren != 0)
            {
                int errno = _posix.GetLastErrno();
                _posix.UnlinkAt(keysFd, tempName, 0);

                // If and ONLY if EEXIST, treat as valid publication race winner
                if (errno == LinuxPosixStorageConstants.EEXIST)
                {
                    if (_posix.FstatAt(keysFd, LinuxStoragePaths.SigningKeyFileName, out _, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0)
                    {
                        ecdsa.Dispose();
                        return OpenAndVerifyExistingKey(keysFd, ownership, protection);
                    }
                }

                // Every other errno (EIO, ENOSPC, EROFS, EINVAL, ENOSYS) fails closed
                throw new InvalidOperationException($"renameat2 failed to publish signing key (errno {errno}).");
            }

            // R1-A: Check parent-directory fsync result for durability
            if (_posix.Fsync(keysFd) != 0)
            {
                throw new SigningIdentityUnavailableException(
                    "Signing key was published but directory durability could not be established.");
            }

            // Re-open and verify final published key to guarantee complete provenance
            ecdsa.Dispose();
            return OpenAndVerifyExistingKey(keysFd, ownership, protection);
        }
        catch
        {
            _posix.UnlinkAt(keysFd, tempName, 0);
            ecdsa.Dispose();
            throw;
        }
        finally
        {
            if (pkcs8 != null)
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
    }

    private (string stateRoot, LinuxStorageOwnershipPolicy ownership) ResolveStateRootAndOwnership()
    {
        if (_scope == LinuxSigningIdentityScope.SystemInstallation)
        {
            var stateRoot = _customStateRoot ?? LinuxStoragePaths.DefaultSystemStateRoot;
            var policy = _ownershipPolicy.EnforceExactOwnership
                ? _ownershipPolicy
                : LinuxStorageOwnershipPolicy.CreateSystem(_posix.GetEuid(), _posix.GetEgid());

            return (stateRoot, policy);
        }
        else
        {
            var stateRoot = _customStateRoot ?? LinuxStoragePaths.TryResolvePortableStateRoot(_getEnv);
            if (string.IsNullOrWhiteSpace(stateRoot))
            {
                throw new SigningIdentityUnavailableException("XDG_STATE_HOME and HOME are unavailable for portable signing identity.");
            }

            var policy = LinuxStorageOwnershipPolicy.CreatePortable(_posix.GetEuid(), _posix.GetEgid());
            return (stateRoot, policy);
        }
    }

    private bool WriteAll(int fd, ReadOnlySpan<byte> data)
    {
        int total = 0;
        while (total < data.Length)
        {
            int n = _posix.Write(fd, data.Slice(total));
            if (n <= 0)
            {
                return false;
            }
            total += n;
        }
        return true;
    }

    private bool ReadExactly(int fd, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _posix.Read(fd, buffer.Slice(total));
            if (n <= 0)
            {
                return false;
            }
            total += n;
        }
        return true;
    }

    private static bool CheckOwnership(PosixStat stat, LinuxStorageOwnershipPolicy policy, out string? errorMessage)
    {
        if (policy.EnforceExactOwnership)
        {
            if (policy.ExpectedUid.HasValue && stat.Uid != policy.ExpectedUid.Value)
            {
                errorMessage = $"UID mismatch: found {stat.Uid}, expected {policy.ExpectedUid.Value}.";
                return false;
            }
            if (policy.ExpectedGid.HasValue && stat.Gid != policy.ExpectedGid.Value)
            {
                errorMessage = $"GID mismatch: found {stat.Gid}, expected {policy.ExpectedGid.Value}.";
                return false;
            }
        }
        errorMessage = null;
        return true;
    }

    private static string NormalizePath(string path)
    {
        var norm = path.Replace('\\', '/').TrimEnd('/');
        if (norm.Length >= 2 && norm[1] == ':' && char.IsLetter(norm[0]))
        {
            norm = norm.Substring(2);
        }
        return norm;
    }
}
