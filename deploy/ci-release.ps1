<# Runs only on the CI runner: package and verify before publishing a release. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
if (-not (Test-Path -LiteralPath "$PSScriptRoot\releases\$Version.md")) {
    throw "Release notes are missing for $Version."
}

& "$PSScriptRoot\release.ps1" -Version $Version -SkipPublish
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }

$output = Join-Path $root 'release'
$archives = @(Get-ChildItem -LiteralPath $output -File -Filter '*.zip')
if ($archives.Count -ne 8) { throw 'Expected eight release archives.' }

foreach ($archive in $archives) {
    $expected = ((Get-Content -LiteralPath ($archive.FullName + '.sha256') -Raw).Trim() -split '\s+')[0]
    if ((Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash -ne $expected) {
        throw "Checksum mismatch for $($archive.Name)."
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.Name -in @('fleet.json', 'miner.json', 'xmrig-api.token')) {
                throw "Private runtime configuration in $($archive.Name)."
            }
        }
    }
    finally { $zip.Dispose() }
}

# Alias packages must be byte-for-byte identical to the corresponding current name.
foreach ($archive in $archives | Where-Object Name -Like 'mining-fleet*') {
    $alias = Join-Path $output ($archive.Name -replace '^mining-fleet', 'xmrig-fleet')
    if ((Get-FileHash -LiteralPath $archive.FullName).Hash -ne (Get-FileHash -LiteralPath $alias).Hash) {
        throw "Legacy alias mismatch for $($archive.Name)."
    }

    $stage = Join-Path $output ('verify\' + $archive.BaseName)
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $stage
    $isAgent = $archive.Name -like 'mining-fleet-agent-*'
    $exeName = if ($isAgent) { 'mining-fleet-agent.exe' } else { 'mining-fleet.exe' }
    $exe = Join-Path $stage $exeName
    $number = $Version.TrimStart('v')
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    $fileVersion = [version]$actual
    if ($fileVersion.ToString(3) -ne $number -or $fileVersion.Revision -notin @(-1, 0)) {
        throw "Wrong binary version: $actual"
    }
    if ($isAgent) {
        $settings = Get-Content -LiteralPath (Join-Path $stage 'appsettings.json') -Raw | ConvertFrom-Json
        if ($settings.Agent.Token -ne 'CHANGE-ME') { throw 'Agent token template is not safe to ship.' }
    }
    else {
        $versionOutput = & $exe version
        if ($LASTEXITCODE -ne 0 -or ($versionOutput -join ' ') -notmatch [regex]::Escape($number)) {
            throw "Console version smoke test failed for $($archive.Name)."
        }
    }
    Write-Host "Verified $($archive.Name): $actual"
}
