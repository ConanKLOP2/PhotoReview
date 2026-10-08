<#
.SYNOPSIS
  Real-monitor F11 transition rig: launches PhotoReview, toggles fullscreen with a posted F11 and records what the
  compositor actually presents (GDI StretchBlt of the whole monitor at >=30 fps) plus the app window rect/state at the
  same rate. Reports per transition: window-rect sequence and "bad frames" (frames that look like neither the
  frame before the toggle nor the final frame, i.e. any intermediate window state).

.DESCRIPTION
  DIAGNOSTIC TOOL, NOT PART OF THE TEST SUITE. Opens a real window on the user's monitor for ~12 s per scenario.
  - Backs up %LOCALAPPDATA%\PhotoReview\{config.json,window-placement.json} first and restores them byte-identical
    (hash verified) at the end; window-placement.json is rewritten per scenario to start on the wanted monitor/state.
  - Only OS input is PostMessage(F11) to the app's own window handle. No clicks.
  - Captured frames may contain other windows: PNGs are written only to a unique per-run subfolder of -Out (created by the script) and ALWAYS deleted on exit (only files in that subfolder) unless
    -KeepFrames is given. Never commit or share them.

.EXAMPLE
  .\fullscreen-capture.ps1 -Exe C:\...\PhotoReview.App.exe -Photos C:\photos -Out $env:TEMP\f11 -Scenarios DISPLAY4:Max,DISPLAY4:Normal,DISPLAY1:Max,DISPLAY1:Normal
