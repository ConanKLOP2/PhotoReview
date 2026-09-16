<#
.SYNOPSIS
  Drive PhotoReview.App.exe with real keyboard input (SendInput) for perf diagnosis.

.DESCRIPTION
  Temporarily rewrites %LOCALAPPDATA%\PhotoReview\config.json (LoggingEnabled, LoadingMode),
  optionally clears the reconstructible disk caches, starts the app pointed at -Folder with
  PHOTOREVIEW_DATA_ROOT set to an isolated temp root, waits for the main window, brings it to
  the foreground, sends -Count presses of -Keys spaced -IntervalMs apart via a real SendInput
  call (not SendKeys / not a synthetic WPF routed event), waits, then closes the app.

  Does NOT modify PhotoReview app source or business logic. Only touches the user's own
  config.json (restored by the caller) and reconstructible cache files.

.PARAMETER Exe       Path to PhotoReview.App.exe
.PARAMETER Folder    Folder to open (one of the F1/F1b/F2 fixture paths)
.PARAMETER Mode      Fast | Preview | Original -> written to config.json LoadingMode
.PARAMETER Keys      Which key to send, repeated. Currently only "Right" is implemented.
.PARAMETER Count     Number of key presses to send
.PARAMETER IntervalMs Delay between key presses, in milliseconds
.PARAMETER WarmupMs  Delay after the window appears before sending any input
.PARAMETER DataRoot  Value for PHOTOREVIEW_DATA_ROOT (isolated temp dir for this run)
.PARAMETER ColdCache Clear %LOCALAPPDATA%\PhotoReview\cache\*.png and \thumbnails\*.png before launch

.OUTPUTS
  Path to app.log for this run (under $DataRoot\logs\app.log).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Folder,
    [Parameter(Mandatory)] [ValidateSet('Fast','Preview','Original')] [string]$Mode,
    [ValidateSet('Right')] [string]$Keys = 'Right',
    [Parameter(Mandatory)] [int]$Count,
    [Parameter(Mandatory)] [int]$IntervalMs,
    [int]$WarmupMs = 2000,
    [Parameter(Mandatory)] [string]$DataRoot,
    [switch]$ColdCache
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Win32 P/Invoke: SetForegroundWindow, ShowWindow, SendInput (VK_RIGHT)
# ---------------------------------------------------------------------------
$sig = @'
using System;
using System.Runtime.InteropServices;

public static class DiagNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    // Robust foreground switch: Windows normally refuses SetForegroundWindow calls
    // from a background/automation process. Attaching our input queue to the
    // target window's thread first works around that restriction.
    public static bool ForceForeground(IntPtr hWnd)
    {
        uint foreProcId;
        uint targetProcId;
        var foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out foreProcId);
        var targetThread = GetWindowThreadProcessId(hWnd, out targetProcId);
        var curThread = GetCurrentThreadId();

        bool attachedToFore = foreThread != curThread && AttachThreadInput(curThread, foreThread, true);
        bool attachedToTarget = targetThread != curThread && AttachThreadInput(curThread, targetThread, true);
        try
        {
            ShowWindow(hWnd, 9); // SW_RESTORE
            BringWindowToTop(hWnd);
            bool ok = SetForegroundWindow(hWnd);
            return ok;
        }
        finally
        {
            if (attachedToTarget) AttachThreadInput(curThread, targetThread, false);
            if (attachedToFore) AttachThreadInput(curThread, foreThread, false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // The union must include MOUSEINPUT (its largest member) or Marshal.SizeOf(INPUT)
    // under-computes the struct size on x64 (missing padding), which makes SendInput
    // fail with ERROR_INVALID_PARAMETER (87) even though the keyboard fields are correct.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
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

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_RIGHT = 0x27;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendKey(ushort vk)
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero };
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}
'@
if (-not ('DiagNative' -as [type])) {
    Add-Type -TypeDefinition $sig -Language CSharp
}

function Get-ConfigPath {
    Join-Path $env:LOCALAPPDATA 'PhotoReview\config.json'
}

function Backup-Config {
    param([string]$BackupPath)
    $cfgPath = Get-ConfigPath
    New-Item -ItemType Directory -Force -Path (Split-Path $BackupPath) | Out-Null
    Copy-Item -Path $cfgPath -Destination $BackupPath -Force
}

function Set-DiagConfig {
    param([string]$Mode)
    $cfgPath = Get-ConfigPath
    $json = Get-Content -Raw -Path $cfgPath | ConvertFrom-Json
    $json.LoggingEnabled = $true
    $json.LoadingMode = $Mode
    ($json | ConvertTo-Json -Depth 10) | Set-Content -Path $cfgPath -Encoding UTF8
}

function Clear-ReconstructibleCache {
    $cacheDir = Join-Path $env:LOCALAPPDATA 'PhotoReview\cache'
    $thumbDir = Join-Path $env:LOCALAPPDATA 'PhotoReview\thumbnails'
    if (Test-Path $cacheDir) { Get-ChildItem -Path $cacheDir -Filter '*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }
    if (Test-Path $thumbDir) { Get-ChildItem -Path $thumbDir -Filter '*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Exe)) { throw "Exe not found: $Exe" }
if (-not (Test-Path -LiteralPath $Folder)) { throw "Folder not found: $Folder" }

Set-DiagConfig -Mode $Mode
if ($ColdCache) { Clear-ReconstructibleCache }

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
$env:PHOTOREVIEW_DATA_ROOT = $DataRoot

$proc = $null
try {
    $proc = Start-Process -FilePath $Exe -ArgumentList "`"$Folder`"" -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($proc.MainWindowHandle -eq 0) {
        throw "Timed out waiting for MainWindowHandle (30s) for $Exe on $Folder"
    }

    Start-Sleep -Milliseconds $WarmupMs

    $hwnd = $proc.MainWindowHandle
    [DiagNative]::ForceForeground($hwnd) | Out-Null
    Start-Sleep -Milliseconds 300
    if ([DiagNative]::GetForegroundWindow() -ne $hwnd) {
        Write-Warning "Could not bring app window to foreground before sending input; keys may not reach it"
    }

    $vk = [DiagNative]::VK_RIGHT
    for ($i = 0; $i -lt $Count; $i++) {
        if ([DiagNative]::GetForegroundWindow() -ne $hwnd) {
            Write-Warning "Window lost foreground before keypress $i; re-foregrounding"
            [DiagNative]::ForceForeground($hwnd) | Out-Null
            Start-Sleep -Milliseconds 150
        }
        [DiagNative]::SendKey($vk)
        Start-Sleep -Milliseconds $IntervalMs
    }

    Start-Sleep -Seconds 3

    $proc.Refresh()
    if (-not $proc.HasExited) {
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(10000)) {
            Write-Warning "App did not exit within 10s after CloseMainWindow; killing"
            $proc.Kill()
            $proc.WaitForExit(5000) | Out-Null
        }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill() } catch {}
    }
}

$logPath = Join-Path $DataRoot 'logs\app.log'
Write-Output $logPath
