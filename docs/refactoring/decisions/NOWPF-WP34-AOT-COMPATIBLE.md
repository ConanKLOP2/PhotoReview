---
id: NOWPF-WP34
order: 1000
summary: |-
  WP-34: IsAotCompatible cho Core/Imaging*/Platform.Windows (+App.Shared, Shell.*); 0 DllImport trong src; cong publish AOT 0 canh bao.
---

# NOWPF-WP34 - IsAotCompatible toan bo + cong publish AOT (2026-10-12)

The: WP-34 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiep theo [NOWPF-WP33](NOWPF-WP33-COM-SHELL.md) (COM shell) va spike
[NOWPF-WP32](NOWPF-WP32-AOT-SPIKE.md). So do: [perf/2026-10-12-wp34-aot-compatible.md](../perf/2026-10-12-wp34-aot-compatible.md).

## 1. Da lam

| Muc | Ket qua |
|---|---|
| `IsAotCompatible=true` | Them cho `Core`, `Imaging`, `Imaging.Raw`, `Imaging.LibRaw`, `Imaging.TurboJpeg`, `Platform.Windows` (da co: `App.Shared`, `Shell.Interop/Rendering/Win32`). Khong bat cho phia WPF (`App`, `App.WpfWindows`, `Imaging.Wpf`, `Shell.WpfBridge`: WPF khong AOT) |
| Analyzer IL2xxx/IL3xxx | **Chay duoc** tren `net10.0-windows` voi SDK hien tai (kiem chung: mot `Type.GetType(string)` + `Activator.CreateInstance` tam trong Platform.Windows bi bao IL2057/IL2070 va lam hong build Release). Ghi chu "NETSDK1210 khong chay analyzer" o WP-12/WP-33 khong con dung. Build Release 0 canh bao => 0 IL o 10 project AOT |
| `[DllImport]` -> `[LibraryImport]` | 53 khai bao: Platform.Windows 15 (`WindowsDisplayClock` 8, `InstanceForwardClient` 2, `WindowsDarkTitleBar` 2, `WindowsDwmCloak`, `WindowsMemoryProbe`, `WindowsNaturalComparer`), Core 2 (`PhysicalJournalCompactionFiles`), TurboJpeg 8, LibRaw 23, App 3 (`WindowPlacementService` 2, `MainWindow` 1), WpfBridge 2. `src/` khong con `[DllImport]` nao |
| Reflection | Khong con API khong an toan AOT trong 10 project (grep + luat kiem tra moi). `LibRawAvailability.RequiredExports` doc entry point bang reflection tren `[DllImport]` -> thay bang danh sach tinh `LibRawNativeMethods.EntryPoints` (test quet nguon doi chieu) |
| Cong 0 canh bao | `tools/check-aot-warnings.ps1`: publish `PhotoReview.exe` voi `-p:ShellAot=true`, dem canh bao IL2xxx/IL3xxx/ILC; chi chay khi co MSVC (`-RequireToolchain` de bat buoc); tu them `Visual Studio\Installer` vao PATH. CI se goi o WP-36 |

### Chi tiet hanh vi giu nguyen

- `SetLastError`, `ExactSpelling` (EntryPoint ghi ro), `CharSet.Unicode` -> `StringMarshalling.Utf16`, `[return: MarshalAs(Bool)]` (BOOL 4 byte), `CallingConvention.Cdecl` ->
  `[UnmanagedCallConv(typeof(CallConvCdecl))]`, `DefaultDllImportSearchPaths` (assembly + System32) giu nguyen; cac `catch (DllNotFoundException/EntryPointNotFoundException)` van dung.
- `MonitorInfoEx` (`WindowsDisplayClock`): `ByValTStr` khong blittable -> `fixed char Device[32]` (cung bo cuc 104 byte, doc den NUL dau tien giong ByValTStr).
- `WindowPlacement` (class, schema tep cu + test): cac P/Invoke di qua struct `NativeWindowPlacement` sao chep vao/ra quanh moi lan goi (nhu `[In, Out]` cu).
- `SafeHandle` tham so (`SafeLibRawHandle`, `SafeTurboJpegHandle`, `SafeFileHandle` tra ve) va `delegate` (`ProgressCallback`) do generator sinh ma marshal tuong duong.

## 2. Luat kiem tra moi (`AotCompatibilityRulesTests`, Architecture)

`IsAotCompatible` bat o 10 project; assembly da build khong co `[ComImport]`/P-Invoke ngoai LibraryImport/`Microsoft.CSharp`; cam `[DllImport]` o toan `src/`;
cam `Activator.CreateInstance`, `Assembly.Load*`, `Type.GetType`, `MakeGeneric*`, `dynamic`, `Marshal.GetObjectForIUnknown`..., `JsonSerializer` khong co context
source-gen o 10 project; script cong giu hop dong (`-p:ShellAot=true`, 0 canh bao, vswhere). Moi bo quet co test chung (mau bi cam / mau an toan).

## 3. Khong lam / de lead biet

1. `PublishAot` van do WP-36 (csproj shell, CI, verify-release); gói nay chi them script cong va luat.
2. `TestSupport.Windows` va test tu khai bao `DllImport` rieng (ngoai `src/`): khong dong.
3. Quyet dinh de xuat: **bo `[DllImport]` hoan toan khoi `src/`** (khong allowlist). Hoan nguyen: dua lai attribute o file bi anh huong.
