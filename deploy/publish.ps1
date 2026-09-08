<#
.SYNOPSIS
    Publishes the agent and the console as self-contained binaries.

.DESCRIPTION
    Self-contained output means a mining node needs no .NET runtime installed - copy the
    publish\agent folder over and run install-agent.ps1 there.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Runtime linux-x64 -SkipConsole
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = "$PSScriptRoot\..\publish",
    [switch]$SkipConsole
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

$agentOut = Join-Path $OutputPath 'agent'
Write-Host "Publishing agent ($Runtime) to $agentOut"
& dotnet publish (Join-Path $root 'src\MiningFleet.Agent') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false `
    -o $agentOut
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }

if (-not $SkipConsole) {
    $consoleOut = Join-Path $OutputPath 'console'
    Write-Host "Publishing console ($Runtime) to $consoleOut"
    & dotnet publish (Join-Path $root 'src\MiningFleet.Console') `
        -c Release -r $Runtime --self-contained true `
        -o $consoleOut
    if ($LASTEXITCODE -ne 0) { throw 'Console publish failed.' }
}

Write-Host ''
Write-Host 'Done. Next:'
Write-Host "  1. Copy $agentOut to each mining node."
Write-Host '  2. On the node, in an elevated PowerShell: .\install-agent.ps1 -Token "<fleet token>" -SourcePath .\agent'
Write-Host '  3. On this machine, run the console and add the node from the tailnet list.'
