# Thin wrapper invoking the canonical release build engine (build/publish.ps1).
# Invariant: WIN_RELEASE_SINGLE_CANONICAL_PUBLISH_PATH

param(
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$Configuration = 'Release',
    [string]$SigningThumbprint = $env:IEM_SIGNING_THUMBPRINT
)

$ErrorActionPreference = 'Stop'

$canonicalScript = Join-Path $PSScriptRoot '..\build\publish.ps1'
& $canonicalScript -Runtimes $Runtimes -Configuration $Configuration -SigningThumbprint $SigningThumbprint
exit $LASTEXITCODE
