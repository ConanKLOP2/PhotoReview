---
id: CI-OPTIMIZE-ROUND2
order: 1260
summary: |-
  CI vÃ²ng 2: 6 job test + compile-gate (build test bá» analyzer, build báº±ng solution filter), tÆ°á»ng ~395 s xuá»‘ng ~200 s (CHÆ¯A XÃC NHáº¬N, 2026-10-14).
---

# CI-OPTIMIZE-ROUND2 - giáº£m thá»i gian tÆ°á»ng cá»§a ci.yml láº§n 2 (2026-10-14)

Tráº¡ng thÃ¡i: **CHÆ¯A XÃC NHáº¬N** (Ä‘á»•i workflow CI cáº§n chá»§ repo xem; PR khÃ´ng tá»± báº­t auto-merge). HoÃ n tÃ¡c: revert PR (ci.yml,
`tools/ci/*.slnf`, `TestFilterDriftTests.cs`, 3 cÃ¢u trong README/TESTING/architecture). Ná»‘i tiáº¿p [CI-PARALLEL-TEST-JOBS](CI-PARALLEL-TEST-JOBS.md).

## 1. Káº¿t quáº£

TÆ°á»ng = tá»« lÃºc job Ä‘áº§u tiÃªn báº¯t Ä‘áº§u Ä‘áº¿n lÃºc `build-test-publish` xong (windows-latest 2 vCPU).

