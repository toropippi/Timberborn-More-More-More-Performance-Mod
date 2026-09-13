[CmdletBinding()]
param(
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe',
    [string] $SteamExe = 'C:\Program Files (x86)\Steam\steam.exe',
    [string] $SteamAppId = '1062090',
    [string] $SettlementName = 'n10c',
    [string] $SaveName = 'n10c',
    # LaunchArgs = the game's own -settlementName/-saveName autoload.
    # MenuLoad   = T3MPTestDriver loads the save from the main menu scene.
    # NewGame    = T3MPTestDriver starts a new game (first built-in map unless
    #              -MapName is given). MenuLoad/NewGame require the test driver
    #              mod in Mods\T3MPTestDriver (verify_release.ps1 deploys it).
    [ValidateSet('LaunchArgs', 'MenuLoad', 'NewGame')]
    [string] $Scenario = 'LaunchArgs',
    [string] $MapName = '',
    # >0 = T3MPTestDriver resumes the loaded game at this speed (e.g. 99 for
    # high-speed visual repros). Requires the test driver mod.
    [double] $TestSpeed = 0,
    [string] $PlayerLog = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn\Player.log'),
    [string] $OutputDir = '',
    [int] $LoadTimeoutSeconds = 180,
    [int] $SecondsAfterLoad = 0,
    # Optional dev-driver completion marker. SecondsAfterLoad becomes a timeout.
    [string] $CompletionPattern = '',
    [switch] $UseSteamLaunchOptions,
    [switch] $AutoConfirmMods,
    [switch] $SkipModManager,
    [switch] $BenchAutoUltra,
    [switch] $BenchTopoUi,
    [switch] $BenchSmoothMode,
    [switch] $BenchTypeSort,
    [switch] $BenchHotspot,
    [switch] $BenchDecide,
    [switch] $BenchSpawn,
    [switch] $BenchAnim,
    [switch] $BenchRenderStats,
    [switch] $BenchBotInstancing,
    [switch] $BenchBotInstancingStaticTime,
    [switch] $BenchBotInstancingPerInstance,
    [switch] $AutoResumeAfterLoad,
    [switch] $SkipAutoResumeAfterLoad,
    [switch] $ForceOptimizedAfterLoad,
    [switch] $PressNormalAfterLoad,
    [switch] $PressUltraAfterLoad,
    [int] $AutoConfirmStartSeconds = 12,
    [int] $AutoConfirmIntervalSeconds = 5,
    [int] $AutoConfirmMaxClicks = 10,
    # Extra raw game command line arguments (e.g. '-t3mpTestRuntimeBaseline').
    [string[]] $ExtraGameArgs = @(),
    [switch] $StopAfter
)

$ErrorActionPreference = 'Stop'

if ($UseSteamLaunchOptions -and $StopAfter) {
    throw 'StopAfter requires a directly launched game process; use direct launch to establish ownership.'
}

$shouldAutoResumeAfterLoad = $false

if (-not $UseSteamLaunchOptions -and -not (Test-Path -LiteralPath $TimberbornExe)) {
    throw "Timberborn.exe was not found: $TimberbornExe"
}

if ($UseSteamLaunchOptions -and -not (Test-Path -LiteralPath $SteamExe)) {
    throw "steam.exe was not found: $SteamExe"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $scriptRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { $PSScriptRoot }
    $repoRoot = Resolve-Path -LiteralPath (Join-Path $scriptRoot '..')
    $OutputDir = Join-Path $repoRoot.Path 'testlogs'
}

function Get-TimberbornProcesses {
    Get-Process | Where-Object { $_.ProcessName -like '*Timberborn*' }
}

if ($AutoConfirmMods -or $ForceOptimizedAfterLoad -or $PressNormalAfterLoad -or $PressUltraAfterLoad) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TimberbornProbeUser32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
'@
}

