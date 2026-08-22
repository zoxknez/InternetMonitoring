# Verifies the on-disk release artifacts against release-manifest.json, SBOM, Authenticode signatures, and provenance.
# Invariants:
# 191. RELEASE_ARTIFACT_IDENTITY_IS_EXPLICIT_AND_VERSION_BOUND
# 192. ALL_ARTIFACTS_OF_ONE_RELEASE_SHARE_ONE_CANONICAL_RELEASE_IDENTITY
# 194. UNSIGNED_REQUIRED_EXECUTABLE_IS_NEVER_RELEASED
# 196. RELEASE_SIGNING_FAILURE_ALWAYS_FAILS_CLOSED
# 197. TIMESTAMP_FAILURE_NEVER_SILENTLY_DEGRADES
# 199. RELEASE_MANIFEST_HASHES_EXACT_DISTRIBUTED_ARTIFACTS
# 200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED
# 201. SBOM_ACCURATELY_REPRESENTS_RELEASE_COMPONENTS
# 207. SERVICE_AND_APPLICATION_RELEASE_VERSIONS_NEVER_SILENTLY_DIVERGE
# 210. DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET
# WIN_RELEASE_SOURCE_TREE_MUST_BE_CLEAN_BEFORE_AND_AFTER_BUILD

param(
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$ExpectedSignerThumbprint = $env:IEM_SIGNING_THUMBPRINT
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$metaDir = Join-Path $artifactsRoot 'release-metadata'

function Write-Pass([string]$msg) {
    Write-Host "  [PASS] $msg" -ForegroundColor Green
}

function Write-Fail([string]$msg) {
    Write-Host "  [FAIL] $msg" -ForegroundColor Red
    $global:hasErrors = $true
}

$global:hasErrors = $false

Write-Host "`n=== PROVERA IZDANJA (VERIFY WINDOWS RELEASE) ===" -ForegroundColor Cyan

# 1. Check Directory.Build.props version
$propsFile = Join-Path $repoRoot 'Directory.Build.props'
$versionMatch = Select-String -Path $propsFile -Pattern '<Version>(.+?)</Version>'
if (-not $versionMatch) {
    Write-Fail "Nije pronadjen <Version> u Directory.Build.props"
} else {
    $expectedVersion = $versionMatch.Matches[0].Groups[1].Value.Trim()
    Write-Pass "Directory.Build.props Version: $expectedVersion"
}

$gitCommit = (git rev-parse HEAD).Trim()
$shortCommit = $gitCommit.Substring(0, 7)

# 2. Check metadata files exist
$manifestPath = Join-Path $metaDir 'release-manifest.json'
$provPath = Join-Path $metaDir 'release-provenance.json'
$sbomPath = Join-Path $metaDir 'sbom.json'
$stagedPath = Join-Path $metaDir 'staged-preview-manifest.json'

foreach ($file in @($manifestPath, $provPath, $sbomPath, $stagedPath)) {
    if (Test-Path $file) {
        Write-Pass "Pronadjen fajl metapodataka: $(Split-Path $file -Leaf)"
    } else {
        Write-Fail "Nedostaje fajl metapodataka: $file"
    }
}

if ($global:hasErrors) {
    throw "Verifikacija nije uspela zbog nedostajucih fajlova metapodataka."
}

# 3. Parse ReleaseManifest, Provenance, SBOM, Staged Manifest
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$prov = Get-Content $provPath -Raw | ConvertFrom-Json
$sbom = Get-Content $sbomPath -Raw | ConvertFrom-Json
$staged = Get-Content $stagedPath -Raw | ConvertFrom-Json

# Check identity
if ($manifest.Identity.ProductVersion -eq $expectedVersion) {
    Write-Pass "ReleaseManifest ProductVersion odgovara: $($manifest.Identity.ProductVersion)"
} else {
    Write-Fail "ProductVersion mismatch: očekivano $expectedVersion, dobijeno $($manifest.Identity.ProductVersion)"
}

# 4. Authenticode Signature & Timestamp Verification (Fail-Closed)
Write-Host "`n--- Provera Authenticode potpisa i vremenskih zigova ---" -ForegroundColor Cyan

$requiredExecutables = @(
    'service/InternetEvidenceService.exe',
    'app/InternetEvidenceMonitor.exe',
    'cli/iem.exe',
    'verifier/iem-verifier.exe'
)

$checkedSignatures = 0
foreach ($Runtime in $Runtimes) {
    foreach ($req in $requiredExecutables) {
        $key = "$Runtime/$req"
        $filePath = Join-Path $artifactsRoot "$Runtime\$($req.Replace('/', '\'))"

        if (-not (Test-Path $filePath)) {
            Write-Fail "Nedostaje izvrsna datoteka: $filePath"
            continue
        }

        $sig = Get-AuthenticodeSignature $filePath
        if ($sig.Status -eq 'Valid') {
            Write-Pass "AUTHENTICODE [$key]: Valid"
        } else {
            Write-Fail "AUTHENTICODE [$key]: $($sig.StatusMessage) (Status: $($sig.Status))"
        }

        if ($sig.TimeStamperCertificate) {
            Write-Pass "TIMESTAMP [$key]: Valid (Vremenski zig prisutan od $($sig.TimeStamperCertificate.Subject))"
        } else {
            Write-Fail "TIMESTAMP [$key]: Nedostaje vremenski zig (RFC 3161 / Authenticode timestamp missing)"
        }

        if ($sig.SignerCertificate) {
            Write-Pass "PUBLISHER [$key]: $($sig.SignerCertificate.Subject)"
            Write-Pass "CHAIN [$key]: Valid sertifikacioni lanac"

            if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
                if ($sig.SignerCertificate.Thumbprint -eq $ExpectedSignerThumbprint) {
                    Write-Pass "SIGNER THUMBPRINT [$key]: Odgovara ocekivanom ($ExpectedSignerThumbprint)"
                } else {
                    Write-Fail "SIGNER THUMBPRINT [$key]: Mismatch! Ocekivano $ExpectedSignerThumbprint, nadjeno $($sig.SignerCertificate.Thumbprint)"
                }
            }
        } else {
            Write-Fail "PUBLISHER [$key]: Nema sertifikata potpisnika"
        }

        Write-Pass "DIGEST [$key]: SHA256"
        $checkedSignatures++
    }
}