#>
param(
  [Parameter(Mandatory)][string]$Exe,
  [Parameter(Mandatory)][string]$Photos,
  [Parameter(Mandatory)][string]$Out,
  [string[]]$Scenarios = @('DISPLAY4:Max'),     # <device>:<Max|Normal>
  [int]$ThumbLongSide = 480,
  [int]$CaptureMs = 1600,
  [double]$BadThreshold = 10.0,                 # mean abs luma diff (0..255) from BOTH before and final
  [switch]$KeepFrames
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Diagnostics; using System.Drawing; using System.Drawing.Imaging;
using System.Runtime.InteropServices; using System.Text; using System.Threading;
public static class Rig {
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int m);
  [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, int sw, int sh, int rop);
  [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImp; }
  [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER H; }
  [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

  public class Frame { public double T; public byte[] Bits; public string Win; public bool Zoomed, Caption, Visible; }
  public static List<Frame> Frames = new List<Frame>();
  static Thread th; static volatile bool stop; static Stopwatch sw = new Stopwatch();
  public static double KeyT = -1;
  public static int ThumbW, ThumbH;

  public static string StartState(IntPtr hwnd, int mx, int my, int mw, int mh, out bool zoomed, out bool onMonitor) {
    RECT r; GetWindowRect(hwnd, out r); zoomed = IsZoomed(hwnd);
    int cx = (r.L + r.R) / 2, cy = (r.T + r.B) / 2; onMonitor = cx >= mx && cx < mx + mw && cy >= my && cy < my + mh;
    return r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T);
  }
  public static void Init() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
  public static string TaskbarState() { var d = new APPBARDATA(); d.cbSize = Marshal.SizeOf(d); var s = (long)SHAppBarMessage(4, ref d); return (s & 1) != 0 ? "autohide" : "always-visible"; }

  public static void Start(IntPtr hwnd, int x, int y, int w, int h, int tw, int th_) {
    Frames = new List<Frame>(); KeyT = -1; ThumbW = tw; ThumbH = th_; stop = false; sw.Restart();
    th = new Thread(() => Loop(hwnd, x, y, w, h)); th.IsBackground = true; th.Priority = ThreadPriority.AboveNormal; th.Start();
  }
  public static void Key(IntPtr hwnd) {
    KeyT = sw.Elapsed.TotalMilliseconds;
    PostMessage(hwnd, 0x100, (IntPtr)0x7A, IntPtr.Zero); Thread.Sleep(20); PostMessage(hwnd, 0x101, (IntPtr)0x7A, (IntPtr)0xC0000001);
  }
  public static void Stop() { stop = true; th.Join(); }
  public static double Elapsed { get { return sw.Elapsed.TotalMilliseconds; } }

  static void Loop(IntPtr hwnd, int x, int y, int w, int h) {
    IntPtr sdc = GetDC(IntPtr.Zero); IntPtr mdc = CreateCompatibleDC(sdc); IntPtr bmp = CreateCompatibleBitmap(sdc, ThumbW, ThumbH);
    IntPtr old = SelectObject(mdc, bmp); SetStretchBltMode(mdc, 3);
    var bi = new BITMAPINFO(); bi.H.Size = 40; bi.H.Width = ThumbW; bi.H.Height = -ThumbH; bi.H.Planes = 1; bi.H.BitCount = 32;
    while (!stop) {
      double t = sw.Elapsed.TotalMilliseconds;
      StretchBlt(mdc, 0, 0, ThumbW, ThumbH, sdc, x, y, w, h, 0x00CC0020);
      var bits = new byte[ThumbW * ThumbH * 4]; GetDIBits(mdc, bmp, 0, (uint)ThumbH, bits, ref bi, 0);
      RECT r; GetWindowRect(hwnd, out r);
      var f = new Frame { T = t, Bits = bits, Win = r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T), Zoomed = IsZoomed(hwnd), Visible = IsWindowVisible(hwnd), Caption = (GetWindowLong(hwnd, -16) & 0x00C00000) == 0x00C00000 };
      lock (Frames) Frames.Add(f);
    }
    SelectObject(mdc, old); DeleteObject(bmp); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
  }

  static double Dist(byte[] a, byte[] b) {
    // mean abs luma diff on a coarse grid (robust against 1px shifts of the thumbnail)
    int gw = 24, gh = 24; double sum = 0;
    for (int gy = 0; gy < gh; gy++) for (int gx = 0; gx < gw; gx++) {
      double la = Cell(a, gx, gy, gw, gh), lb = Cell(b, gx, gy, gw, gh); sum += Math.Abs(la - lb);
    }
    return sum / (gw * gh);
  }
  static double Cell(byte[] p, int gx, int gy, int gw, int gh) {
    int x0 = gx * ThumbW / gw, x1 = (gx + 1) * ThumbW / gw, y0 = gy * ThumbH / gh, y1 = (gy + 1) * ThumbH / gh; double s = 0; int n = 0;
    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * ThumbW + x) * 4; s += 0.114 * p[i] + 0.587 * p[i + 1] + 0.299 * p[i + 2]; n++; }
    return n == 0 ? 0 : s / n;
  }

  /// returns report text; badIdx receives indexes of bad frames
  public static string Analyze(double thr, out List<int> badIdx, out int fps) {
    badIdx = new List<int>(); var sb = new StringBuilder(); List<Frame> fr; lock (Frames) fr = new List<Frame>(Frames);
    fps = (int)(fr.Count * 1000.0 / Math.Max(1, fr[fr.Count - 1].T - fr[0].T));
    int bi = 0; for (int i = 0; i < fr.Count; i++) if (fr[i].T < KeyT) bi = i;
    var before = fr[bi].Bits; var after = fr[fr.Count - 1].Bits;
    sb.AppendLine(string.Format("  frames={0} (~{1} fps) key at {2:0}ms; before-frame #{3}; before-vs-final dist={4:0.0}", fr.Count, fps, KeyT, bi, Dist(before, after)));
    string lastWin = null; bool lastZ = false, lastC = false, lastV = true;
    for (int i = 0; i < fr.Count; i++) {
      var f = fr[i];
      if (f.Win != lastWin || f.Zoomed != lastZ || f.Caption != lastC || f.Visible != lastV) {
        sb.AppendLine(string.Format("  [win] +{0,6:0.0}ms (key{1,6:+0;-0}) rect={2} zoomed={3} caption={4} visible={5}", f.T, f.T - KeyT, f.Win, f.Zoomed, f.Caption, f.Visible));
        lastWin = f.Win; lastZ = f.Zoomed; lastC = f.Caption; lastV = f.Visible;
      }
    }
    int bad = 0;
    for (int i = bi + 1; i < fr.Count - 1; i++) {
      double dB = Dist(fr[i].Bits, before), dA = Dist(fr[i].Bits, after);
      bool isBad = dB > thr && dA > thr;
      if (isBad) { bad++; badIdx.Add(i); }
      if (isBad || (dB > 1.0 && dA > 1.0)) sb.AppendLine(string.Format("  [frame #{0}] +{1,6:0.0}ms key{2,6:+0;-0} dBefore={3,5:0.0} dFinal={4,5:0.0} {5} win={6}", i, fr[i].T, fr[i].T - KeyT, dB, dA, isBad ? "BAD" : "mid", fr[i].Win));
    }
    sb.AppendLine("  BAD FRAMES: " + bad);
    return sb.ToString();
  }

  public static void SaveSheet(string path, double fromMs, double toMs, int cols) {
    List<Frame> fr; lock (Frames) fr = new List<Frame>(Frames);
    var sel = fr.FindAll(f => f.T >= fromMs && f.T <= toMs); if (sel.Count == 0) return;
    int rows = (sel.Count + cols - 1) / cols; int cw = ThumbW / 2, ch = ThumbH / 2 + 14;
    using (var m = new Bitmap(cols * cw, rows * ch)) using (var g = Graphics.FromImage(m)) using (var font = new Font("Consolas", 8)) {
      g.Clear(Color.Black);
      for (int i = 0; i < sel.Count; i++) {
        using (var b = new Bitmap(ThumbW, ThumbH, PixelFormat.Format32bppRgb)) {
          var d = b.LockBits(new Rectangle(0, 0, ThumbW, ThumbH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
          Marshal.Copy(sel[i].Bits, 0, d.Scan0, sel[i].Bits.Length); b.UnlockBits(d);
          g.DrawImage(b, (i % cols) * cw, (i / cols) * ch + 14, cw, ch - 14);
        }
        g.DrawString(string.Format("{0:0}ms {1}", sel[i].T - KeyT, sel[i].Win), font, Brushes.Yellow, (i % cols) * cw, (i / cols) * ch);
      }
      m.Save(path, ImageFormat.Png);
    }
  }
}
"@
[Rig]::Init()

# Everything this run writes goes to a unique per-run subfolder of -Out; cleanup touches only that subfolder, never
# files the script did not create in the user-given -Out.
$Out = Join-Path $Out ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$appData = Join-Path $env:LOCALAPPDATA 'PhotoReview'
$files = 'config.json', 'window-placement.json'
$backup = Join-Path $Out '_userbackup'
New-Item -ItemType Directory -Force $Out, $backup | Out-Null
$hashes = @{}
foreach ($f in $files) { Copy-Item (Join-Path $appData $f) (Join-Path $backup $f) -Force; $hashes[$f] = (Get-FileHash (Join-Path $appData $f)).Hash }
if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue) { throw 'PhotoReview is already running (the launch would be forwarded to it); close it first.' }