function Invoke-TimberbornBottomOkClick {
    $windowProcess = Get-TimberbornProcesses |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
    if (-not $windowProcess) {
        return $false
    }

    $rect = New-Object TimberbornProbeUser32+RECT
    if (-not [TimberbornProbeUser32]::GetWindowRect($windowProcess.MainWindowHandle, [ref] $rect)) {
        return $false
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        return $false
    }

    $x = [int]($rect.Left + ($width * 0.50))
    $clickYs = @(
        [int]($rect.Top + ($height * 0.760)),
        [int]($rect.Top + ($height * 0.785)),
        [int]($rect.Top + ($height * 0.735)),
        [int]($rect.Top + ($height * 0.810))
    )
    [TimberbornProbeUser32]::SetForegroundWindow($windowProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    [TimberbornProbeUser32]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [TimberbornProbeUser32]::keybd_event(0x0D, 0, 0x0002, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150

    foreach ($y in $clickYs) {
        [TimberbornProbeUser32]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 50
        [TimberbornProbeUser32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 50
        [TimberbornProbeUser32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150
    }

    return $true
}

function Invoke-TimberbornKeyPress {
    param([byte] $VirtualKey)

    $windowProcess = Get-TimberbornProcesses |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
    if (-not $windowProcess) {
        return $false
    }

    [TimberbornProbeUser32]::SetForegroundWindow($windowProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    [TimberbornProbeUser32]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [TimberbornProbeUser32]::keybd_event($VirtualKey, 0, 0x0002, [UIntPtr]::Zero)
    return $true
}

function Invoke-TimberbornCtrlShiftKeyPress {
    param([byte] $VirtualKey)

    $windowProcess = Get-TimberbornProcesses |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
    if (-not $windowProcess) {
        return $false
    }

    [TimberbornProbeUser32]::SetForegroundWindow($windowProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    [TimberbornProbeUser32]::keybd_event(0xA2, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [TimberbornProbeUser32]::keybd_event(0xA0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [TimberbornProbeUser32]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [TimberbornProbeUser32]::keybd_event($VirtualKey, 0, 0x0002, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [TimberbornProbeUser32]::keybd_event(0xA0, 0, 0x0002, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [TimberbornProbeUser32]::keybd_event(0xA2, 0, 0x0002, [UIntPtr]::Zero)
    return $true
}

$existing = Get-TimberbornProcesses
if ($existing) {
    throw "Timberborn is already running. Close it before running this probe. Pids: $($existing.Id -join ', ')"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$startedAt = Get-Date
$stamp = $startedAt.ToString('yyyyMMdd-HHmmss')

if (Test-Path -LiteralPath $PlayerLog) {
    $previousLog = Join-Path $OutputDir "autoload-previous-$stamp.log"
    Move-Item -LiteralPath $PlayerLog -Destination $previousLog -Force
    Write-Host "Moved previous Player.log to: $previousLog"
}

function Quote-ProcessArgument([string] $Value) {
    '"' + ($Value -replace '"', '\"') + '"'
}

$arguments = if ($UseSteamLaunchOptions) {
    ''
} else {
    $argList = @(switch ($Scenario) {
        'LaunchArgs' {
            @('-settlementName', (Quote-ProcessArgument $SettlementName),
              '-saveName', (Quote-ProcessArgument $SaveName))
        }
        'MenuLoad' {
            @('-t3mpTestMenuLoad',
              '-t3mpTestSettlement', (Quote-ProcessArgument $SettlementName),
              '-t3mpTestSave', (Quote-ProcessArgument $SaveName))
        }
        'NewGame' {
            $newGameArgs = @('-t3mpTestNewGame')
            if (-not [string]::IsNullOrWhiteSpace($MapName)) {
                $newGameArgs += @('-t3mpTestMap', (Quote-ProcessArgument $MapName))
            }
            $newGameArgs
        }
    })
    if ($TestSpeed -gt 0) {
        $argList += @('-t3mpTestSpeed', $TestSpeed.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    }
    if ($ExtraGameArgs.Count -gt 0) {
        $argList += $ExtraGameArgs
    }
    if ($SkipModManager) {
        # Opt-in for automated testing only. ModManagerScenePanel.ShouldSkipModManager:
        # '-skipModManager' loads enabled mods and starts the game without the
        # mod manager OK screen. Not used for normal play.
        $argList += '-skipModManager'
    }
    if ($BenchAutoUltra) {
        # Opt-in for the dev benchmark harness only. The mod reads
        # '-benchAutoUltra' and auto-applies ultra speed + render blackout after
        # load. Normal play uses the manual speed keys instead.
        $argList += '-benchAutoUltra'
    }
    if ($BenchTopoUi) {
        # Opt-in for the topology-UI investigation harness. The mod reads
        # '-benchTopoUi' and runs the automated gear/path selection scenario
        # at forced speed x50, rendered (see TopologyUiScenario).
        $argList += '-benchTopoUi'
    }
    if ($BenchSmoothMode) {
        # Starts the Shift+O smooth mode (fps-priority timeScale governor)
        # already enabled, for automated verification.
        $argList += '-benchSmoothMode'
    }
    if ($BenchTypeSort) {
        # Measurement-only experiment: type-sorted bucket sweep (results NOT
        # vanilla-identical; never use for correctness comparisons).
        $argList += '-benchTypeSort'
    }
    if ($BenchHotspot) {
        # Attribution-only: per-component-type Tick timing reported every ~20s.
        # Stopwatch overhead contaminates ticks/s - never use for A/B numbers.
        $argList += '-benchHotspot'
    }
    if ($BenchDecide) {
        # Attribution-only: per-type Behavior.Decide timing + no-action rates
        # inside BehaviorManager. Same caveat as -benchHotspot.
        $argList += '-benchDecide'
    }
    if ($BenchSpawn) {
        # Attribution-only: entity spawn/delete tax split (event fanout,
        # template instantiate, lifecycle phases). Same caveat as -benchHotspot.
        $argList += '-benchSpawn'
    }
    if ($BenchAnim) {
        # Attribution-only: per-frame animation sampling split (vertex material
        # set vs node transform write vs loop). Same caveat as -benchHotspot.
        $argList += '-benchAnim'
    }
    if ($BenchRenderStats) {
        # Attribution-only: Unity render counters plus a temporary A/B that
        # disables active vertex-animation MeshRenderers.
        $argList += '-benchRenderStats'
    }
    if ($BenchBotInstancing) {
        # Rendering experiment: replace initially-visible Iron Teeth bot
        # renderers with exact-animation-time DrawMeshInstanced batches.
        $argList += '-benchBotInstancing'
    }
    if ($BenchBotInstancingStaticTime) {
        # Theoretical lower-bound rendering experiment: collapse animation
        # time within each static bot render key, yielding one draw per key.
        $argList += '-benchBotInstancingStaticTime'
    }
    if ($BenchBotInstancingPerInstance) {
        # Custom BotURP shader experiment: preserve each bot's animation time
        # in an instance property while drawing once per static render key.
        $argList += '-benchBotInstancingPerInstance'
    }
    $argList -join ' '
}

$outLog = Join-Path $OutputDir "autoload-$stamp.log"

Write-Host "Launching Timberborn with autoload args."
Write-Host "  settlement: $SettlementName"
Write-Host "  save:       $SaveName"
Write-Host "  args:       $(if ($UseSteamLaunchOptions) { '<Steam launch options>' } else { $arguments })"

$process = $null
$observationStarted = $null
$observationEnded = $null
$observationCompleted = $false
$completionMatched = $false
$probeFailure = $null
$finalizationFailures = [Collections.Generic.List[string]]::new()
$logCaptured = $false
$deadline = (Get-Date).AddSeconds($LoadTimeoutSeconds)
$processGraceDeadline = (Get-Date).AddSeconds(20)
$sawLoading = $false
$sawLoadTime = $false
$sawException = $false
$lastLoadTimeLine = $null
$lastExceptionLine = $null
$autoConfirmClicks = 0
$lastAutoConfirm = [DateTime]::MinValue

function Update-ProbeStateFromLines([string[]] $Lines) {
    if (-not $Lines -or $Lines.Count -eq 0) {
        return
    }

    if (@($Lines | Where-Object { $_ -match 'Loading saved game' }).Count -gt 0) {
        $script:sawLoading = $true
    }

    # Scene index 2 is the game scene; the main menu also logs a "Load time"
    # (scene index 1) which must not end the wait in menu-driven scenarios.
    $loadTimeMatches = @($Lines | Where-Object { $_ -match 'Load time:.*scene index: 2' })
    if ($loadTimeMatches.Count -gt 0) {
        $script:sawLoadTime = $true
        $script:lastLoadTimeLine = $loadTimeMatches[$loadTimeMatches.Count - 1]
    }

    $exceptionMatches = @($Lines | Where-Object {
        $_ -match 'First uncaught exception' -or
        $_ -match '^Rethrow as Exception:' -or
        $_ -match 'InvalidOperationException|^\s*(?:[\w]+\.)*[\w]*Exception\s*:'
    })
    if ($exceptionMatches.Count -gt 0) {
        $script:sawException = $true
        $script:lastExceptionLine = $exceptionMatches[$exceptionMatches.Count - 1]
    }
}

try {
    $process = if ($UseSteamLaunchOptions) {
        Start-Process -FilePath $SteamExe -WorkingDirectory (Split-Path -Parent $SteamExe) -ArgumentList "-applaunch $SteamAppId" -PassThru
    } else {
        Start-Process -FilePath $TimberbornExe -WorkingDirectory (Split-Path -Parent $TimberbornExe) -ArgumentList $arguments -PassThru
    }
    while ((Get-Date) -lt $deadline) {
        $running = Get-TimberbornProcesses
        if (-not $running) {
            if ((Get-Date) -gt $processGraceDeadline) {
                Write-Host "Timberborn process exited before load completion."
                break
            }

            Start-Sleep -Seconds 1
            continue
        }

        if (Test-Path -LiteralPath $PlayerLog) {
            $tail = Get-Content -LiteralPath $PlayerLog -Tail 160 -ErrorAction SilentlyContinue
            Update-ProbeStateFromLines @($tail)

            if ($sawLoadTime) {
                break
            }
        }

        if ($AutoConfirmMods -and
            -not $sawLoading -and
            $autoConfirmClicks -lt $AutoConfirmMaxClicks -and
            ((Get-Date) - $startedAt).TotalSeconds -ge $AutoConfirmStartSeconds -and
            ((Get-Date) - $lastAutoConfirm).TotalSeconds -ge $AutoConfirmIntervalSeconds)
        {
            if (Invoke-TimberbornBottomOkClick) {
                $autoConfirmClicks++
                $lastAutoConfirm = Get-Date
                Write-Host "Auto-confirm click sent: $autoConfirmClicks"
            }
        }

        Start-Sleep -Seconds 2
    }

    if ($AutoResumeAfterLoad -or (-not $SkipAutoResumeAfterLoad)) {
        Write-Host "Auto-resume key press skipped. Speed is controlled by the deployed mod build."
    }

    if ($ForceOptimizedAfterLoad -and (Get-TimberbornProcesses)) {
        if (Invoke-TimberbornCtrlShiftKeyPress 0x4F) {
            Write-Host "Forced Optimized hotkey sent."
        }
    }

    if ($PressUltraAfterLoad -and (Get-TimberbornProcesses)) {
        if (Invoke-TimberbornKeyPress 0x34) {
            Write-Host "Ultra speed key 4 sent."
        }
    }

    if ($PressNormalAfterLoad -and (Get-TimberbornProcesses)) {
        if (Invoke-TimberbornKeyPress 0x31) {
            Write-Host "Normal speed key 1 sent."
        }
    }




    if ($SecondsAfterLoad -gt 0 -and (Get-TimberbornProcesses)) {
        $observationStarted = [DateTime]::UtcNow
        $observationDeadline = (Get-Date).AddSeconds($SecondsAfterLoad)
        while ((Get-Date) -lt $observationDeadline -and (Get-TimberbornProcesses)) {
            if ($CompletionPattern -and (Test-Path -LiteralPath $PlayerLog)) {
                $observationTail = Get-Content -LiteralPath $PlayerLog -Tail 80 -ErrorAction SilentlyContinue
                if ($observationTail -match $CompletionPattern) { $completionMatched = $true; break }
            }
            Start-Sleep -Seconds 2
        }
        $process.Refresh()
        $observationCompleted = $sawLoadTime -and ((Get-Date) -ge $observationDeadline -or $completionMatched) -and
            (-not $UseSteamLaunchOptions) -and (-not $process.HasExited)
    }

} catch {
    $probeFailure = $_.ToString()
} finally {
    # Freeze observed duration before log copying and process teardown.
    if ($null -ne $observationStarted) { $observationEnded = [DateTime]::UtcNow }
    try {
        if (Test-Path -LiteralPath $PlayerLog) {
            $logLines = Get-Content -LiteralPath $PlayerLog -ErrorAction Stop
            Update-ProbeStateFromLines @($logLines)
        }
    } catch { $finalizationFailures.Add($_.ToString()) }
    try {
        if (Test-Path -LiteralPath $PlayerLog) {
            Copy-Item -LiteralPath $PlayerLog -Destination $outLog -Force -ErrorAction Stop
            $logCaptured = $true
        }
    } catch { $finalizationFailures.Add($_.ToString()) }
    if ($StopAfter) {
        try {
            if ($null -ne $process) {
                $process.Refresh()
                if (-not $process.HasExited) {
                    try { Stop-Process -InputObject $process -Force -ErrorAction Stop }
                    catch { $process.Refresh(); if (-not $process.HasExited) { throw } }
                    if (-not $process.WaitForExit(30000)) { throw 'Owned probe did not exit' }
                }
            }
        } catch { $finalizationFailures.Add($_.ToString()) }
    }
    [ordered]@{
        ProcessId = if ($null -ne $process) { $process.Id } else { $null }
        DirectLaunch = -not [bool]$UseSteamLaunchOptions
        SawLoadTime = $sawLoadTime
        SawException = $sawException
        ObservationCompleted = $observationCompleted
        CompletionMatched = $completionMatched
        RequestedSeconds = $SecondsAfterLoad
        ObservationStarted = if ($null -ne $observationStarted) { $observationStarted.ToString('o') } else { $null }
        ObservationEnded = if ($null -ne $observationEnded) { $observationEnded.ToString('o') } else { $null }
        ObservedSeconds = if ($null -ne $observationEnded) { ($observationEnded - $observationStarted).TotalSeconds } else { 0 }
        Log = $outLog
        LogCaptured = $logCaptured
        Failure = $probeFailure
        FinalizationFailures = $finalizationFailures.ToArray()
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir "probe-summary-$stamp.json") -Encoding utf8
}
if ($probeFailure -or $finalizationFailures.Count) {
    throw ('Probe failed: ' + $probeFailure + ' ' + ($finalizationFailures -join '; ') + '; records: ' + $OutputDir)
}

$pids = (Get-TimberbornProcesses | Select-Object -ExpandProperty Id) -join ','
if ([string]::IsNullOrWhiteSpace($pids)) {
    $pids = "$($process.Id) (launcher/exited)"
}

Write-Host "Autoload probe summary:"
Write-Host "  pid:          $pids"
Write-Host "  sawLoading:   $sawLoading"
Write-Host "  sawLoadTime:  $sawLoadTime"
Write-Host "  sawException: $sawException"
Write-Host "  autoClicks:   $autoConfirmClicks"
if ($lastLoadTimeLine) {
    Write-Host "  loadTime:     $lastLoadTimeLine"
}
if ($lastExceptionLine) {
    Write-Host "  exception:    $lastExceptionLine"
}
Write-Host "  copiedLog:    $outLog"
