# Performance status and baselines

**Updated:** 2026-10-01. Older detail (D-series tasks, AR04 interleaved gate tables, per-PR perf-night notes, benchmark speed-up step tables,
kinetic-pan harness tables): `git show 1de561c:docs/refactoring/PERF-STATUS.md`.
Fixture F4 = a real folder of portrait 20-30 MP JPEGs (1841-2058 files, 13-19 GB, grows over time); paths are machine-specific (`CLAUDE.local.md`).
Harness: `tools/diag/run-matrix.ps1 -Profile quick|gate|full` (`--perf-session` on the production graph via `AppHost`). Compare only within
the same folder state, same build family and interleaved runs (same-build P95 noise is ~5 ms).

**Do not compare numbers before the AR02c boundary:** the D-series / T66 numbers came from the legacy `--perf-session` graph (no preload, 64 MiB cache).

**Recording a new measurement (avoid merge conflicts across parallel PRs):** put ALL detail (methodology, tables, numbers,
reasoning) in a new file under [`perf/`](perf/), named `<date>-<topic>.md` (ISO date so filenames sort chronologically) --
never write the actual measurement content here. Add exactly ONE new bullet to the list below, at the end. Two PRs
measuring different things on the same day may still collide on that one new bullet, but resolving a one-line insert is
trivial -- it's the growing multi-paragraph sections that caused repeated multi-round merge conflicts on 2026-09-27.

See [`perf/`](perf/) for every measurement pass, oldest first by filename:

