[CmdletBinding()]
param(
    # Passed straight through to `dotnet test` after the fixed CI-equivalent flags below. Use this to
    # override the project/solution, filter, or add extra dotnet-test options.
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$TestArgs,

    # Log file that captures the child process's stdout+stderr (also tailed to the console when it exits).
    # Named-only (not positional) so it is never mistaken for a pass-through dotnet-test argument.
    [Parameter(DontShow = $false)]
    [string]$LogPath = '',

    # Name of the private (non-interactive) Win32 desktop to create. Reused if it somehow already exists
    # (e.g. a previous run left it behind -- CloseDesktop below is best-effort in a finally block).
    [string]$DesktopName = 'PhotoReviewTests'
)

<#
.SYNOPSIS
    Runs `dotnet test` (including Category=UI real-WPF tests) on a private, non-interactive Win32 desktop
    so no window is ever visible on the caller's desktop and nothing steals focus.

.DESCRIPTION
    Windows supports multiple "desktops" per window station (see MSDN "Window Stations and Desktops").
    Each process's top-level windows belong to whichever desktop its threads were created on. A window
    created on desktop B literally cannot be seen, focused, or enumerated from desktop A -- this is an
    OS-level isolation boundary, not a hack like moving windows off-screen. This script:

      1. CreateDesktop()s a new, private desktop ("PhotoReviewTests" by default) on the caller's own
         window station.
      2. CreateProcess()es `dotnet test ...` with STARTUPINFO.lpDesktop pointing at that desktop's name.
         Every window a child process (dotnet -> vstest.console -> testhost -> the app's own STA/WPF
         threads) creates inherits the desktop of the thread that created it, which is ultimately the
         process's initial thread -- so the whole test host tree, and any real WPF Window/MessageBox it
         pops, renders on the hidden desktop instead of the interactive one.
      3. Waits for the process to exit, relays its stdout/stderr (redirected to a log file, then tailed
         to the console) and exit code.
      4. CloseDesktop()s the private desktop in a finally block (also on Ctrl+C), so nothing is leaked
         even if `dotnet test` itself hangs and is killed.

    This does not change what runs: Category=UI tests are NOT excluded, and the CI-equivalent filter /
    hang-guard flags are used by default so a hidden run and a normal run exercise the same tests.

.PARAMETER TestArgs
    Extra arguments appended to `dotnet test`, after the CI-equivalent defaults below. Pass your own
    project/solution path and/or --filter to override the defaults entirely, e.g.:
        tools/run-tests-hidden.ps1 tests/PhotoReview.Integration.Tests/PhotoReview.Integration.Tests.csproj -c Release --no-build --filter "Category=UI"

.PARAMETER LogPath
    Where child stdout+stderr is redirected. Defaults to a timestamped file under
    <repo>\TestResults\hidden-desktop-runner\.

.EXAMPLE
    tools/run-tests-hidden.ps1
    Runs the full CI-filter suite (PhotoReview.slnx, Category!=Manual&Category!=Native&Category!=Slow,
    tests/test.runsettings hang guard) on the hidden desktop.

.EXAMPLE
    tools/run-tests-hidden.ps1 tests/PhotoReview.Integration.Tests/PhotoReview.Integration.Tests.csproj -c Release --filter "Category=UI"
    Runs only the UI-category tests in Integration.Tests, hidden.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $resultsDir = Join-Path $root 'TestResults\hidden-desktop-runner'
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $LogPath = Join-Path $resultsDir "run-$stamp.log"
}
else {
    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
}

# Same defaults as the CI gate / tools/verify-all.ps1 (TEST_FILTER in .github/workflows/ci.yml, filter line
# in AGENTS.md > Tests). tests/test.runsettings (the repo-wide hang guard) is picked up automatically via
# tests/Directory.Build.props for every project under tests/, same as any other `dotnet test` invocation.
$defaultArgs = @(
    (Join-Path $root 'PhotoReview.slnx'),
    '-c', 'Release',
    '--filter', 'Category!=Manual&Category!=Native&Category!=Slow',
    '--blame-hang', '--blame-hang-timeout', '120s', '--blame-hang-dump-type', 'none'
)

