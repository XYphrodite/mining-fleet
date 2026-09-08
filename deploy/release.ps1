<#
.SYNOPSIS
    Builds, packages and publishes a mining-fleet release.

.DESCRIPTION
    Produces the assets that `deploy\install.ps1` and `xmrig-fleet update` look for:

        xmrig-fleet-win-x64.zip         operator console, self-contained
        xmrig-fleet-agent-win-x64.zip   node agent, self-contained

    The version comes from the tag: -Version v1.1.0 stamps 1.1.0 into both binaries, so the
    console can compare the release tag against its own assembly version.

.EXAMPLE
    .\release.ps1 -Version v1.1.0 -Notes 'Per-node electricity tariff.'
    .\release.ps1 -Version v1.1.0 -SkipPublish      # build the zips only
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes = '',

    [string]$Runtime = 'win-x64',

    [string]$OutputPath = "$PSScriptRoot\..\release",

    # Build the archives but do not create the GitHub release.
    [switch]$SkipPublish,

    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$number = $Version.TrimStart('v')

# A running agent or console locks its own executable and fails the build. Only processes
# started out of this repository can lock the build output, so an installed agent service
# on this machine is deliberately left alone: killing it would stop a production node.
foreach ($name in 'xmrig-fleet-agent', 'xmrig-fleet') {
    Get-Process $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Write-Host "Stopping $name (pid $($_.Id)) from the working tree so its files can be replaced" -ForegroundColor Yellow
            Stop-Process -Id $_.Id -Force
        }
}

if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

$targets = @(
    @{ Name = 'xmrig-fleet';       Project = 'src\MiningFleet.Console'; Asset = "xmrig-fleet-$Runtime.zip" }
    @{ Name = 'xmrig-fleet-agent'; Project = 'src\MiningFleet.Agent';   Asset = "xmrig-fleet-agent-$Runtime.zip" }
)

foreach ($t in $targets) {
    $stage = Join-Path $OutputPath $t.Name
    Write-Host "==> Publishing $($t.Name) $number ($Runtime)" -ForegroundColor Cyan

    & dotnet publish (Join-Path $root $t.Project) `
        -c Release -r $Runtime --self-contained true `
        -p:Version=$number -p:AssemblyVersion=$number -p:FileVersion=$number `
        -o $stage
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($t.Name)" }

    # Debug symbols are useful locally but only bloat what every operator downloads.
    Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

    # appsettings.json ships as a template; a real token is written by install-agent.ps1.
    $archive = Join-Path $OutputPath $t.Asset
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
    Remove-Item $stage -Recurse -Force

    $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "    $($t.Asset)  $size MB"
}

if ($SkipPublish) {
    Write-Host ''
    Write-Host "Archives are in $OutputPath. Publish them with:" -ForegroundColor Cyan
    Write-Host "  gh release create $Version $OutputPath\*.zip --title $Version --notes '...'"
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'The GitHub CLI (gh) is required to publish. Re-run with -SkipPublish and upload by hand.'
}

Write-Host "==> Creating release $Version" -ForegroundColor Cyan
$assets = (Get-ChildItem $OutputPath -Filter *.zip).FullName
$ghArgs = @('release', 'create', $Version) + $assets + @('--title', $Version, '--notes', $Notes)
if ($Draft) { $ghArgs += '--draft' }

& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }

Write-Host ''
Write-Host "Published $Version." -ForegroundColor Green
Write-Host 'Operators can now run:  xmrig-fleet update'