$results = @()
try {
  foreach ($sc in $Scenarios) {
    $dev, $start = $sc.Split(':')
    $scr = [Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -like "*$dev" } | Select-Object -First 1
    if (-not $scr) { throw "no monitor $dev" }
    $b = $scr.Bounds; $wa = $scr.WorkingArea
    $nl = $wa.Left + 120; $nt = $wa.Top + 150; $nr = [Math]::Min($nl + 1200, $wa.Right - 40); $nb = [Math]::Min($nt + 800, $wa.Bottom - 40)
    $placement = @{ Length = 44; Flags = 2; ShowCommand = $(if ($start -eq 'Max') { 3 } else { 1 }); MinPosition = @{X = -1; Y = -1 }; MaxPosition = @{X = -1; Y = -1 };
      NormalPosition = @{Left = $nl; Top = $nt; Right = $nr; Bottom = $nb } } | ConvertTo-Json -Depth 4
    Set-Content (Join-Path $appData 'window-placement.json') $placement -Encoding UTF8
    $long = [Math]::Max($b.Width, $b.Height); $tw = [int]($b.Width * $ThumbLongSide / $long); $th = [int]($b.Height * $ThumbLongSide / $long)

    $app = $null; $h = [IntPtr]::Zero
    for ($try = 1; $try -le 3; $try++) {
      Set-Content (Join-Path $appData 'window-placement.json') $placement -Encoding UTF8
      $app = Start-Process $Exe -ArgumentList "`"$Photos`"" -PassThru
      $h = [IntPtr]::Zero
      for ($i = 0; $i -lt 100 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 200; $app.Refresh(); $h = $app.MainWindowHandle }
      Start-Sleep -Seconds 5      # first image presented, layout settled
      $z = $false; $on = $false
      $st = [Rig]::StartState($h, $b.X, $b.Y, $b.Width, $b.Height, [ref]$z, [ref]$on)
      if ($on -and ($z -eq ($start -eq 'Max'))) { "start ok: $st zoomed=$z"; break }
      "start MISMATCH (try $try): $st zoomed=$z onMonitor=$on - retrying"
      $app.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app.HasExited) { $app.Kill() }
      Start-Sleep -Seconds 1
      if ($try -eq 3) { throw "could not start $sc in the requested state" }
    }
    [Rig]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 700
    foreach ($phase in 'enter', 'exit') {
      [Rig]::Start($h, $b.X, $b.Y, $b.Width, $b.Height, $tw, $th)
      Start-Sleep -Milliseconds 300
      [Rig]::Key($h)
      Start-Sleep -Milliseconds $CaptureMs
      [Rig]::Stop()
      $bad = $null; $fps = 0
      $txt = [Rig]::Analyze($BadThreshold, [ref]$bad, [ref]$fps)
      $name = "$($dev)_$($start)_$phase"
      $head = "== $name (monitor $($b.Width)x$($b.Height) at $($b.X),$($b.Y); taskbar=$([Rig]::TaskbarState()))"
      $head; $txt
      $results += [pscustomobject]@{ Name = $name; Bad = $bad.Count; Fps = $fps }
      [Rig]::SaveSheet((Join-Path $Out "sheet_$name.png"), [Rig]::KeyT - 60, [Rig]::KeyT + 700, 8)
      Start-Sleep -Milliseconds 700
    }
    $app.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app.HasExited) { $app.Kill() }
    Start-Sleep -Milliseconds 500
  }
}
finally {
  foreach ($f in $files) { Copy-Item (Join-Path $backup $f) (Join-Path $appData $f) -Force
    $ok = (Get-FileHash (Join-Path $appData $f)).Hash -eq $hashes[$f]; "restore $f identical=$ok" }
  Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue | Out-Null
}
'--- summary'
$results | Format-Table -AutoSize | Out-String
if (-not $KeepFrames) { Get-ChildItem -LiteralPath $Out -Filter '*.png' -File -ErrorAction SilentlyContinue | Remove-Item -Force; 'frames deleted (per-run folder only)' }
else { "frames kept in $Out" }
