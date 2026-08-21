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

        // 1. Open and verify StateRoot (Verify-only, StateRoot must exist)
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

            // 2. Open or create keys/ directory using crash-safe LinuxDirectoryProvisioner
            int keysFd;
            try
            {
                keysFd = LinuxDirectoryProvisioner.ProvisionOrVerifyDirectory(
                    _posix, rootFd, LinuxStoragePaths.KeysDirName, ownership, LinuxPosixStorageConstants.Mode0700);
            }
            catch (Exception ex)
            {
                throw new SigningIdentityUnavailableException($"Keys directory provisioning or verification failed: {ex.Message}", ex);
            }

            try
            {
                // 3. Check if evidence-signing-v1.p8 exists (authoritative check)
                int statRes = _posix.FstatAt(keysFd, LinuxStoragePaths.SigningKeyFileName, out _, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW);
                if (statRes == 0)
                {
                    // Existing key: VERIFY-ONLY (NO SILENT ROTATION!)
                    var identity = OpenAndVerifyExistingKey(keysFd, ownership, protection);
                    return Task.FromResult<IEvidenceSigningIdentity>(identity);
                }

                int errno = _posix.GetLastErrno();
                if (errno != LinuxPosixStorageConstants.ENOENT)
                {
                    throw new SigningIdentityUnavailableException(
                        $"Authoritative lookup of '{LinuxStoragePaths.SigningKeyFileName}' failed with errno {errno} (not ENOENT).");
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

                // R1-B & 8D-F: Re-establish keys directory durability before returning existing identity
                if (_posix.Fsync(keysFd) != 0)
                {
                    throw new SigningIdentityUnavailableException(
                        "Existing signing key was validated but keys directory durability could not be established.");
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
                    ecdsa.Dispose();
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

        try
        {
            pkcs8 = ecdsa.ExportPkcs8PrivateKey();

            var pubResult = LinuxAtomicFilePublisher.PublishAtomically(
                _posix,
                keysFd,
                LinuxStoragePaths.SigningKeyFileName,
                pkcs8,
                LinuxPosixStorageConstants.Mode0600,
                ownership,
                "key");

            if (pubResult.IsCollision)
            {
                ecdsa.Dispose();
                return OpenAndVerifyExistingKey(keysFd, ownership, protection);
            }

            if (pubResult.FinalFd >= 0)
            {
                _posix.Close(pubResult.FinalFd);
            }

            // Re-open and verify final published key to guarantee complete provenance
            ecdsa.Dispose();
            return OpenAndVerifyExistingKey(keysFd, ownership, protection);
        }
        catch (Exception ex) when (ex is not SigningIdentityUnavailableException)
        {
            ecdsa.Dispose();
            if (ex is InvalidOperationException && ex.Message.Contains("durability"))
            {
                throw new SigningIdentityUnavailableException(ex.Message, ex);
            }
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
