# Run on the mining node as Administrator/SYSTEM. PresentMon must already be installed.
# Does not start or stop miners. The agent owns their lifecycle and CPU thermal control.
[CmdletBinding()]
param(
    [string]$AgentDirectory = 'C:\Program Files\mining-fleet-agent',
    [string]$DataDirectory = 'C:\mining\fleet-game',
    [string]$GameProcess = 'dontstarve_steam_x64',
    [string]$GameName = '',
    [double]$TargetFps = 30,
    [double]$MaxTemperatureC = 85,
    [switch]$Once
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$presentMon = Join-Path $DataDirectory 'PresentMon-2.3.0-x64.exe'
if (!(Test-Path -LiteralPath $presentMon)) { throw "PresentMon missing: $presentMon" }
$settings = Get-Content -Raw (Join-Path $AgentDirectory 'appsettings.json') | ConvertFrom-Json
$headers = @{ 'X-Fleet-Token' = $settings.Agent.Token.Trim() }
$base = 'http://127.0.0.1:47800/api/v1'
$statePath = Join-Path $DataDirectory 'governor-state.json'
$logPath = Join-Path $DataDirectory 'governor.jsonl'
$invariant = [Globalization.CultureInfo]::InvariantCulture
if ([string]::IsNullOrWhiteSpace($GameName)) {
    $GameName = if ($GameProcess -eq 'dontstarve_steam_x64') { 'DST' } else { $GameProcess }
}
$statusPath = Join-Path $AgentDirectory 'game-mining-status.json'

function Publish-GameStatus($Active, $Reduced, $Frames) {
    $status = @{
        observedAt = [DateTimeOffset]::UtcNow.ToString('o')
        active = [bool]$Active; gameName = $GameName; reduced = [bool]$Reduced
        fps = $(if ($null -ne $Frames) { [math]::Round($Frames.Fps, 2) } else { $null })
    }
    $temporary = $statusPath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($status | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $statusPath) { [IO.File]::Replace($temporary, $statusPath, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $statusPath) }
}

# GPU scheduling priority, not CPU priority (which can park XMRig on E-cores).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FleetGameGpuPriority {
    [DllImport("gdi32.dll")]
    public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("gdi32.dll")]
    public static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);
}
'@