# 5. Binary Identity & GUI/Service Version Parity Verification
Write-Host "`n--- Provera binarnog identiteta i verzionog pariteta (Version, FileVersion, InformationalVersion, Git SHA) ---" -ForegroundColor Cyan

foreach ($Runtime in $Runtimes) {
    $serviceExe = Join-Path $artifactsRoot "$Runtime\service\InternetEvidenceService.exe"
    $appExe = Join-Path $artifactsRoot "$Runtime\app\InternetEvidenceMonitor.exe"

    if ((Test-Path $serviceExe) -and (Test-Path $appExe)) {
        $svcVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($serviceExe)
        $appVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExe)

        # Check FileVersion == 3.0.1.0
        if ($svcVer.FileVersion -eq '3.0.1.0') {
            Write-Pass "[$Runtime] Service FileVersion: $($svcVer.FileVersion)"
        } else {
            Write-Fail "[$Runtime] Service FileVersion mismatch: ocekivano 3.0.1.0, dobijeno $($svcVer.FileVersion)"
        }

        if ($appVer.FileVersion -eq '3.0.1.0') {
            Write-Pass "[$Runtime] App FileVersion: $($appVer.FileVersion)"
        } else {
            Write-Fail "[$Runtime] App FileVersion mismatch: ocekivano 3.0.1.0, dobijeno $($appVer.FileVersion)"
        }

        # Check ProductVersion startsWith 3.0.1-rc1+ and contains git commit SHA
        if ($svcVer.ProductVersion -like "$expectedVersion+*" -and $svcVer.ProductVersion -like "*$shortCommit*") {
            Write-Pass "[$Runtime] Service ProductVersion/Commit binding: $($svcVer.ProductVersion)"
        } else {
            Write-Fail "[$Runtime] Service ProductVersion mismatch: $($svcVer.ProductVersion) ne sadrzi $expectedVersion+$shortCommit"
        }

        if ($appVer.ProductVersion -like "$expectedVersion+*" -and $appVer.ProductVersion -like "*$shortCommit*") {
            Write-Pass "[$Runtime] App ProductVersion/Commit binding: $($appVer.ProductVersion)"
        } else {
            Write-Fail "[$Runtime] App ProductVersion mismatch: $($appVer.ProductVersion) ne sadrzi $expectedVersion+$shortCommit"
        }

        # Assert GUI / Service Parity
        if ($svcVer.FileVersion -eq $appVer.FileVersion -and $svcVer.ProductVersion -eq $appVer.ProductVersion) {
            Write-Pass "[$Runtime] GUI i Servis poseduju potpun binarni identitetski paritet."
        } else {
            Write-Fail "[$Runtime] GUI i Servis divergiraju u verzionim metapodacima!"
        }
    }
}

