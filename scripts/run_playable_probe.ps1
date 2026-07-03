[CmdletBinding()]
param(
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe',
    [string] $SettlementName = 'n10c',
    [string] $SaveName = 'n10c',
    [string] $PlayerLog = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn\Player.log'),
    [string] $OutputDir = '',
    [int] $LoadTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
    $OutputDir = Join-Path $repoRoot.Path 'testlogs'
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class TimberbornPlayableProbeUser32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
'@

function Get-TimberbornProcesses {
    Get-Process | Where-Object { $_.ProcessName -like '*Timberborn*' }
}

function Get-TimberbornWindow {
    Get-TimberbornProcesses |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
}

function Click-WindowCenter {
    param([System.Diagnostics.Process] $Process)

    $rect = New-Object TimberbornPlayableProbeUser32+RECT
    [TimberbornPlayableProbeUser32]::GetWindowRect($Process.MainWindowHandle, [ref] $rect) | Out-Null
    $x = [int](($rect.Left + $rect.Right) / 2)
    $y = [int](($rect.Top + $rect.Bottom) / 2)
    [TimberbornPlayableProbeUser32]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    [TimberbornPlayableProbeUser32]::SetCursorPos($x, $y) | Out-Null
    [TimberbornPlayableProbeUser32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [TimberbornPlayableProbeUser32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Click-BottomConfirmationArea {
    param([System.Diagnostics.Process] $Process)

    $rect = New-Object TimberbornPlayableProbeUser32+RECT
    [TimberbornPlayableProbeUser32]::GetWindowRect($Process.MainWindowHandle, [ref] $rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        return
    }

    $x = [int]($rect.Left + ($width * 0.50))
    $clickYs = @(
        [int]($rect.Top + ($height * 0.760)),
        [int]($rect.Top + ($height * 0.785)),
        [int]($rect.Top + ($height * 0.735)),
        [int]($rect.Top + ($height * 0.810))
    )

    [TimberbornPlayableProbeUser32]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
    Send-Key 0x0D 60
    Start-Sleep -Milliseconds 100

    foreach ($y in $clickYs) {
        [TimberbornPlayableProbeUser32]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 50
        [TimberbornPlayableProbeUser32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 50
        [TimberbornPlayableProbeUser32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150
    }
}

function Send-Key {
    param(
        [UInt16] $VirtualKey,
        [int] $HoldMilliseconds = 80
    )

    $inputSize = [Runtime.InteropServices.Marshal]::SizeOf([type] [TimberbornPlayableProbeUser32+INPUT])
    $down = New-Object TimberbornPlayableProbeUser32+INPUT
    $down.type = 1
    $down.U.ki.wVk = $VirtualKey
    $down.U.ki.dwFlags = 0
    $up = New-Object TimberbornPlayableProbeUser32+INPUT
    $up.type = 1
    $up.U.ki.wVk = $VirtualKey
    $up.U.ki.dwFlags = 0x0002
    [TimberbornPlayableProbeUser32]::SendInput(1, @($down), $inputSize) | Out-Null
    Start-Sleep -Milliseconds $HoldMilliseconds
    [TimberbornPlayableProbeUser32]::SendInput(1, @($up), $inputSize) | Out-Null
}

function Capture-Window {
    param(
        [System.Diagnostics.Process] $Process,
        [string] $Path
    )

    $rect = New-Object TimberbornPlayableProbeUser32+RECT
    [TimberbornPlayableProbeUser32]::GetWindowRect($Process.MainWindowHandle, [ref] $rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object Drawing.Size $width, $height))
    $graphics.Dispose()
    $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Get-MeanAbsoluteImageDiff {
    param(
        [string] $BeforePath,
        [string] $AfterPath
    )

    $before = [Drawing.Bitmap]::FromFile($BeforePath)
    $after = [Drawing.Bitmap]::FromFile($AfterPath)
    try {
        $width = [Math]::Min($before.Width, $after.Width)
        $height = [Math]::Min($before.Height, $after.Height)
        $stepX = [Math]::Max(1, [int]($width / 160))
        $stepY = [Math]::Max(1, [int]($height / 90))
        [int64] $sum = 0
        [int] $count = 0

        for ($y = 0; $y -lt $height; $y += $stepY) {
            for ($x = 0; $x -lt $width; $x += $stepX) {
                $a = $before.GetPixel($x, $y)
                $b = $after.GetPixel($x, $y)
                $sum += [Math]::Abs($a.R - $b.R) + [Math]::Abs($a.G - $b.G) + [Math]::Abs($a.B - $b.B)
                $count += 3
            }
        }

        return [Math]::Round($sum / [double] $count, 3)
    }
    finally {
        $before.Dispose()
        $after.Dispose()
    }
}

$existing = Get-TimberbornProcesses
if ($existing) {
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
if (Test-Path -LiteralPath $PlayerLog) {
    Move-Item -LiteralPath $PlayerLog -Destination (Join-Path $OutputDir "playable-previous-$stamp.log") -Force
}

$args = @(
    '-settlementName',
    "`"$SettlementName`"",
    '-saveName',
    "`"$SaveName`""
) -join ' '

Start-Process -FilePath $TimberbornExe -WorkingDirectory (Split-Path -Parent $TimberbornExe) -ArgumentList $args | Out-Null

$deadline = (Get-Date).AddSeconds($LoadTimeoutSeconds)
$lastConfirm = [DateTime]::MinValue
while ((Get-Date) -lt $deadline) {
    $windowProcess = Get-TimberbornWindow
    if ($windowProcess -and ((Get-Date) - $lastConfirm).TotalSeconds -gt 5) {
        Click-BottomConfirmationArea $windowProcess
        $lastConfirm = Get-Date
    }

    if ((Test-Path -LiteralPath $PlayerLog) -and
        (Select-String -LiteralPath $PlayerLog -Pattern 'Load time:' -Quiet -ErrorAction SilentlyContinue)) {
        break
    }

    Start-Sleep -Milliseconds 500
}

if (-not (Test-Path -LiteralPath $PlayerLog) -or
    -not (Select-String -LiteralPath $PlayerLog -Pattern 'Load time:' -Quiet -ErrorAction SilentlyContinue)) {
    throw 'Timed out waiting for Timberborn to load the save.'
}

Start-Sleep -Seconds 10
$windowProcess = Get-TimberbornWindow
if (-not $windowProcess) {
    throw 'Timberborn window was not found after load.'
}

Click-WindowCenter $windowProcess
$beforePath = Join-Path $OutputDir "playable-before-$stamp.png"
Capture-Window $windowProcess $beforePath

Send-Key 0x31 80
Start-Sleep -Milliseconds 300
Send-Key 0x44 1000
[TimberbornPlayableProbeUser32]::mouse_event(0x0800, 0, 0, 4294967176, [UIntPtr]::Zero)
Start-Sleep -Seconds 1

$afterPath = Join-Path $OutputDir "playable-after-$stamp.png"
Capture-Window $windowProcess $afterPath
$currentLog = Join-Path $OutputDir "playable-current-$stamp.log"
Copy-Item -LiteralPath $PlayerLog -Destination $currentLog -Force
$meanDiff = Get-MeanAbsoluteImageDiff $beforePath $afterPath
$implementation = (Select-String -LiteralPath $PlayerLog -Pattern 'optimizedImplementation=' | Select-Object -Last 1).Line
$sawException = Select-String -LiteralPath $PlayerLog -Pattern 'Exception|NullReferenceException' -Quiet -ErrorAction SilentlyContinue

Get-TimberbornProcesses | Stop-Process -Force

[pscustomobject]@{
    Before = $beforePath
    After = $afterPath
    CurrentLog = $currentLog
    MeanAbsoluteDiff = $meanDiff
    SawException = [bool] $sawException
    Implementation = $implementation
}