function Set-AgentConfig($Patch) {
    $null = Invoke-RestMethod "$base/config" -Headers $headers -Method Put -TimeoutSec 8 `
        -ContentType 'application/json' -Body ($Patch | ConvertTo-Json -Depth 5)
}

function Measure-GameFps {
    $csv = Join-Path $DataDirectory 'frames.csv'
    # Never mistake an old capture for fresh frames. Only this exact scratch file is removed.
    if (Test-Path -LiteralPath $csv) { Remove-Item -LiteralPath $csv }
    $session = 'FleetGame-' + [guid]::NewGuid().ToString('N')
    $arguments = "--process_name $GameProcess.exe --output_file `"$csv`" --timed 10 " +
        "--terminate_after_timed --no_console_stats --no_track_gpu --no_track_input " +
        "--no_track_display --session_name $session --v1_metrics"
    $capture = Start-Process -FilePath $presentMon -ArgumentList $arguments -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $DataDirectory 'capture-error.txt') `
        -RedirectStandardOutput (Join-Path $DataDirectory 'capture-output.txt') -PassThru
    try {
        # Retain the native handle; Windows PowerShell can otherwise lose ExitCode on exit.
        $null = $capture.Handle
        if (!$capture.WaitForExit(18000)) {
            $capture.Kill()
            throw 'PresentMon timed out; no FPS decision made'
        }
        if ($capture.ExitCode -ne 0) { throw 'PresentMon failed; no FPS decision made' }
        $errors = Get-Content -Raw (Join-Path $DataDirectory 'capture-error.txt')
        if ($errors -match '(?i)events were lost') { return $null }
        if (!(Test-Path -LiteralPath $csv)) { return $null }
        # Use the main swap chain; summing multiple swap chains would overstate FPS.
        $chain = Import-Csv -LiteralPath $csv | Group-Object ProcessID,SwapChainAddress |
            Sort-Object Count -Descending | Select-Object -First 1
        if (!$chain -or $chain.Count -lt 60) { return $null }
        $frames = @($chain.Group | ForEach-Object {
            $ms = 0.0
            if ([double]::TryParse($_.msBetweenPresents, [Globalization.NumberStyles]::Float,
                    $invariant, [ref]$ms) -and $ms -gt 0) { $ms }
        } | Sort-Object)
        if ($frames.Count -lt 60) { return $null }
        return [pscustomobject]@{
            Fps = 1000 / ($frames | Measure-Object -Average).Average
            P95Ms = $frames[[int][math]::Floor(($frames.Count - 1) * 0.95)]
        }
    }
    finally { $capture.Dispose() }
}

$mutex = New-Object Threading.Mutex($false, 'Global\MiningFleet-GameGovernor')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'The game governor is already running' }
    $badSamples = 0
    $goodSamples = 0
    do {
        try {
            $config = Invoke-RestMethod "$base/config" -Headers $headers -TimeoutSec 8
            $game = Get-Process -Name $GameProcess -ErrorAction SilentlyContinue
            $saved = if (Test-Path -LiteralPath $statePath) {
                Get-Content -Raw $statePath | ConvertFrom-Json
            } else { $null }
            $gpu = Get-Process -Name lolMiner -ErrorAction SilentlyContinue | Where-Object {
                $config.gpuMiner.executablePath -and $_.Path -and
                    [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($config.gpuMiner.executablePath)
            } | Select-Object -First 1

            if ($game -and !$saved) {
                # Persist before changing anything, so an agent/helper restart can restore it.
                $limit = (& nvidia-smi -i 0 --query-gpu=power.limit --format=csv,noheader,nounits)
                if ($LASTEXITCODE -ne 0) { throw 'Could not read GPU power limit' }
                $saved = [pscustomobject]@{
                    CpuPercent = $(if ($null -eq $config.maxCpuPercent) { 100 } else { $config.maxCpuPercent })
                    PowerWatts = [double]::Parse($limit.Trim(), $invariant)
                    GpuPid = $null
                    GpuPriority = 2
                }
                $saved | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $statePath
            }
            if ($game -and $gpu) {
                if ($saved.GpuPid -ne $gpu.Id) {
                    $previous = 2
                    $result = [FleetGameGpuPriority]::D3DKMTGetProcessSchedulingPriorityClass($gpu.Handle, [ref]$previous)
                    if ($result -ne 0) { throw "GPU priority read failed: $result" }
                    $saved.GpuPid = $gpu.Id
                    $saved.GpuPriority = $previous
                    $saved | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath $statePath
                }
                $result = [FleetGameGpuPriority]::D3DKMTSetProcessSchedulingPriorityClass($gpu.Handle, 0)
                if ($result -ne 0) { throw "GPU priority write failed: $result" }
            }

            if (!$game -and $saved) {
                Set-AgentConfig @{ maxCpuPercent = $saved.CpuPercent }
                & nvidia-smi -i 0 -pl $saved.PowerWatts | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not restore GPU power limit' }
                if ($gpu -and $gpu.Id -eq $saved.GpuPid) {
                    $result = [FleetGameGpuPriority]::D3DKMTSetProcessSchedulingPriorityClass($gpu.Handle, $saved.GpuPriority)
                    if ($result -ne 0) { throw "GPU priority restore failed: $result" }
                }
                Remove-Item -LiteralPath $statePath
                $badSamples = 0
                $goodSamples = 0
            }

            $frames = $null
            if ($game) {
                try { $frames = Measure-GameFps }
                catch {
                    Add-Content -LiteralPath $logPath -Encoding UTF8 -Value (
                        @{time=[DateTimeOffset]::Now.ToString('o');error=$_.Exception.Message} | ConvertTo-Json -Compress)
                }
            }
            $snapshot = Invoke-RestMethod "$base/status" -Headers $headers -TimeoutSec 12
            $card = $snapshot.hardware.gpus | Select-Object -First 1
            $ceiling = $config.maxCpuPercent
            if ($game) {
                if ($null -eq $ceiling) { $ceiling = 100 }
                $ceiling = [math]::Min(50, $ceiling)
                if ($null -ne $frames) {
                    if ($frames.Fps -lt $TargetFps) { $badSamples++; $goodSamples = 0 }
                    elseif ($frames.Fps -gt ($TargetFps * 1.1) -and
                            $snapshot.hardware.cpuTemperatureC -lt ($MaxTemperatureC - 3) -and
                            $card.temperatureC -lt ($MaxTemperatureC - 3)) {
                        $goodSamples++; $badSamples = 0
                    } else { $goodSamples = 0; $badSamples = 0 }
                    if ($badSamples -ge 2) {
                        $ceiling = [math]::Max([math]::Min(10, $saved.CpuPercent), $ceiling - 10)
                        $badSamples = 0
                    }
                    if ($goodSamples -ge 4) {
                        $ceiling = [math]::Min([math]::Min(50, $saved.CpuPercent), $ceiling + 10)
                        $goodSamples = 0
                    }
                } else { $badSamples = 0; $goodSamples = 0 }
                if ($ceiling -ne $config.maxCpuPercent -or $config.maxCpuTemperatureC -ne $MaxTemperatureC) {
                    Set-AgentConfig @{ maxCpuPercent = $ceiling; maxCpuTemperatureC = $MaxTemperatureC }
                }
                # RTX 4060's supported minimum. No invented lower power value or clock underclock
                # that would slow the game too. lolMiner's tstop=85 remains the emergency guard.
                & nvidia-smi -i 0 -pl 90 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not set GPU power limit' }
            }
            $record = [ordered]@{
                time = [DateTimeOffset]::Now.ToString('o'); game = [bool]$game
                fps = $(if ($frames) { [math]::Round($frames.Fps, 2) } else { $null })
                p95Ms = $(if ($frames) { [math]::Round($frames.P95Ms, 2) } else { $null })
                cpuTemperatureC = $snapshot.hardware.cpuTemperatureC; gpuTemperatureC = $card.temperatureC
                cpuPercent = $ceiling; cpuHashrate = $snapshot.miner.hashrate60s; gpuHashrate = $snapshot.gpuMiner.hashrate
                notice = $(if ($game -and !$frames) { 'No reliable FPS sample; holding limits' }
                    elseif ($frames -and $frames.Fps -lt $TargetFps -and $ceiling -le 10) {
                        'FPS below target at minimum mining budget; mining kept running'
                    } else { $null })
            }
            $line = $record | ConvertTo-Json -Compress
            # Recheck after capture: do not keep advertising a game that exited during the sample.
            $active = [bool](Get-Process -Name $GameProcess -ErrorAction SilentlyContinue)
            Publish-GameStatus $active ($active -and ($snapshot.miner.running -or $snapshot.gpuMiner.running)) $frames
            if ((Test-Path $logPath) -and (Get-Item $logPath).Length -gt 2MB) {
                Move-Item -LiteralPath $logPath -Destination ($logPath + '.old') -Force
            }
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $line
            if ($Once) { Write-Output $line }
        }
        catch {
            $line = @{time=[DateTimeOffset]::Now.ToString('o');error=$_.Exception.Message} | ConvertTo-Json -Compress
            Add-Content -LiteralPath $logPath -Encoding UTF8 -Value $line
            if ($Once) { throw }
        }
        if (!$Once) { Start-Sleep -Seconds 20 }
    } while (!$Once)
}
finally {
    if ($locked) {
        # Only the owner may clear telemetry; a rejected second invocation leaves it alone.
        if (!$Once) {
            try { Publish-GameStatus $false $false $null } catch { }
        }
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
