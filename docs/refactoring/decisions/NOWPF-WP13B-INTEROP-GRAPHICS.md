---
id: NOWPF-WP13B
order: 342
summary: |-
  WP-13b (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): interop D3D11/DXGI/D2D1/DWrite + COM shell (IFileOpenDialog, IShellItem, IDataObject, IDropTarget) bằng [GeneratedComInterface], số slot vtable ghi trong comment từng method; D2DSmokeTests (WARP offscreen, vẽ + đọc lại pixel, swap chain composition, DWrite tiếng Việt) + smoke COM shell; kiểm chứng đổi GUID/đổi thứ tự slot đều bị bắt. Quy ước then chốt: mọi method [PreserveSig] khai báo đúng kiểu trả C++, tham số ra kiểu interface là out nint + ComInterop.Wrap (FinalRelease thật).
---

# NOWPF-WP13B-INTEROP-GRAPHICS - interop đồ hoạ và shell COM (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (mục 1-3, 8), thẻ WP-13b trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md). Hợp đồng v1 không đổi.

## 1. Đã tạo (`src/PhotoReview.Shell.Interop`)

| File | Nội dung |
|---|---|
| `Graphics/Guids.cs` | `GraphicsGuids`: IID dạng const string (cho `[Guid]`) + `Guid` tĩnh cho CreateFactory/QueryInterface/GetBuffer |
| `Graphics/ComInterop.cs` | `Wrap<T>(nint)` (wrapper UniqueInstance, chiếm 1 ref), `QueryInterface`, `Release(object)` (= `ComObject.FinalRelease`, trả ref NGAY), `Check(hr)` |
| `Graphics/D3D11.cs` | `ID3D11Device` (42 slot, thật: CreateTexture2D[5], GetFeatureLevel[37], GetDeviceRemovedReason[39], GetImmediateContext[40]); `D3D11.CreateDevice` (Hardware/WARP), `ClearStateAndFlush` (vtable thô slot 110/111 của context) |
| `Graphics/Dxgi.cs` | IDXGIObject/DeviceSubObject/Adapter/Device/Device1/Surface/Factory..Factory2 (CreateSwapChainForHwnd[15], ForComposition[24])/SwapChain..SwapChain2 (Present[8], GetBuffer[9], ResizeBuffers[13], Present1[22], SetMaximumFrameLatency[31], GetFrameLatencyWaitableObject[33]); `Dxgi.CreateDxgiFactory2` |
| `Graphics/D2D1.cs` | ID2D1Factory1 (CreateDevice[17]), Device, DeviceContext (CreateBitmapEx[57], CreateBitmapFromDxgiSurface[62], SetTarget[74], DrawBitmapEx[85]...), RenderTarget (CreateSolidColorBrush[8], FillRectangle[17], DrawTextLayout[28], SetTransform[30], PushAxisAlignedClip[45], Clear[47], BeginDraw[48], EndDraw[49]...), Bitmap/Bitmap1 (CopyFromBitmap, Map/Unmap), Brush/SolidColorBrush; `D2D1.CreateFactory` |
| `Graphics/DWrite.cs` | IDWriteFactory (CreateTextFormat[15], CreateTextLayout[18]), IDWriteTextFormat (66 slot tới [27]), IDWriteTextLayout (SetMaxWidth/SetFontSize.. , GetLineMetrics[59], GetMetrics[60], HitTestPoint[64], HitTestTextPosition[65]); `DWrite.CreateFactory` |
| `Shell/IFileOpenDialog.cs` | IModalWindow, IFileDialog (SetOptions[9] .. SetFilter[26]), IFileOpenDialog (GetResults[27], GetSelectedItems[28]), IShellItem, IShellItemArray, `ShellNative` (SHCreateItemFromParsingName, CoCreateInstance(FileOpenDialog), SHCreateDataObject), `ShellGuids` |
| `Shell/IDataObject.cs` | IDataObject (GetData[3], QueryGetData[5], SetData[7]), FORMATETC/STGMEDIUM, `OleNative.ReleaseStgMedium` |
| `Shell/IDropTarget.cs` | IDropTarget (DragEnter[3], DragOver[4], DragLeave[5], Drop[6]), POINTL, DropEffect |

Mọi method có comment `[n]` = slot vtable; slot không dùng là `ReservedNN...` (giữ chỗ, không gọi, không đổi thứ tự).

## 2. Quy ước ABI đã chốt (WP-15/17/19 phải theo)