- [2026-09-23 -- Baseline AR02e](perf/2026-09-23-baseline-ar02e.md)
- [2026-09-24 -- Perf night (#39-#48)](perf/2026-09-24-perf-night.md)
- [2026-09-26 -- AR16 readability probe](perf/2026-09-26-ar16-readability-probe.md)
- [2026-09-26 -- Whole-folder preload (Q-R17)](perf/2026-09-26-whole-folder-preload-qr17.md)
- [2026-09-26 -- Benchmark harness speed-up](perf/2026-09-26-benchmark-harness-speedup.md)
- [2026-09-26 -- Kinetic pan / glide](perf/2026-09-26-kinetic-pan-glide.md)
- [2026-09-27 -- Image crossfade (PR-D)](perf/2026-09-27-image-crossfade.md)
- [2026-09-27 -- R01/R02/R03/R13 metadata I/O](perf/2026-09-27-r01-r02-r03-r13-metadata-io.md)
- [2026-09-27 -- R04 preload contention / R14 startup sweep](perf/2026-09-27-r04-preload-contention-r14-startup-sweep.md)
- [2026-09-27 -- R06/R07/R11 benchmark/cache misc](perf/2026-09-27-r06-r07-r11-benchmark-cache-misc.md)
- [2026-09-27 -- Q-R29 slow-link simulation (option B)](perf/2026-09-27-qr29-slow-link-sim.md)
- [2026-09-27 -- R15/R16/R17/R18 hash/catalog/drag-drop/diagnostics costs](perf/2026-09-27-r15-r18.md)
- [2026-09-27 -- Q-R29 option C: navigation stat off the UI thread](perf/2026-09-27-qr29-option-c.md)
- [2026-09-28 -- Q-R29 option C-2: ISourceReader seam, preload/viewer bandwidth contention](perf/2026-09-28-qr29-c2-preload-throttle.md)
- [2026-09-28 -- IMG-07 single JPEG header marker walk; title-bar/EXIF-line rebuild cost check (no change)](perf/2026-09-28-img07-single-header-walk.md)
- [2026-09-29 -- RAW-22 cache/preload/RAM estimate](perf/2026-09-29-raw-22-cache-preload.md)
- [2026-09-29 -- RAW-60 decoder benchmark](perf/2026-09-29-raw-60-decoder-bench.md)
- [2026-09-29 -- RAW-31 LibRaw `half_size` evaluation](perf/2026-09-29-raw-31-half-size-evaluation.md)
- [2026-10-06 -- PLAN: bench many configurations for this device (not yet run)](perf/PLAN-device-config-bench.md)
- [2026-10-06 -- S0 noise floor of the device-tuning bench (F4 grew to 28.5 GB; run P95 too noisy at 3 repeats)](perf/2026-10-06-s0-noise-floor.md)
- [2026-10-07 -- Device-tuning bench results S0-S9 plus completion round (S4/S5/S6): no winner, defaults kept](perf/2026-10-07-device-tuning-results.md)
- [2026-10-07 -- TieredPGO=0 confirmation (48 runs): P50 -8.6 %, P95 +8 %, not adopted](perf/2026-10-07-tieredpgo-confirmation.md)
- [2026-10-10 -- Mở ảnh từ Explorer: ảnh đầu, khung trắng lúc khởi động, so sánh v2.0.200-380 (không hồi quy ở v240)](perf/2026-10-10-startup-first-image.md)
- [2026-10-10 -- P-1 "WPF nhanh": ảnh đầu từ Explorer R2R 1127 -> 782 ms, build 1321 -> 915 ms (chưa đạt NW-5 <= 600 ms); thí nghiệm self-contained + composite R2R -140..-160 ms](perf/2026-10-10-p1-startup-in-wpf.md)
- [2026-10-10 -- WP-15 renderer Direct2D: upload 24 MP 35 ms, pan CPU submit P95 0,8 ms, Fit HQC 24 MP GPU 27-41 ms (rủi ro); tải nền 60-100 %, cần đo lại khi máy rảnh](perf/2026-10-10-wp15-d2d-renderer.md)
- [2026-10-10 -- WP-04 cache đĩa bằng WIC (JPEG +17..30 %, PNG ngang/nhanh hơn; mutation 23/24)](perf/2026-10-10-wp04-cache-wic.md)
- [2026-10-10 -- WP-03 WIC decode ra PixelBuffer: 24 MP -> 1920 px, new so với master -1.8 % gộp / +2.9 % lượt yên (trong ngưỡng 5 %)](perf/2026-10-10-wp03-wic-decode.md)
- [2026-10-10 -- WP-05 TurboJpeg ra PixelBuffer (fine-scale vs WIC Fant)](perf/2026-10-10-wp05-native-pixelbuffer.md)
- [2026-10-11 -- WP-09 tách App.Shared: ảnh đầu từ Explorer base vs new +2,9 % median (22 vòng, máy rất bận), nửa thấp -0,3 %; không hồi quy > 5 %](perf/2026-10-11-wp09-app-shared.md)
- [2026-10-12 -- WP-19b App.WpfWindows + WpfBridge: startup bản WPF +1,9 % median (10 vòng xen kẽ, CPU ~22 %); mở cửa sổ phụ qua cầu lần đầu ~470-690 ms, lần sau ~100-215 ms](perf/2026-10-12-wp19b-wpf-bridge.md)
- [2026-10-11 -- WP-12 WIC sang [GeneratedComInterface]: decode P50 -1,7 %/+1,6 % (nền nhiễu +1,9 %), cache JPEG encode -5,7 %, decode -6,2 %, không hồi quy > 5 %; mutation 25/25](perf/2026-10-11-wp12-wic-generated-com.md)
- [2026-10-11 -- WP-17 chữ DirectWrite + overlay: layout miss P50 0,10 ms, hit ~0, khung 5 panel submit P50 0,47 / P95 1,4 ms (GPU thật, tải nền ~23 %); G-OVL lệch 0,017 DIP](perf/2026-10-11-wp17-text-overlay.md)
- [2026-10-12 -- WP-20 startup shell (PhotoReview.exe Win32): first image R2R median vs the WPF app, window-visible milestones](perf/2026-10-12-wp20-startup-shell.md)
- [2026-10-12 -- WP-23 overlay HUD: khung HUD đầy đủ GPU CPU submit P50 0,34 / P95 0,47 ms (tới khi GPU xong P50 1,0 ms); khởi động thêm ~10 ms (~2 %) sau khi tooltip tạo muộn + làm nóng DirectWrite trên pool](perf/2026-10-12-wp23-overlay-hud.md)
- [2026-10-12 -- WP-22 shell input: key->handler/layout 0.14/0.18 ms p50, key->frame ~260 ms (Present(1) blocks, WP-21)](perf/2026-10-12-wp22-input.md)
- [2026-10-12 -- WP-21 trình bày ảnh Win32: Present(1) chặn UI thread P50 222 ms mọi khung -> Present(0) ~1,2 ms; pan 60 Hz P95 5 ms; khung đơn lẻ sau nghỉ 100-450 ms vẫn có thể chặn ~0,5 s (EndDraw, chưa rõ nguyên nhân, CHƯA XÁC NHẬN)](perf/2026-10-12-wp21-presentation.md)
- [2026-10-12 -- WP-25 F11/đa màn hình/DPI trên shell Win32: F11 vào/ra p50 3,4/4,6 ms (WARP, UI thread), không thêm việc vào đường nóng](perf/2026-10-12-wp25-windowing.md)
- [2026-10-12 -- WP-26 thanh cuộn tự vẽ: kéo con trượt trên HWND thật p50 0,044 / p95 0,063 ms, hit-test ~2 us, vẽ ~3 us/khung (không đáng kể)](perf/2026-10-12-wp26-scrollbars.md)
- [2026-10-12 -- WP21-STALL: nguyên nhân gốc khung đơn lẻ bị EndDraw chặn ~0,47 s = GPU Intel rỗi > ~25 ms rồi nộp việc đầu; giữ ấm bằng luồng nền 20 ms trong 5 s sau tương tác: key->frame P95 215-229 -> 8,8 ms, khung >= 100 ms 26,5 -> 0 %, +0,6 % CPU một lõi / ~4 % engine GPU khi tương tác (CHƯA XÁC NHẬN bật mặc định)](perf/2026-10-12-wp21-stall.md)
- [2026-10-12 -- NO-WPF đợt 3 (WP-29 probe xen kẽ trên bản R2R): ảnh đầu shell 463-472 ms vs WPF ~1,2 s, cửa sổ thấy được 356 ms (hụt <= 150), WS -16 %; key->frame P95 trong cache shell 183-220 ms vs WPF 11-17 ms (hụt <= 3 ms: EndDraw treo, WP21-STALL); Settings lần sau R2R median 289 ms (hụt <= 150)](perf/2026-10-12-nowpf-wave3.md)
- [2026-10-12 -- NO-WPF spike S1 (WP-32) Native AOT của shell Win32: publish được, exe 13,3 MB; ảnh đầu median 355-390 ms vs R2R 821-933 ms cùng lô xen kẽ (máy bận 40-65 %), cửa sổ hiện ~145 ms, WS 116 vs 162 MB; G0.1 (<= 200) hụt, G0.3 đạt xa, 80 cảnh báo IL chỉ ở 2 file COM `dynamic` (WP-33); cửa sổ phụ WPF không chạy được trong AOT (WP-35)](perf/2026-10-12-no-wpf-spike.md)
- [2026-10-12 -- WP-33 COM shell không late-bound: truy vấn thứ tự Explorer warm 27-42 ms (cửa sổ thật 6 tệp), publish AOT shell 80 -> 0 cảnh báo IL, gửi/khôi phục Thùng rác Native TC06 cùng kết quả](perf/2026-10-12-wp33-com-shell.md)
- [2026-10-12 -- WP-34 IsAotCompatible 10 project + cong publish AOT: 0 canh bao IL, 0 DllImport trong src, exe AOT 11 MB](perf/2026-10-12-wp34-aot-compatible.md)
