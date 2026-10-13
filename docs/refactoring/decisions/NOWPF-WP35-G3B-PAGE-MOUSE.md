---
id: NOWPF-WP35-G3B-MOUSE
order: 1180
summary: |-
  WP-35 GÄ3b (CHÆ¯A XÃC NHáº¬N, tiáº¿p NOWPF-WP35-G3A): trang Mouse cá»§a SettingsDialog Win32 - 20 thuá»™c tÃ­nh (zoom/click/bÆ°á»›c phÃ­m/neo Fit width 1-2/giá»¯ zoom/chuá»™t giá»¯a 11 má»¥c, con lÄƒn, kinetic, mÅ©i tÃªn á»Ÿ mÃ©p, chuyá»ƒn áº£nh Fade + Ms, touchpad + khoáº£ng vuá»‘t) vÃ  18 Ã´ tick menu chuá»™t pháº£i dá»±ng báº±ng mÃ£ theo `ContextMenuItems.Items`; má»¥c Settings luÃ´n báº­t + tooltip. Khung chá»‰ Cá»˜NG THÃŠM 3 thá»© nhá» (hook OnActivated, SetTooltip, CurrentShortcutTexts). 19 Ä‘á»™t biáº¿n cháº¡y: 18 cháº¿t á»Ÿ bÆ°á»›c 1, 1 sá»‘ng (M04) Ä‘Æ°á»£c láº¥p báº±ng test má»›i, 0 sá»‘ng tháº­t.
---

# NOWPF-WP35-G3B - trang Mouse (2026-10-13)

Theo hÆ°á»›ng dáº«n "cÃ¡ch thÃªm má»™t trang" cá»§a [NOWPF-WP35-G3A-SETTINGS-SHELL](NOWPF-WP35-G3A-SETTINGS-SHELL.md); presenter/`SettingsFieldMap` KHÃ”NG Ä‘á»•i.

## 1. ÄÃ£ lÃ m

| Pháº§n | File | Ná»™i dung |
|---|---|---|
| Trang | `src/PhotoReview.Shell.Win32/Dialogs/SettingsUi/Pages/MouseSettingsPage.cs` | 3 nhÃ³m nhÆ° WPF: Zoom, "Mouse & zoom", Menu chuá»™t pháº£i. Thá»© tá»± má»¥c combobox giá»¯ Ä‘Ãºng chá»‰ sá»‘ cá»§a `SettingsFormState` (middle-click = giÃ¡ trá»‹ enum 0..10). Ã” Ms cá»§a Fade chá»‰ báº­t khi chá»n Fade. 18 Ã´ tick: má»™t cho má»—i `ContextMenuItems.Items`, con cá»§a Zoom thá»¥t vÃ o 12 dlu, chá»¯ qua `ContextMenuItemLabel` nÃªn Ä‘á»•i theo ngÃ´n ngá»¯. TÃ³m táº¯t phÃ­m zoom (8 dÃ²ng) do mÃ£ Ä‘áº·t. |
| Danh má»¥c | `SettingsPageCatalog.cs` | Má»˜T dÃ²ng `["Mouse"]`. |
| Khung (cá»™ng thÃªm) | `ISettingsPageView`/`SettingsPageBase.OnActivated`, `SettingsPageContext.SetTooltip` + `CurrentShortcutTexts`, `SettingsDialog.SetControlTooltip` + gá»i `OnActivated` trong `ActivatePage` | TÃ³m táº¯t phÃ­m zoom pháº£i theo Ã´ phÃ­m Ä‘ang gÃµ á»Ÿ trang Shortcuts (lÃ m tÆ°Æ¡i khi chuyá»ƒn tá»›i trang Mouse); tooltip dÃ¹ng láº¡i `SetItemTooltips` (callback, Ä‘á»•i chá»¯ theo ngÃ´n ngá»¯). |
| Chuá»—i | - | KhÃ´ng thÃªm khoÃ¡: má»i chuá»—i Ä‘Ã£ cÃ³ trong catalog (i18n-check xanh). |
| Test | `tests/PhotoReview.Shell.Tests/Dialogs/SettingsUi/MouseSettingsPageTests.cs` | file RIÃŠNG (34 ca ká»ƒ cáº£ cÃ¡c hÃ ng Theory) Ä‘á»ƒ PR cÃ¡c trang song song khÃ´ng xung Ä‘á»™t; `SettingsPagesTests.AnUnportedPage...` Ä‘á»•i sang "trang chá» Ä‘áº§u tiÃªn cÃ²n láº¡i" thay vÃ¬ cá»‘ Ä‘á»‹nh Mouse. |

## 2. Quyáº¿t Ä‘á»‹nh (CHÆ¯A XÃC NHáº¬N)