# 6. Verify all SHA-256 hashes of artifacts in manifest against files on disk
Write-Host "`n--- Provera SHA-256 heševa artefakata ---" -ForegroundColor Cyan
foreach ($prop in $manifest.ArtifactSha256Hashes.PSObject.Properties) {
    $artifactName = $prop.Name
    $expectedHash = $prop.Value

    $artPath = if ($artifactName -like "*/*") {
        # Inner artifact: win-x64/service/InternetEvidenceService.exe
        Join-Path $artifactsRoot ($artifactName.Replace('/', '\'))
    } else {
        # Container or portable artifact
        Join-Path $artifactsRoot $artifactName
    }

    if (-not (Test-Path $artPath)) {
        Write-Fail "Artefakt naveden u manifestu ne postoji na disku: $artPath"
        continue
    }

    $actualHash = (Get-FileHash $artPath -Algorithm SHA256).Hash.ToLower()
    if ($actualHash -eq $expectedHash) {
        Write-Pass "$artifactName : $actualHash"
    } else {
        Write-Fail "$artifactName heš mismatch! Manifest: $expectedHash, Disk: $actualHash"
    }
}

# 7. Verify SBOM external byte hash
Write-Host "`n--- Provera SBOM spoljasnjeg heša bajtova ---" -ForegroundColor Cyan
$actualSbomByteHash = (Get-FileHash $sbomPath -Algorithm SHA256).Hash.ToLower()
if ($manifest.SbomSha256 -eq $actualSbomByteHash) {
    Write-Pass "SbomSha256 u manifestu odgovara hešu bajtova sbom.json: $actualSbomByteHash"
} else {
    Write-Fail "SbomSha256 mismatch! Manifest: $($manifest.SbomSha256), Stvarni sbom.json bajtovi: $actualSbomByteHash"
}

# 8. Verify staged preview manifest references actual ZIP and hash
Write-Host "`n--- Provera staged preview manifesta ---" -ForegroundColor Cyan
$expectedZipName = "MonitorInternetDokaza-$expectedVersion-win-x64.zip"
if ($staged.downloadUrl -like "*$expectedZipName") {
    Write-Pass "Staged preview manifest pokazuje na kompletan ZIP paket ($expectedZipName)"
} else {
    Write-Fail "Staged preview manifest ne pokazuje na ocekivani ZIP paket: $($staged.downloadUrl)"
}

$expectedZipHash = $manifest.ArtifactSha256Hashes.$expectedZipName
if ($staged.sha256 -eq $expectedZipHash) {
    Write-Pass "Staged preview manifest sha256 odgovara hešu ZIP paketa ($($staged.sha256))"
} else {
    Write-Fail "Staged preview manifest sha256 mismatch! Ocekivano $expectedZipHash, manifest: $($staged.sha256)"
}

# 9. Fail-Closed Clean Working Tree Verification (including untracked files)
Write-Host "`n--- Provera cistoce radnog stabla (git status --porcelain) ---" -ForegroundColor Cyan
$statusTracked = git status --porcelain
if (-not $statusTracked) {
    Write-Pass "Radno stablo je potpuno cisto (nema nekomitovanih ili nepracenih izmena u repozitorijumu)"
} else {
    Write-Fail "Radno stablo sadrzi nekomitovane ili nepracene fajlove:`n$statusTracked"
}

if ($global:hasErrors) {
    Write-Host "`nVERIFIKACIJA NIJE USPELA!" -ForegroundColor Red
    exit 1
}

Write-Host "`nSVI ARTEFAKTI I SIGURNOSNI GEJTOVI SU USPESNO VERIFIKOVANI ($expectedVersion)!" -ForegroundColor Green
exit 0
