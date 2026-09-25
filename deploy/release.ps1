<#
.SYNOPSIS
    Builds, packages and publishes a mining-fleet release.

.DESCRIPTION
    Produces the assets that `deploy\install.ps1` and `mining-fleet update` look for.
    Each payload is published under both the new name and the xmrig-fleet alias so a
    console or agent that has not moved yet can still find its zip, and in two variants:
    self-contained (full) plus framework-dependent (light, needs the .NET runtime):

        mining-fleet-win-x64.zip / xmrig-fleet-win-x64.zip
        mining-fleet-win-x64-light.zip / xmrig-fleet-win-x64-light.zip
        mining-fleet-agent-win-x64.zip / xmrig-fleet-agent-win-x64.zip
        mining-fleet-agent-win-x64-light.zip / xmrig-fleet-agent-win-x64-light.zip

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
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
# Recursive cleanup must stay in a dedicated output folder, never the checkout or its sources.
$allowedRoots = @((Join-Path $root 'release'), (Join-Path $root 'publish'))
if (-not ($allowedRoots | Where-Object {
    $OutputPath.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
    $OutputPath.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
})) { throw 'OutputPath must be inside the repository release or publish directory.' }

# A running agent or console locks its own executable and fails the build. Only processes
# started out of this repository can lock the build output, so an installed agent service
# on this machine is deliberately left alone: killing it would stop a production node.
foreach ($name in 'mining-fleet-agent', 'xmrig-fleet-agent', 'mining-fleet', 'xmrig-fleet') {
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
    @{
        Name = 'mining-fleet'
        Project = 'src\MiningFleet.Console'
        Assets = @("mining-fleet-$Runtime.zip", "xmrig-fleet-$Runtime.zip")
        ShimFrom = 'mining-fleet.exe'
        ShimTo = 'xmrig-fleet.exe'
    }
    @{
        Name = 'mining-fleet-agent'
        Project = 'src\MiningFleet.Agent'
        Assets = @("mining-fleet-agent-$Runtime.zip", "xmrig-fleet-agent-$Runtime.zip")
        ShimFrom = 'mining-fleet-agent.exe'
        ShimTo = 'xmrig-fleet-agent.exe'
    }
)

foreach ($t in $targets) {
    # Two builds of the same sources: self-contained (full) and framework-dependent
    # (light, needs the .NET runtime on the machine). PublishTrimmed=false on the
    # light build: a trimmed framework-dependent single-file publish fails the build
    # (NETSDK1102), and the runtime is shared anyway so trimming saves nothing.
    $variants = @(
        @{ Suffix = ''; SelfContained = 'true'; ExtraArgs = @() }
        @{ Suffix = '-light'; SelfContained = 'false'; ExtraArgs = @('-p:PublishTrimmed=false') }
    )

    foreach ($v in $variants) {
        $stage = Join-Path $OutputPath ($t.Name + $v.Suffix)
        # Keep full/light intermediate outputs apart: a reused single-file bundle can
        # otherwise retain the runtime from the preceding self-contained publish.
        $artifacts = Join-Path $OutputPath ('.build\' + $t.Name + $v.Suffix)
        Write-Host "==> Publishing $($t.Name)$($v.Suffix) $number ($Runtime)" -ForegroundColor Cyan

        & dotnet publish (Join-Path $root $t.Project) `
            -c Release -r $Runtime "-p:SelfContained=$($v.SelfContained)" `
            --artifacts-path $artifacts `
            -m:1 -p:UseSharedCompilation=false `
            -p:Version=$number -p:AssemblyVersion=$number -p:FileVersion=$number `
            @($v.ExtraArgs) `
            -o $stage
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $($t.Name)$($v.Suffix)" }

        # Check the generated runtime config even when it is embedded in a single file.
        $runtimeConfig = Get-ChildItem (Join-Path $artifacts 'bin') -Recurse -Filter "$($t.Name).runtimeconfig.json" |
            Select-Object -First 1
        if (-not $runtimeConfig) { throw "Missing runtime config for $($t.Name)$($v.Suffix)" }
        $runtimeOptions = (Get-Content $runtimeConfig.FullName -Raw | ConvertFrom-Json).runtimeOptions
        $isSelfContained = @($runtimeOptions.includedFrameworks).Where({ $_ }).Count -gt 0
        if ($isSelfContained -ne ($v.SelfContained -eq 'true')) {
            throw "Wrong runtime packaging for $($t.Name)$($v.Suffix)"
        }

        # Debug symbols are useful locally but only bloat what every operator downloads.
        Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

        if ($t.ShimFrom -and $Runtime -like 'win-*') {
            $from = Join-Path $stage $t.ShimFrom
            $to = Join-Path $stage $t.ShimTo
            if (-not (Test-Path $from)) { throw "Published console is missing $($t.ShimFrom)." }
            Copy-Item $from $to -Force
        }

        $lightSuffix = $v.Suffix
        $names = @($t.Assets | ForEach-Object { $_ -replace '\.zip$', "$lightSuffix.zip" })
        # appsettings.json ships as a template; a real token is written by install-agent.ps1.
        $primary = Join-Path $OutputPath $names[0]
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $primary -Force
        $stage = [IO.Path]::GetFullPath($stage)
        if (-not $stage.StartsWith($OutputPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Release stage escaped the output directory.'
        }
        Remove-Item $stage -Recurse -Force

        foreach ($asset in $names) {
            $archive = Join-Path $OutputPath $asset
            if ($archive -ne $primary) { Copy-Item $primary $archive -Force }
            $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
            Write-Host "    $asset  $size MB"
            # The self-update path requires a checksum sidecar next to every payload.
            $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $asset" -Encoding ascii
        }
    }
}

if ($SkipPublish) {
    Write-Host ''
    Write-Host "Archives are in $OutputPath. Publish them with:" -ForegroundColor Cyan
    Write-Host "  gh release create $Version $OutputPath\*.zip $OutputPath\*.sha256 --title $Version --notes '...'"
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'The GitHub CLI (gh) is required to publish. Re-run with -SkipPublish and upload by hand.'
}

Write-Host "==> Creating release $Version" -ForegroundColor Cyan
$assets = ((Get-ChildItem $OutputPath -Filter *.zip).FullName + (Get-ChildItem $OutputPath -Filter *.sha256).FullName) | Where-Object { $_ }
$ghArgs = @('release', 'create', $Version) + $assets + @('--title', $Version, '--notes', $Notes)
if ($Draft) { $ghArgs += '--draft' }

& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }

Write-Host ''
Write-Host "Published $Version." -ForegroundColor Green
Write-Host 'Operators can now run:  mining-fleet update'