- Má»¥c Settings bá»‹ khoÃ¡: WPF vÃ´ hiá»‡u hoÃ¡ Ã´ + tooltip. Win32 khÃ´ng hiá»‡n tooltip cá»§a Ä‘iá»u khiá»ƒn bá»‹ disable (cá»­a sá»• disable khÃ´ng nháº­n chuá»™t) nÃªn Ã´ GIá»® báº­t, tá»± tick láº¡i khi báº¥m vÃ  cÃ³ tooltip. PhÆ°Æ¡ng Ã¡n thay: disable + gáº¯n tooltip lÃªn hÃ ng cha (xáº¥u hÆ¡n).
- Lá»—i `ClickZoomPercent` khÃ´ng cÃ³ `SettingsFocusField` (presenter khÃ´ng Ä‘áº·t) nÃªn Save tá»« trang khÃ¡c khÃ´ng tá»± chuyá»ƒn vá» trang Mouse (giá»‘ng WPF: chá»‰ hiá»‡n cáº£nh bÃ¡o). Muá»‘n Ä‘á»•i pháº£i thÃªm `ClickZoomPercent` vÃ o enum + presenter (ngoÃ i pháº¡m vi).
- Ã” Ms bá»‹ disable (transition = None) mÃ  giÃ¡ trá»‹ sai: cáº£nh bÃ¡o hiá»‡n nhÆ°ng khÃ´ng focus Ä‘Æ°á»£c Ã´ (giá»‘ng WPF).

## 3. Kiá»ƒm chá»©ng

- áº¢nh chá»¥p `settings-mouse-{en,vi}[-150][-mid|-bottom].png` (desktop áº©n `PRSetMouse`) Ä‘Ã£ xem báº±ng máº¯t: nhÃ£n tiáº¿ng Viá»‡t dÃ i (2-3 dÃ²ng), ghi chÃº 3-4 dÃ²ng, 8 dÃ²ng tÃ³m táº¯t phÃ­m, 18 Ã´ tick: khÃ´ng cáº¯t chá»¯, khÃ´ng chá»“ng á»Ÿ 100 % vÃ  150 %; thanh cuá»™n tá»‘i.
- Khung Ä‘Ã£ duyá»‡t bá»‘ cá»¥c/Tab/rÃ² handle en-vi 100 %/150 % cho má»i trang cá»§a `SettingsPresenter.Pages` (Mouse náº±m trong Ä‘Ã³): `SettingsUi` 109 ca xanh.
- Äá»™t biáº¿n hai bÆ°á»›c (19 Ä‘á»™t biáº¿n thá»§ cÃ´ng cháº¡y Ä‘Æ°á»£c, bÆ°á»›c 1 = `MouseSettingsPageTests`; M19 "CurrentShortcutTexts tráº£ rá»—ng" khÃ´ng dá»±ng Ä‘Æ°á»£c vÃ¬ analyzer CA1822 cháº·n, Ã½ Ä‘Ã³ Ä‘Ã£ Ä‘Æ°á»£c M14/M18 phá»§): lá»‡ch trÆ°á»ng (M01-M04, M13, M20), chá»‰ sá»‘/thá»© tá»± combo (M02, M08, M17), 18 Ã´ menu báº­t/táº¯t (M05, M06), thá»¥t (M07), má»¥c khoÃ¡ (M09, M16), phá»¥ thuá»™c Fade (M10), FocusTarget (M11, M12), tÃ³m táº¯t/hook khung (M14, M15, M18). Sá»‘ng á»Ÿ bÆ°á»›c 1: M04 (ClickToZoom Ä‘á»c tá»« Ã´ KeepZoom - test sá»­a Ä‘á»“ng thá»i má»i Ã´ tick nÃªn khÃ´ng phÃ¢n biá»‡t) -> test má»›i `Mouse_EachTickBox_WritesOnlyItsOwnProperty` (má»—i Ã´ má»™t láº§n), cháº¿t. 0 sá»‘ng tháº­t. BÆ°á»›c 2 (toÃ n Shell.Tests) khÃ´ng cáº§n vÃ¬ khÃ´ng cÃ²n káº» sá»‘ng sau bÆ°á»›c 1.

## 4. ChÆ°a kiá»ƒm

DPI hai mÃ n hÃ¬nh tháº­t, title bar tá»‘i, Narrator/IME bÃ n phÃ­m tháº­t (nhÆ° GÄ3a); bá»‘ cá»¥c cá»§a ba trang Performance/Files/Shortcuts do PR riÃªng.

HoÃ n tÃ¡c: revert PR (má»™t dÃ²ng catalog + file trang; pháº§n khung cá»™ng thÃªm vÃ´ háº¡i).
