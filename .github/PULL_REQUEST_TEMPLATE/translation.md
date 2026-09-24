## Translation / Bản dịch

- Language / Ngôn ngữ: <!-- e.g. de (Deutsch) -->
- New language or update / Ngôn ngữ mới hay cập nhật:

### Checklist

- [ ] Only files in `src/PhotoReview.Core/Localization/Languages/` changed (no code).
- [ ] `_meta` has `code`, `name`, `nativeName`, `plural`, `authors`.
- [ ] `powershell -ExecutionPolicy Bypass -File tools/i18n-check.ps1` passes (no errors).
- [ ] Checked in the app (Settings → Language, or a copy in `%LocalAppData%\PhotoReview\Languages`); `--i18n-pseudo` shows no clipped layout in my language.

Guide: [docs/TRANSLATING.md](../../docs/TRANSLATING.md)