1. **Mọi method `[PreserveSig]`** và khai báo đúng kiểu trả của C++: HRESULT -> `int`, `void` -> `void`, struct 8 byte (`D2D1_SIZE_F/U`, `D2D1_PIXEL_FORMAT`) trả thẳng struct, struct 16 byte (`D2D1_COLOR_F` của `GetColor`) = con trỏ ẩn nên khai báo `void GetColor(D2dColorF*)`. Không để generator "bỏ HRESULT" cho hàm void: rax rác bị coi là HRESULT và ném giả.
2. **Tham số RA kiểu interface luôn là `out nint`**, bọc bằng `ComInterop.Wrap<T>` và trả bằng `ComInterop.Release`. Đã đo: wrapper do marshaller sinh cho `out IFoo` nằm trong cache chung, `FinalRelease` không trả ref (back buffer còn ref -> `ResizeBuffers` = `DXGI_ERROR_INVALID_CALL`). Tham số VÀO kiểu interface dùng kiểu interface thường.
3. `D3D11.ClearStateAndFlush(device)` sau khi bỏ target D2D và trước `ResizeBuffers` (khuyến nghị của mẫu D2D 1.1; context không khai báo đủ ~115 slot, chỉ vtable thô).
4. Chuỗi UTF-16 (`StringMarshalling.Utf16`); COM shell (file dialog) chỉ dùng trên luồng STA.
5. Tên khác C++ khi trùng overload: `CreateBitmapEx` (ID2D1DeviceContext[57]), `DrawBitmapEx` ([85], interpolation đầy đủ) so với `CreateBitmap`/`DrawBitmap` của RenderTarget.

## 3. Kiểm chứng

`tests/PhotoReview.Shell.Tests/Graphics/` (`Category=Native`, ~0,5 s, không cửa sổ/GPU thật):

- `D2DSmokeTests` (14): WARP device + feature level + removed reason; bitmap 4x4 vẽ Clear/FillRectangle và đọc lại pixel (CopyFromBitmap + Map); `DrawBitmapEx` co giãn; bitmap từ texture D3D qua `IDXGISurface`; swap chain composition (waitable object, frame latency, Present/Present1, ResizeBuffers sau khi trả ref); DWrite "Tiếng Việt" đo > 0, wrap theo `SetMaxWidth`, HitTest, vẽ bằng D2D ra pixel tối; brush/DrawLine/DrawRectangle/clip/CopyFromMemory; state setter round-trip; layout struct = kích thước Win32 SDK; `ComInterop.Release` trả hết ref.
- `ShellComInteropGraphicsTests` (4, STA): IShellItem từ đường dẫn + parent; cấu hình IFileOpenDialog không `Show`; IDataObject SetData/GetData Unicode text; IDropTarget gọi qua con trỏ hàm thô theo **số slot literal** 3..6.

Mutation check (đã chạy, khôi phục sau mỗi lần): đổi GUID ID2D1DeviceContext -> 8/16 test đỏ; đổi chỗ FillRectangle[17]/slot 18 -> pixel sai; đổi chỗ SwapChain Present/GetBuffer -> test swap chain đỏ; đổi chỗ DWrite GetLineMetrics/GetMetrics -> test host crash (bắt được); đổi chỗ IDropTarget DragOver/DragLeave -> đỏ; dời GetFeatureLevel[37] -> đỏ.

## 4. Chưa có test chạy thật (giữ slot đúng theo header, cần kiểm khi dùng lần đầu)

`IDXGIFactory2.CreateSwapChainForHwnd[15]` (cần cửa sổ; cùng chuỗi vtable với [24] đã kiểm), `IDXGIDevice1.*`, `IFileDialog.GetResult/Close/AddPlace/Advise`, `IFileOpenDialog.GetResults/GetSelectedItems`, `IShellItemArray.*` (cần `Show`), `ID2D1Bitmap1.GetSurface[13]`, `IDWriteFactory.GetSystemFontCollection[3]`, `IDWriteTextLayout.SetFontFamilyName/SetFontWeight/SetFontStyle`. WP-15/WP-19 phải bổ sung test khi bắt đầu dùng (cửa sổ thật trong Shell.Integration.Tests).

## 5. Lệch khỏi thẻ

- Thêm `Graphics/ComInterop.cs` (ngoài danh sách file của thẻ) cho quy ước sở hữu COM.
- `ID3D11DeviceContext` không khai báo thành interface (chỉ `GetImmediateContext` + vtable thô cho ClearState/Flush): renderer không cần, tránh ~115 slot không kiểm chứng.
- IID `ID2D1Image`, `ID2D1Bitmap1`, `ID2D1DeviceContext` lấy từ header mingw-w64 (không có Windows SDK trên máy này); được test QueryInterface thật xác nhận.
- `ShellNative`/`OleNative` có `CoCreateInstance`, `SHCreateItemFromParsingName`, `SHCreateDataObject`, `ReleaseStgMedium` riêng vì các hàm này gắn chặt với interface COM; không trùng `Win32/*` của WP-13a (đã kiểm).
