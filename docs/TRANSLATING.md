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
   `<code>.todo.json` with every missing key, its English text and translator notes.
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
- A broken file never crashes the app: it is skipped and the problem is written to the log.
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

1. Cài đặt → chọn ngôn ngữ → **Xuất file cần dịch**: tạo `<mã>.todo.json` gồm các key còn thiếu, câu English và ghi chú.
2. Tạo `<mã>.json` có khối `_meta` (mã, tên, tên bản địa, `plural`, tác giả) và bản dịch.
3. Đặt vào thư mục người dùng, **Tải lại bản dịch**, chọn trong **Ngôn ngữ**.
4. Chạy PhotoReview với `--i18n-keys` để thấy key của từng chữ, hoặc `--i18n-pseudo` để phát hiện chữ bị cắt.
5. Chia sẻ: mở pull request thêm file vào `src/PhotoReview.Core/Localization/Languages/` (template "Translation").
   CI chạy `tools/i18n-check.ps1`; nên tự chạy trước.

### Quy tắc

- Giữ nguyên placeholder `{count}`, `{fileName}`…: được đổi vị trí hoặc bỏ bớt (có cảnh báo), không được tự đặt tên mới
  (key đó bị bỏ qua, hiện English). Dấu ngoặc nhọn thường viết `{{` / `}}`.
- Số nhiều: `key.one` / `key.other`; tiếng Việt dùng `"plural": "none"` nên chỉ cần `.other`.
- File hỏng không làm app crash: file bị bỏ qua và lỗi được ghi vào log.