| Cáº¥u hÃ¬nh | Run | TÆ°á»ng | Ghi chÃº |
|---|---|---|---|
| Ná»n (master, sau #502) | 38424862694 / 38423595823 | 395 s / ~390 s | `test-shell` 394 s quyáº¿t Ä‘á»‹nh |
| V1: chá»‰ Ä‘á»•i vÃ²ng `foreach build` thÃ nh 1 `dotnet build x.slnf` | 38430243581 | 388 s | build 118/82/71 s so vá»›i 123-139/78-117/52-63 s: KHÃ”NG lá»£i (CPU-bound trÃªn 2 vCPU) |
| V2: V1 + Shell.WpfBridge.Tests sang test-core | 38430286743 | 338 s | test-shell 326 s (Ä‘á» do flaky), core 312 s |
| V3: V2 + Shell.Tests thÃ nh 2 shard | 38430800376 | 331 s | core 316 s thÃ nh Ä‘iá»ƒm ngháº½n |
| V5: 6 job test + build-publish chá»‰ build 2 exe | 38432813132 | 249 s | má»i job <= 244 s (test-shell Ä‘á» do flaky) |
| V6/V10: V5 + `-p:RunAnalyzers=false` á»Ÿ build cá»§a job test | 38432884099 / 38434088843 | job test 155-219 s | build job test 29-48 s so vá»›i 61-100 s |
| **V11 (cuá»‘i): V10 + job `compile-gate` + NuGet trÃªn D:** | 38435760833 (láº§n 1 nguá»™i / láº§n 2 áº¥m) | ~230 s / **205 s** | xem báº£ng dÆ°á»›i |

Tá»«ng job, V11 cháº¡y áº¥m (láº§n 2) so vá»›i ná»n (s):

| Job | Ná»n | V11 | Viá»‡c |
|---|---|---|---|
| `test-core` | 298 | 182 | Core, Imaging (build nháº¹) |
| `test-app` | (trong test-core) | 155 | Architecture, App, Shell.WpfBridge (build náº·ng WPF) |
| `test-shell` | 394 | 146 | Shell.Tests shard B + Shell.Integration |
| `test-shell-settings` | (trong test-shell) | 161 | Shell.Tests shard A (namespace `Dialogs.SettingsUi`) |
| `test-integration` | 255 | 192 | Integration.Tests bÆ°á»›c chÃ­nh |
| `test-integration-slow` | (trong test-integration) | 169 | bÆ°á»›c Integration+Slow cá»§a Integration.Tests |
| `compile-gate` | (trong build-publish) | 172 | cáº£ `PhotoReview.slnx`, analyzer Báº¬T, warning = lá»—i |
| `build-publish` | 169 | 157 | build 2 exe (analyzer báº­t) + publish + verify |

Sá»‘ test tá»«ng project khÃ´ng Ä‘á»•i (Passed/Total, run 38432884099 vÃ  38432903608): Architecture 160 (159 + 1 luáº­t má»›i), Core 3202,
Imaging 4181, App 2583, Integration 1344 + 17 Slow, Shell 1370 + 239 = 1609 (hai shard bÃ¹ nhau), Shell.Integration 151, WpfBridge 39.

## 2. CÃ¡i gÃ¬ giá»¯ láº¡i vÃ  vÃ¬ sao

1. **`-p:RunAnalyzers=false` á»Ÿ bÆ°á»›c build cá»§a 6 job test** (Ä‘Ã²n báº©y lá»›n nháº¥t): build cá»§a job test giáº£m cÃ²n 34-46 s (tá»« 61-100 s);
   build cáº£ solution khÃ´ng analyzer chá»‰ 45 s so vá»›i 126 s cÃ³ analyzer (thÃ­ nghiá»‡m artifact, run 38437459182). Analyzer khÃ´ng Ä‘á»•i mÃ£ sinh ra
   (source generator nhÆ° Localization.Generator VáºªN cháº¡y), nÃªn káº¿t quáº£ test nhÆ° cÅ©. Cá»•ng cháº¥t lÆ°á»£ng KHÃ”NG máº¥t: job má»›i `compile-gate` build
   cáº£ solution (src + tests + tools) vá»›i analyzer báº­t vÃ  warning = lá»—i, y nhÆ° bÆ°á»›c `Build Release` cÅ© cá»§a build-publish. Luáº­t
   `AnalyzersAreSkippedOnlyWhereCompileGateStillRunsThem` canh: cá» chá»‰ náº±m á»Ÿ dÃ²ng build cá»§a job `test-*`, khÃ´ng á»Ÿ `compile-gate`/`build-publish`/props.
2. **Má»™t `dotnet build tools/ci/<job>.slnf`** thay vÃ²ng build tuáº§n tá»±: khÃ´ng nhanh hÆ¡n trÃªn CI (V1), nhÆ°ng gá»n, vÃ  cho phÃ©p luáº­t
   "job nÃ o test project nÃ o thÃ¬ build project Ä‘Ã³" (Ä‘á»c slnf).
3. **CÃ¢n láº¡i job**: Shell.WpfBridge.Tests sang `test-app` (váº¿t build WPF cá»§a Architecture Ä‘Ã£ phá»§ háº¿t phá»¥ thuá»™c cá»§a nÃ³, thÃªm ~12 s test) nÃªn
   `test-shell` khÃ´ng cÃ²n build WPF/XAML; Shell.Tests tÃ¡ch 2 shard theo namespace báº±ng filter bÃ¹ nhau (`FullyQualifiedName~X` vÃ  `!~X`,
   luáº­t CI-PARALLEL kiá»ƒm cáº·p, nÃªn khÃ´ng máº¥t/trÃ¹ng test); bÆ°á»›c Integration+Slow cá»§a Integration.Tests (68-89 s) sang job riÃªng.
4. **build-publish chá»‰ build `tools/ci/publish.slnf`** (2 exe + closure), 157 s so vá»›i 169-262 s; pháº§n cÃ²n láº¡i cá»§a solution do `compile-gate` phá»§.
5. **`NUGET_PACKAGES=D:\nuget-packages`** (á»• D: lÃ  á»• workspace, nhanh hÆ¡n á»• C: profile): restore tráº§n 14-18 s (V8) so vá»›i 33-63 s khi bá» cache
   trÃªn C: (V7); cache trÃºng trÃªn D: = 5-9 s + restore 11-13 s, ngang restore tráº§n, nÃªn giá»¯ bÆ°á»›c cache NuGet (Ä‘á»¡ phá»¥ thuá»™c nuget.org) nhÆ°ng
   Ä‘áº·t cáº£ hai trÃªn D:. ChÃªnh so vá»›i C: (cache 10-36 s + restore 10-13 s) ~ 5-10 s/job.
6. **Cache `native/x64/turbojpeg.dll`** (key theo hash pin + script): bÆ°á»›c `Fetch native binaries` (táº£i turbojpeg tá»« nuget.org) trung bÃ¬nh 3-6 s
   nhÆ°ng cÃ³ outlier 106-132 s á»Ÿ 6 trong ~60 lÆ°á»£t job (V6, V7, V8, V10, V11). CÃ³ cache thÃ¬ chá»‰ kiá»ƒm hash; script váº«n xÃ¡c minh SHA-256 pin.
   LibRaw Ä‘Ã£ náº±m trong repo nÃªn khÃ´ng táº£i.

## 3. Ã Ä‘Ã£ thá»­ vÃ  LOáº I

| Ã | Sá»‘ Ä‘o | LÃ½ do loáº¡i |
|---|---|---|
| `-p:EnforceCodeStyleInBuild=false` riÃªng | V9: build 49-88 s so vá»›i V5 61-100 s (trong phÆ°Æ¡ng sai) | khÃ´ng lá»£i; `RunAnalyzers=false` Ä‘Ã£ bao háº¿t |
| Bá» bÆ°á»›c cache NuGet, restore tráº§n (á»• C:) | V7: restore 33-63 s | cháº­m hÆ¡n cache (24-26 s) |
| Chia sáº» build qua artifact (2c): 1 job build rá»“i cÃ¡c job test `--no-build` | exp 38437459182: upload 8 s, download 5 s (272 MB, 1408 file), build cáº£ solution khÃ´ng analyzer 45 s | táº£i/giáº£i nÃ©n ráº», nhÆ°ng ná»‘i tiáº¿p build -> test cá»™ng ~100 s (checkout+cache+restore+build+upload) vÃ o má»i job, trong khi build riÃªng tá»«ng job nay chá»‰ 34-46 s cháº¡y song song; Ä‘Æ°á»ng gÄƒng sáº½ ~235 s so vá»›i ~200 s |
| Shard Integration.Tests bÆ°á»›c chÃ­nh (111 s) thÃ nh 2 | Æ°á»›c tÃ­nh | tÆ°á»ng chá»‰ cÃ²n bá»‹ `compile-gate` (172-197 s) cháº·n: lá»£i ~15 s Ä‘á»•i láº¥y +1 job runner |
| Táº¯t Defender/loáº¡i trá»« thÆ° má»¥c (nhanh hÆ¡n IO) | khÃ´ng thá»­ | lÃ  Ä‘á»•i cÃ i Ä‘áº·t báº£o máº­t cá»§a runner: Ä‘á»ƒ chá»§ repo quyáº¿t |
| `--no-restore` + restore theo slnf | khÃ´ng thá»­ | cache NuGet lÆ°u tá»« job Ä‘áº§u: restore má»™t pháº§n sáº½ lÆ°u cache thiáº¿u cho má»i run sau (lÃ½ do cÅ© trong ci.yml) |
| Bá» `fetch-depth: 0` á»Ÿ job test | khÃ´ng thá»­ | checkout chá»‰ ~8 s; phiÃªn báº£n dá»±a vÃ o lá»‹ch sá»­ git |

## 4. Rá»§i ro

- Sá»‘ job Windows má»—i run: 10 (repo-checks, build-publish, compile-gate, 6 test, publish-aot) so vá»›i 7 trÆ°á»›c: nhiá»u PR cÃ¹ng lÃºc cÃ³ thá»ƒ xáº¿p hÃ ng
  náº¿u gÃ³i GitHub giá»›i háº¡n 20 job Ä‘á»“ng thá»i; tá»•ng phÃºt runner tÄƒng ~35% (1500 s so vá»›i 1116 s) Ä‘á»ƒ Ä‘á»•i láº¥y tÆ°á»ng giáº£m ~48%.
- Hai flaky Ä‘Ã£ tháº¥y, KHÃ”NG do thay Ä‘á»•i nÃ y (Ä‘Ã£ gáº·p trÆ°á»›c khi tÃ¡ch): `NativeDialogChromeTests.Benchmark_HasADarkBackground_AndReadableText` (shell, Ä‘á» á»Ÿ
  ~4 trong 8 run V2-V11) vÃ  `DarkScrollBarRenderingTests.ScrollViewer_WithAppDarkTheme_RendersDarkThumbAndCorner` ("render timed out" 15 s, 1 láº§n á»Ÿ test-app).
  CÃ³ 6 job nÃªn `gh run rerun --failed` chá»‰ cháº¡y láº¡i job Ä‘á» (ráº» hÆ¡n trÆ°á»›c).
- Job test dÃ¹ng binary biÃªn dá»‹ch KHÃ”NG analyzer; náº¿u ai Ä‘Ã³ thÃªm logic phá»¥ thuá»™c analyzer vÃ o test (khÃ´ng cÃ³ hÃ´m nay), luáº­t pháº£i Ä‘Æ°á»£c xem láº¡i.
- Cache D: vÃ  cache turbojpeg theo nhÃ¡nh: lÆ°á»£t Ä‘áº§u cá»§a nhÃ¡nh/PR lÃ  nguá»™i (restore ~15-19 s, táº£i turbojpeg), master giá»¯ cache cho cÃ¡c PR sau.

## 5. Viá»‡c cho chá»§ repo

Required checks váº«n `build-test-publish` + `repo-checks`, tÃªn khÃ´ng Ä‘á»•i: khÃ´ng cáº§n sá»­a gÃ¬. Job má»›i (`test-app`, `test-shell-settings`, `test-integration-slow`,
`compile-gate`) Ä‘Ã£ náº±m trong `needs` cá»§a `build-test-publish`.