$effectiveArgs = if ($TestArgs -and $TestArgs.Count -gt 0) { $TestArgs } else { $defaultArgs }

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class HiddenDesktopRunner
{
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100; // not requested; we never switch the interactive desktop
    private const uint GENERIC_ALL = 0x10000000;

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_ALWAYS = 4;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public static IntPtr CreateHiddenDesktop(string name)
    {
        uint access = DESKTOP_CREATEWINDOW | DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | GENERIC_ALL;
        IntPtr h = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, access, IntPtr.Zero);
        if (h == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateDesktop failed, Win32 error " + Marshal.GetLastWin32Error());
        }
        return h;
    }

    public static PROCESS_INFORMATION StartOnDesktop(string desktopName, string commandLine, string workingDirectory, IntPtr stdOutHandle, IntPtr stdErrHandle)
    {
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.lpDesktop = desktopName;
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdOutput = stdOutHandle;
        si.hStdError = stdErrHandle;
        si.hStdInput = IntPtr.Zero;

        var sb = new StringBuilder(commandLine);
        PROCESS_INFORMATION pi;
        bool ok = CreateProcess(
            null,
            sb,
            IntPtr.Zero,
            IntPtr.Zero,
            true, // inherit handles, so the child's stdout/stderr go to our redirected file
            CREATE_UNICODE_ENVIRONMENT,
            IntPtr.Zero,
            workingDirectory,
            ref si,
            out pi);

        if (!ok)
        {
            throw new InvalidOperationException("CreateProcess failed, Win32 error " + Marshal.GetLastWin32Error());
        }
        return pi;
    }
}
'@

$desktopHandle = [IntPtr]::Zero
$processInfo = $null
$exitCode = 1

# SECURITY_ATTRIBUTES with bInheritHandle = true, so the child process (created with bInheritHandles=true)
# can inherit this file handle as its stdout/stderr.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InheritableFile
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
'@ -ErrorAction SilentlyContinue

try {
    Write-Host "Creating private desktop '$DesktopName'..." -ForegroundColor Cyan
    $desktopHandle = [HiddenDesktopRunner]::CreateHiddenDesktop($DesktopName)

    $sa = New-Object InheritableFile+SECURITY_ATTRIBUTES
    $sa.nLength = [Runtime.InteropServices.Marshal]::SizeOf($sa)
    $sa.bInheritHandle = $true
    $sa.lpSecurityDescriptor = [IntPtr]::Zero

    $logFileHandle = [InheritableFile]::CreateFile($LogPath, [HiddenDesktopRunner]::GENERIC_WRITE, ([HiddenDesktopRunner]::FILE_SHARE_READ -bor [HiddenDesktopRunner]::FILE_SHARE_WRITE), [ref]$sa, [HiddenDesktopRunner]::OPEN_ALWAYS, [HiddenDesktopRunner]::FILE_ATTRIBUTE_NORMAL, [IntPtr]::Zero)
    if ($logFileHandle -eq [IntPtr]::Zero -or $logFileHandle -eq [IntPtr]::new(-1)) {
        throw "Failed to open log file '$LogPath' (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
    }

    $dotnetExe = (Get-Command dotnet.exe -ErrorAction SilentlyContinue)
    if (-not $dotnetExe) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop) }
    $dotnetPath = $dotnetExe.Source

    # Build a properly quoted command line for CreateProcess (CommandLineToArgvW quoting rules).
    $quotedParts = @($dotnetPath, 'test') + $effectiveArgs | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }
    $commandLine = [string]::Join(' ', $quotedParts)

    Write-Host "Running on hidden desktop: $commandLine" -ForegroundColor Cyan
    Write-Host "Log: $LogPath" -ForegroundColor Cyan

    $processInfo = [HiddenDesktopRunner]::StartOnDesktop($DesktopName, $commandLine, $root, $logFileHandle, $logFileHandle)
    [HiddenDesktopRunner]::CloseHandle($logFileHandle) | Out-Null

    Write-Host "Started dotnet test as PID $($processInfo.dwProcessId) on desktop '$DesktopName'. Waiting..." -ForegroundColor Cyan

    # Poll instead of a single infinite WaitForSingleObject so Ctrl+C (SIGINT) is observed promptly and
    # the finally block (CloseDesktop / process cleanup) still runs.
    $handle = $processInfo.hProcess
    while ($true) {
        $wait = [HiddenDesktopRunner]::WaitForSingleObject($handle, 2000)
        if ($wait -eq 0) { break } # WAIT_OBJECT_0: process exited
    }

    $code = 0
    [void][HiddenDesktopRunner]::GetExitCodeProcess($handle, [ref]$code)
    $exitCode = $code
}
finally {
    if ($processInfo -and $processInfo.hProcess -ne [IntPtr]::Zero) {
        [HiddenDesktopRunner]::CloseHandle($processInfo.hProcess) | Out-Null
    }
    if ($processInfo -and $processInfo.hThread -ne [IntPtr]::Zero) {
        [HiddenDesktopRunner]::CloseHandle($processInfo.hThread) | Out-Null
    }
    if ($desktopHandle -ne [IntPtr]::Zero) {
        [HiddenDesktopRunner]::CloseDesktop($desktopHandle) | Out-Null
        Write-Host "Closed private desktop '$DesktopName'." -ForegroundColor Cyan
    }
}

Write-Host "`n=== Tail of $LogPath ===" -ForegroundColor Cyan
if (Test-Path -LiteralPath $LogPath) {
    Get-Content -LiteralPath $LogPath -Tail 200
}
Write-Host "=== dotnet test exit code: $exitCode ===" -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })

exit $exitCode
