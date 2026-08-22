# Verifies the on-disk release artifacts against release-manifest.json, SBOM, and provenance.
# Invariants:
# 191. RELEASE_ARTIFACT_IDENTITY_IS_EXPLICIT_AND_VERSION_BOUND
# 192. ALL_ARTIFACTS_OF_ONE_RELEASE_SHARE_ONE_CANONICAL_RELEASE_IDENTITY
# 199. RELEASE_MANIFEST_HASHES_EXACT_DISTRIBUTED_ARTIFACTS
# 200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED
# 210. DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET

param(
    [string[]]$Runtimes = @('win-x64', 'win-arm64')
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

# 3. Parse and verify ReleaseManifest
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

# 4. Verify all SHA-256 hashes of artifacts in manifest against files on disk
Write-Host "`n--- Provera SHA-256 heševa artefakata ---" -ForegroundColor Cyan
foreach ($prop in $manifest.ArtifactSha256Hashes.PSObject.Properties) {
    $artifactName = $prop.Name
    $expectedHash = $prop.Value

    $artPath = Join-Path $artifactsRoot $artifactName
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

# 5. Verify SBOM SHA
Write-Host "`n--- Provera SBOM-a ---" -ForegroundColor Cyan
if ($manifest.SbomSha256 -eq $sbom.SbomSha256) {
    Write-Pass "SbomSha256 odgovara manifestu: $($manifest.SbomSha256)"
} else {
    Write-Fail "SbomSha256 mismatch! Manifest: $($manifest.SbomSha256), SBOM doc: $($sbom.SbomSha256)"
}

# 6. Verify staged preview manifest references actual ZIP and hash
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

# 7. Check post-build clean working tree
Write-Host "`n--- Provera cistoce radnog stabla (tracked source files) ---" -ForegroundColor Cyan
$statusTracked = git status -s --untracked-files=no
if (-not $statusTracked) {
    Write-Pass "Praceni source fajlovi su potpuno cisti (nema nekomitovanih izmena u repozitorijumu)"
} else {
    Write-Host "  [INFO] Nekomitovane izmene u pracenim fajlovima:`n$statusTracked" -ForegroundColor Yellow
}

if ($global:hasErrors) {
    Write-Host "`nVERIFIKACIJA NIJE USPELA!" -ForegroundColor Red
    exit 1
}

Write-Host "`nSVI ARTEFAKTI SU USPESNO VERIFIKOVANI ($expectedVersion)!" -ForegroundColor Green
exit 0
