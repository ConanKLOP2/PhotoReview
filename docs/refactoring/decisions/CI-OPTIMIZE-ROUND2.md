---
id: CI-OPTIMIZE-ROUND2
order: 1270
summary: |-
  CI vòng 2: 6 job test + compile-gate (build của job test bỏ analyzer, build bằng solution filter), tường ~400 s xuống ~220 s (CHƯA XÁC NHẬN, 2026-10-14).
---

# CI-OPTIMIZE-ROUND2 - giảm thời gian tường của ci.yml lần 2 (2026-10-14)

Trạng thái: **CHƯA XÁC NHẬN** (đổi workflow CI cần chủ repo xem; PR không tự bật auto-merge). Hoàn tác: revert PR (ci.yml,
`tools/ci/*.slnf`, `TestFilterDriftTests.cs`, 3 câu trong README/TESTING/architecture). Nối tiếp [CI-PARALLEL-TEST-JOBS](CI-PARALLEL-TEST-JOBS.md).

## 1. Kết quả

Tường = từ lúc job đầu tiên bắt đầu đến lúc `build-test-publish` xong (windows-latest, 2 vCPU).

| Cấu hình | Run | Tường | Ghi chú |
|---|---|---|---|
| Nền (master sau #502, 5 run) | 38424862694, 38435237477, 38436308425, 38436511490, 38437634780 | 395-421 s | `test-shell` 394-421 s quyết định |
| V1: chỉ đổi vòng `foreach build` thành một `dotnet build x.slnf` | 38430243581 | 388 s | build 118/82/71 s so với 123-139/78-117/52-63 s: KHÔNG lợi (CPU-bound trên 2 vCPU) |
| V2: V1 + Shell.WpfBridge.Tests sang test-core | 38430286743 | 338 s | test-shell 326 s (đỏ do flaky), core 312 s |
| V3: V2 + Shell.Tests thành 2 shard | 38430800376 | 331 s | core 316 s thành điểm nghẽn |
| V5: 6 job test + build-publish chỉ build 2 exe | 38432813132 | 249 s | mọi job <= 244 s (test-shell đỏ do flaky) |
| V6 / V10: V5 + `-p:RunAnalyzers=false` ở build của job test | 38432884099 / 38434088843 | job test 155-219 s | build job test 29-48 s so với 61-100 s |
| V11: V10 + job `compile-gate` + NuGet trên D: (lần 1 nguội / lần 2 ấm) | 38435760833 | ~230 s / 205 s | lần 2 đỏ do flaky App (xem mục 4) |
| **Cuối, PR #508 (lần 1 nguội / lần 2 ấm, cả hai xanh)** | 38439566083 | **219 s / 222 s** | bảng dưới |

Từng job, PR #508 (giây, lần 1 / lần 2) so với nền (master run 38437634780):

| Job | Nền | Sau | Việc |
|---|---|---|---|
| `test-core` | 337 | 166 / 143 | Core, Imaging (build nhẹ) |
| `test-app` | (trong test-core) | 139 / 181 | Architecture, App, Shell.WpfBridge (build nặng WPF) |
| `test-shell` | 409 | 144 / 199 | Shell.Tests shard B + Shell.Integration |
| `test-shell-settings` | (trong test-shell) | 198 / 208 | Shell.Tests shard A (namespace `Dialogs.SettingsUi`) |
| `test-integration` | 243 | 201 / 203 | Integration.Tests bước chính |
| `test-integration-slow` | (trong test-integration) | 170 / 160 | bước Integration+Slow của Integration.Tests |
| `compile-gate` | (trong build-publish) | 194 / 197 | cả `PhotoReview.slnx`, analyzer BẬT, warning = lỗi |
| `build-publish` | 240 | 105 / 117 | build 2 exe (analyzer bật) + publish + verify |

Số test từng project bằng master cùng commit (a117a5b4, run 38437634780): Architecture 160 (159 + 1 luật mới), Core 3202, Imaging 4181, App 2583,
Integration 1347 (gồm 17 Slow chạy ở job riêng), Shell 253 + 1370 = 1623 (hai shard bù nhau), Shell.Integration 151, WpfBridge 39.

## 2. Cái gì giữ lại và vì sao

1. **`-p:RunAnalyzers=false` ở bước build của 6 job test** (đòn bẩy lớn nhất): build của job test còn 24-46 s (từ 61-100 s); build CẢ solution không
   analyzer chỉ 45 s so với 126 s có analyzer (thí nghiệm artifact, run 38437459182). Analyzer không đổi mã sinh ra (source generator như
   Localization.Generator VẪN chạy), nên kết quả test như cũ. Cổng chất lượng KHÔNG mất: job mới `compile-gate` build cả solution (src + tests +
   tools) với analyzer bật và warning = lỗi, y như bước `Build Release` cũ của build-publish. Luật `AnalyzersAreSkippedOnlyWhereCompileGateStillRunsThem`
   canh: cờ chỉ nằm ở dòng build của job `test-*`, không ở `compile-gate`/`build-publish`/props.
2. **Một `dotnet build tools/ci/<job>.slnf`** thay vòng build tuần tự: không nhanh hơn trên CI (V1), nhưng gọn, và cho phép luật
   "job nào test project nào thì build project đó" (đọc slnf). Bước build giữ `shell: powershell`: LibRaw gọi `powershell fetch-libraw.ps1`
   và dưới pwsh 7 script đó không tìm thấy `Get-FileHash` (PSModulePath kế thừa) và chỉ cảnh báo.
3. **Cân lại job**: Shell.WpfBridge.Tests sang `test-app` (vết build WPF của Architecture đã phủ hết phụ thuộc của nó, thêm ~12 s test) nên
   `test-shell` không còn build WPF/XAML; Shell.Tests tách 2 shard theo namespace bằng filter bù nhau (`FullyQualifiedName~X` và `!~X`,
   luật CI-PARALLEL kiểm cặp nên không mất/trùng test); bước Integration+Slow của Integration.Tests (68-89 s) sang job riêng.
4. **build-publish chỉ build `tools/ci/publish.slnf`** (2 exe + closure): 105-157 s so với 169-262 s; phần còn lại của solution do `compile-gate` phủ.
5. **`NUGET_PACKAGES=D:\nuget-packages`** (ổ D: là ổ workspace, nhanh hơn ổ C: profile): restore trần 14-18 s (V8) so với 33-63 s khi bỏ cache
   trên C: (V7); cache trúng trên D: = 5-9 s + restore 11-13 s, ngang restore trần, nên giữ bước cache NuGet (đỡ phụ thuộc nuget.org) nhưng
   đặt cả hai trên D:. So với C: (cache 10-36 s + restore 10-13 s) lợi ~5-10 s/job.
6. **Cache `native/x64/turbojpeg.dll`** (key theo hash pin + script): bước `Fetch native binaries` (tải turbojpeg từ nuget.org) trung bình 3-6 s
   nhưng có outlier 106-132 s ở 6 trong ~60 lượt job (V6, V7, V8, V10, V11). Có cache thì chỉ kiểm hash; script vẫn xác minh SHA-256 pin.
   LibRaw đã nằm trong repo nên không tải.

## 3. Ý đã thử và LOẠI

| Ý | Số đo | Lý do loại |
|---|---|---|
| `-p:EnforceCodeStyleInBuild=false` riêng | V9: build 49-88 s so với V5 61-100 s (trong phương sai) | không lợi; `RunAnalyzers=false` đã bao hết |
| Bỏ bước cache NuGet, restore trần (ổ C:) | V7: restore 33-63 s | chậm hơn cache (24-26 s) |
| Chia sẻ build qua artifact (2c): 1 job build rồi các job test `--no-build` | thí nghiệm 38437459182: upload 8 s, download 5 s (272 MB, 1408 file), build cả solution không analyzer 45 s | tải/giải nén rẻ, nhưng nối tiếp build -> test cộng ~100 s (checkout, cache, restore, build, upload) vào mọi job, trong khi build riêng từng job nay chỉ 24-46 s chạy song song; đường găng ước ~235 s so với ~220 s |
| Shard Integration.Tests bước chính (~110 s) thành 2 | ước tính | tường khi đó bị `compile-gate` (172-197 s) chặn: lợi ~15 s đổi lấy +1 job runner |
| Tắt Defender / loại trừ thư mục (IO nhanh hơn) | không thử | là đổi cài đặt bảo mật của runner: để chủ repo quyết |
| Restore chỉ theo slnf của job | không thử | cache NuGet lưu từ job đầu: restore một phần sẽ lưu cache thiếu cho mọi run sau (lý do cũ trong ci.yml) |
| Bỏ `fetch-depth: 0` ở job test | không thử | checkout chỉ ~8 s; phiên bản dựa vào lịch sử git |

## 4. Rủi ro

- Số job Windows mỗi run: 10 (repo-checks, build-publish, compile-gate, 6 test, publish-aot) so với 7 trước: nhiều PR cùng lúc có thể xếp hàng
  nếu gói GitHub giới hạn 20 job đồng thời; tổng phút runner tăng ~35% (~1500 s so với ~1100 s mỗi run) để đổi lấy tường giảm ~45%.
- Hai flaky đã thấy, KHÔNG do thay đổi này (đã gặp trước khi tách): `NativeDialogChromeTests.Benchmark_HasADarkBackground_AndReadableText` (shell, đỏ ở
  ~4 trong 8 run V2-V11) và `DarkScrollBarRenderingTests.ScrollViewer_WithAppDarkTheme_RendersDarkThumbAndCorner` ("render timed out" 15 s, 1 lần ở test-app).
  Có 6 job nên `gh run rerun --failed` chỉ chạy lại job đỏ (rẻ hơn trước).
- Job test dùng binary biên dịch KHÔNG analyzer; nếu ai đó thêm test phụ thuộc đầu ra analyzer (không có hôm nay) thì luật phải được xem lại.
- Cache D: và cache turbojpeg theo nhánh: lượt đầu của nhánh/PR là nguội (restore ~15-19 s, tải turbojpeg); master giữ cache cho các PR sau.

## 5. Việc cho chủ repo

Required checks vẫn `build-test-publish` + `repo-checks`, tên không đổi: không cần sửa gì. Job mới (`test-app`, `test-shell-settings`, `test-integration-slow`,
`compile-gate`) đã nằm trong `needs` của `build-test-publish`.
