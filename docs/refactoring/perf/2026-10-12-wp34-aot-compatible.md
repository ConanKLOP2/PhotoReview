# WP-34 - IsAotCompatible toan bo + cong publish AOT: so do (2026-10-12)

May: PC dev (may ban). Quyet dinh: [NOWPF-WP34](../decisions/NOWPF-WP34-AOT-COMPATIBLE.md).

| Muc | Truoc | Sau | Ghi chu |
|---|---|---|---|
| Publish AOT shell (`tools/check-aot-warnings.ps1`, `-p:ShellAot=true`, Release, win-x64) | 0 canh bao IL (sau WP-33) nhung chua do ca do thi phu thuoc co analyzer | **0** canh bao IL2xxx/IL3xxx/ILC, exe 11 MB | cong chinh thuc; chi chay khi co MSVC |
| Analyzer AOT/trim (IsAotCompatible) | 4 project (App.Shared, Shell.Interop/Rendering/Win32) | 10 project (them Core, Imaging, Imaging.Raw, Imaging.LibRaw, Imaging.TurboJpeg, Platform.Windows) | chay tren net10.0-windows: probe `Type.GetType`/`Activator` bi IL2057/IL2070 va lam hong build Release |
| `[DllImport]` trong `src/` | 53 (IL stub tao luc chay) | 0 (`[LibraryImport]` source-generated) | khong tinh test |
| Test (loc CI) | | Architecture 154, Core 3154, Imaging 4181, Shell 1020, Shell.Integration 139, App 2406, Integration 1268 xanh | 3 that bai thoang qua duoi tai (UI timing) chay lai rieng xanh |

Khong do A/B thoi gian chay: khai bao P/Invoke thuan (tranh IL stub), khong doi duong nong.
