<#
.SYNOPSIS
  Runs ONE command line on a private (non-interactive) desktop, so any window it (or its children) creates never appears on
  the user's desktop and cannot take focus. Generic cousin of tools/run-tests-hidden.ps1 (same CreateDesktop/CreateProcess
  technique). Used by tools/diag/aot-window-check.ps1 and the startup probe in the AOT local trial.
  Only the process it started is ever terminated (on timeout); exits with the child's exit code (124 on timeout).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CommandLine,
    [Parameter(Mandatory)][string]$LogPath,
    [string]$DesktopName = ('AotHidden' + [guid]::NewGuid().ToString('N').Substring(0, 8)),
    [string]$WorkingDirectory = (Get-Location).Path,
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AotHidden
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateDesktop(string n, IntPtr d, IntPtr m, uint f, uint a, IntPtr s);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string a, StringBuilder c, IntPtr pa, IntPtr ta, bool inh, uint fl, IntPtr env, string cd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint c);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint c);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string n, uint acc, uint share, ref SECURITY_ATTRIBUTES sa, uint disp, uint attr, IntPtr t);

    public static int Run(string desktop, string cmd, string cwd, string log, int timeoutMs)
    {
        IntPtr d = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 | 0x2 | 0x40 | 0x80, IntPtr.Zero);
        if (d == IntPtr.Zero) throw new InvalidOperationException("CreateDesktop " + Marshal.GetLastWin32Error());
        var sa = new SECURITY_ATTRIBUTES(); sa.nLength = Marshal.SizeOf(sa); sa.bInheritHandle = true;
        IntPtr f = CreateFile(log, 0x40000000, 3, ref sa, 2, 0x80, IntPtr.Zero);
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.lpDesktop = desktop; si.dwFlags = 0x100; si.hStdOutput = f; si.hStdError = f;
        PROCESS_INFORMATION pi;
        try
        {
            if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, true, 0x400, IntPtr.Zero, cwd, ref si, out pi))
                throw new InvalidOperationException("CreateProcess " + Marshal.GetLastWin32Error());
            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0) { TerminateProcess(pi.hProcess, 124); return 124; }
                uint code; GetExitCodeProcess(pi.hProcess, out code); return (int)code;
            }
            finally { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
        }
        finally { CloseHandle(f); CloseDesktop(d); }
    }
}
'@
exit ([AotHidden]::Run($DesktopName, $CommandLine, $WorkingDirectory, $LogPath, $TimeoutSec * 1000))
