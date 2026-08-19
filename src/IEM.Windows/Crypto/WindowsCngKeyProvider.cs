using System.Runtime.Versioning;
using System.Security.Cryptography;
using IEM.Evidence.Crypto;

namespace IEM.Windows.Crypto;

/// <summary>
/// Provisions and manages persistent Windows CNG signing keys per Invariants 21 &amp; 22.
/// <para>
/// 1. Tries TPM-backed (Microsoft Platform Crypto Provider) on initial provisioning.
/// 2. Falls back to Microsoft Software Key Storage Provider if TPM is unavailable during initial provisioning.
/// 3. Never silently rotates an existing key: if an existing key cannot be opened, throws <see cref="SigningIdentityUnavailableException"/>.
/// 4. Enforces non-exportable private keys (<see cref="CngExportPolicies.None"/>).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCngKeyProvider : IEvidenceKeyProvider
{
    public const string DefaultKeyName = "IEM_Evidence_Signing_Key_v1";

    private readonly string _keyName;
    private readonly CngKeyOpenOptions _openOptions;

    public WindowsCngKeyProvider(string keyName = DefaultKeyName, bool machineKey = true)
    {
        _keyName = keyName;
        _openOptions = machineKey ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.UserKey;
    }

    public Task<IEvidenceSigningIdentity> GetOrCreateIdentityAsync(CancellationToken ct = default)
    {
        // 1. Try to open existing TPM key
        if (CngKey.Exists(_keyName, CngProvider.MicrosoftPlatformCryptoProvider, _openOptions))
        {
            try
            {
                var tpmKey = CngKey.Open(_keyName, CngProvider.MicrosoftPlatformCryptoProvider, _openOptions);
                var protection = new KeyProtectionClaim(
                    KeyProtectionLevel.TpmBacked,
                    KeyProtectionEvidence.ProviderReported,
                    CngProvider.MicrosoftPlatformCryptoProvider.Provider);

                return Task.FromResult<IEvidenceSigningIdentity>(new WindowsCngSigningIdentity(tpmKey, protection));
            }
            catch (Exception ex)
            {
                throw new SigningIdentityUnavailableException(
                    $"Postojeći TPM ključ {_keyName} ne može biti otvoren. Automatska rotacija je zabranjena (Invarijanta 22).", ex);
            }
        }

        // 2. Try to open existing Software key
        if (CngKey.Exists(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, _openOptions))
        {
            try
            {
                var softwareKey = CngKey.Open(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, _openOptions);
                var protection = new KeyProtectionClaim(
                    KeyProtectionLevel.SoftwareProtected,
                    KeyProtectionEvidence.ProviderReported,
                    CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider);

                return Task.FromResult<IEvidenceSigningIdentity>(new WindowsCngSigningIdentity(softwareKey, protection));
            }
            catch (Exception ex)
            {
                throw new SigningIdentityUnavailableException(
                    $"Postojeći softverski CNG ključ {_keyName} ne može biti otvoren.", ex);
            }
        }

        // 3. First-run provisioning: Try TPM first
        try
        {
            var tpmParams = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftPlatformCryptoProvider,
                KeyCreationOptions = _openOptions == CngKeyOpenOptions.MachineKey
                    ? CngKeyCreationOptions.MachineKey
                    : CngKeyCreationOptions.None,
                ExportPolicy = CngExportPolicies.None,
            };

            var newTpmKey = CngKey.Create(CngAlgorithm.ECDsaP256, _keyName, tpmParams);
            var protection = new KeyProtectionClaim(
                KeyProtectionLevel.TpmBacked,
                KeyProtectionEvidence.ProviderReported,
                CngProvider.MicrosoftPlatformCryptoProvider.Provider);

            return Task.FromResult<IEvidenceSigningIdentity>(new WindowsCngSigningIdentity(newTpmKey, protection));
        }
        catch
        {
            // TPM not supported or available on this system; fallback to Software KSP on initial creation
        }

        // 4. Initial provisioning fallback: Microsoft Software Key Storage Provider
        try
        {
            var softwareParams = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = _openOptions == CngKeyOpenOptions.MachineKey
                    ? CngKeyCreationOptions.MachineKey
                    : CngKeyCreationOptions.None,
                ExportPolicy = CngExportPolicies.None,
            };

            var newSoftwareKey = CngKey.Create(CngAlgorithm.ECDsaP256, _keyName, softwareParams);
            var protection = new KeyProtectionClaim(
                KeyProtectionLevel.SoftwareProtected,
                KeyProtectionEvidence.ProviderReported,
                CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider);

            return Task.FromResult<IEvidenceSigningIdentity>(new WindowsCngSigningIdentity(newSoftwareKey, protection));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Neuspešno kreiranje signing ključa {_keyName} preko Software KSP: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Deletes the key from the provider storage (used exclusively for test cleanup).
    /// </summary>
    public void DeleteKeyForTesting()
    {
        if (CngKey.Exists(_keyName, CngProvider.MicrosoftPlatformCryptoProvider, _openOptions))
        {
            try
            {
                using var key = CngKey.Open(_keyName, CngProvider.MicrosoftPlatformCryptoProvider, _openOptions);
                key.Delete();
            }
            catch
            {
            }
        }

        if (CngKey.Exists(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, _openOptions))
        {
            try
            {
                using var key = CngKey.Open(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, _openOptions);
                key.Delete();
            }
            catch
            {
            }
        }
    }
}
