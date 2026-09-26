# Translating PhotoReview / Dịch PhotoReview

PhotoReview reads its UI text from plain JSON files. You can fix a word, translate the whole app, or add a new
language with Notepad — no build tools needed. Design: [ADR 0006](adr/0006-localization-json-catalogs.md).

PhotoReview lấy toàn bộ chữ trên giao diện từ các file JSON thường. Bạn có thể sửa một chữ, dịch cả ứng dụng
hoặc thêm ngôn ngữ mới chỉ bằng Notepad, không cần công cụ build. Phần tiếng Việt ở [cuối trang](#tiếng-việt).

## English

### Where the files are

| Folder | What | Who edits |
|---|---|---|
| `src/PhotoReview.Core/Localization/Languages/` (repo) = `<app folder>\Languages\` (installed) | `en.json` (source, always complete), `vi.json`, … and `en.notes.json` (context for translators) | Contributors, via pull request |
| `%LocalAppData%\PhotoReview\Languages\` | Your own files. They override the shipped ones key by key, or add a language | You, on your PC |

Lookup order for every text: your file → shipped file → built-in English → the key itself.

### Fix a few words on your own PC

1. Settings → **Language** → **Open languages folder**.
2. Create `vi.json` (or the code of your language) with only the keys you want to change:
   ```json
   { "_meta": { "code": "vi" }, "status.scanningFolder": "Đang quét thư mục ảnh…" }
   ```
3. Settings → **Reload translations**. The change shows immediately.

### Translate or add a language

1. Settings → pick the language (or English for a new one) → **Export strings to translate**. This writes
   `<code>.todo.json` with every key: the missing ones first (English text, also listed in `_missing`), then the
   current translations, plus translator notes. Languages with `"plural": "none"` do not get untranslated `.one` keys.
2. Create `<code>.json` (e.g. `de.json`) with a `_meta` block and your translations:
   ```json
   {
     "_meta": { "code": "de", "name": "German", "nativeName": "Deutsch", "plural": "one-other", "authors": ["Your name"], "formatVersion": 1 },
     "common.cancel": "Abbrechen"
   }
   ```
3. Put it in the user folder, restart or **Reload translations**, pick it under **Language**.
4. Start PhotoReview with `--i18n-keys` to see which key each text uses, or `--i18n-pseudo` to spot clipped layouts.
5. Share it: open a pull request that adds the file to `src/PhotoReview.Core/Localization/Languages/`
   (template: "Translation" PR). CI runs `tools/i18n-check.ps1`; run it yourself first:
   `powershell -ExecutionPolicy Bypass -File tools/i18n-check.ps1 -Path "$env:LOCALAPPDATA\PhotoReview\Languages"`.

### Rules

- Keep placeholders exactly: `{count}`, `{fileName}` … You may move them or drop one (warning), never invent one
  (that entry is ignored and English is shown). Write `{{` / `}}` for literal braces.
- Plurals: `key.one` / `key.other`. Languages without plural forms set `"plural": "none"` and only need `.other`.
- Keep file-dialog filters' `|` separators and `*.json` patterns.
- A broken file never crashes the app: it is skipped (a broken entry shows English) and the problem is written to
  the log. **Reload translations** also shows every problem in a message (file, key, reason), or says none were found.
- Maintainers: when the *meaning* of an English text changes, give it a new key (e.g. `…V2`) so old
  translations never silently mismatch.

## Tiếng Việt

### File nằm ở đâu

| Thư mục | Nội dung | Ai sửa |
|---|---|---|
| `src/PhotoReview.Core/Localization/Languages/` (repo) = `<thư mục app>\Languages\` (khi cài) | `en.json` (bản gốc, luôn đủ), `vi.json`, … và `en.notes.json` (ngữ cảnh cho người dịch) | Người đóng góp, qua pull request |
| `%LocalAppData%\PhotoReview\Languages\` | File của riêng bạn: ghi đè từng key của bản đi kèm, hoặc thêm ngôn ngữ | Bạn, trên máy mình |

Thứ tự tìm mỗi chữ: file của bạn → file đi kèm → English có sẵn → chính tên key.

### Sửa vài chữ trên máy mình

1. Cài đặt → **Ngôn ngữ** → **Mở thư mục ngôn ngữ**.
2. Tạo `vi.json` chỉ chứa các key muốn đổi (ví dụ ở phần English phía trên).
3. Cài đặt → **Tải lại bản dịch**. Thay đổi hiện ngay.

### Dịch hoặc thêm ngôn ngữ

1. Cài đặt → chọn ngôn ngữ → **Xuất chuỗi cần dịch**: tạo `<mã>.todo.json` gồm toàn bộ key: key còn thiếu ở đầu (câu English, liệt kê trong `_missing`), sau đó là bản dịch hiện tại, kèm ghi chú. Ngôn ngữ có `"plural": "none"` không bị liệt kê các key `.one` chưa dịch.
2. Tạo `<mã>.json` có khối `_meta` (mã, tên, tên bản địa, `plural`, tác giả) và bản dịch.
3. Đặt vào thư mục người dùng, **Tải lại bản dịch**, chọn trong **Ngôn ngữ**.
4. Chạy PhotoReview với `--i18n-keys` để thấy key của từng chữ, hoặc `--i18n-pseudo` để phát hiện chữ bị cắt.
5. Chia sẻ: mở pull request thêm file vào `src/PhotoReview.Core/Localization/Languages/` (template "Translation").
   CI chạy `tools/i18n-check.ps1`; nên tự chạy trước.

### Quy tắc

- Giữ nguyên placeholder `{count}`, `{fileName}`…: được đổi vị trí hoặc bỏ bớt (có cảnh báo), không được tự đặt tên mới
  (key đó bị bỏ qua, hiện English). Dấu ngoặc nhọn thường viết `{{` / `}}`.
- Số nhiều: `key.one` / `key.other`; tiếng Việt dùng `"plural": "none"` nên chỉ cần `.other`.
- File hỏng không làm app crash: file bị bỏ qua (mục hỏng hiện tiếng Anh) và lỗi được ghi vào log. Nhấn **Tải lại bản dịch**
  sẽ hiện thông báo liệt kê từng lỗi (file, key, lý do), hoặc báo không có lỗi.

## Vietnamese glossary / Thuật ngữ tiếng Việt

`vi.json` uses exactly one Vietnamese term per concept, following Windows' Vietnamese UI where it has one
(L12). Use these terms when you add or change a Vietnamese string. / `vi.json` dùng đúng một thuật ngữ cho mỗi
khái niệm, theo giao diện Windows tiếng Việt khi có. Hãy dùng các từ dưới đây khi thêm hoặc sửa chuỗi tiếng Việt.

| English | Tiếng Việt | Note / Ghi chú |
|---|---|---|
| folder | thư mục | never "folder" |
| file | tệp | never "file" |
| Recycle Bin; recycle | Thùng rác (capital T); đưa vào Thùng rác | "Delete" operation = `Xóa (vào Thùng rác)`: must not sound permanent |
| action (user-defined, with a shortcut) | hành động | Action profiles = Thiết lập hành động |
| operation (Move/Copy/Recycle that was performed and journaled) | thao tác | pending = đang chờ, failed = thất bại |
| Move / Copy | Di chuyển / Sao chép | capitalized when naming the operation |
| delete, remove, clear, dismiss | xóa | spelling `xóa`, never `xoá` |
| undo | hoàn tác | |
| retry | thử lại | |
| Recovery (window/feature) | Phục hồi | "restore" (defaults, Recycle Bin, session) = khôi phục |
| preview (image) | ảnh xem trước | loading mode Preview = Xem trước; Original = Gốc; Fast = Nhanh |
| Fit | Vừa khung | |
| zoom; zoom in / out | thu phóng; phóng to / thu nhỏ | |
| fullscreen | toàn màn hình | |
| compare | so sánh | |
| settings | cài đặt | |
| shortcut | phím tắt | |
| import / export | nhập / xuất | |
| journal | nhật ký | log (diagnostic log file) stays "log" |
| batch | xử lý hàng loạt | |
| source / destination | nguồn / đích | |
| duplicate | bản trùng lặp | |
| decoder | bộ giải mã | decode = giải mã |
| preload | tải trước | |
| clipboard | bảng tạm | |
| click; press (a key) | nhấn; nhấn phím | double-click = bấm đúp |
| bytes | byte | Vietnamese has no plural form |

Kept in English (technical words Vietnamese users already use / Giữ tiếng Anh): cache, hash, log, benchmark,
JSON, RAM, SSD/HDD, metadata, Explorer, key names (Ctrl, Home, PgUp…), decoder backend names (WPF, WIC Direct,
TurboJPEG), and benchmark profile / workload names (identifiers that also appear in English reports).

Style: sentence case; no trailing period on buttons, labels and titles; full sentences in messages end with a
period; `…` (U+2026) for "choose…" buttons, `...` for progress text, matching `en.json`.
