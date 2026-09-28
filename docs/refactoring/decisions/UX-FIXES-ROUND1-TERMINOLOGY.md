---
id: UX-FIXES-ROUND1 (terminology)
order: 34
summary: |-
  User chose to finish UX-FIXES-ROUND1 finding #3: the two English labels still reading "Recycle" (`action.recycle.name`, `enum.fileOperation.recycle`) now read "Move to Recycle Bin" like every other English string; Vietnamese already said "Đưa vào Thùng rác" everywhere.
---

# UX-FIXES-ROUND1 follow-up — Recycle Bin terminology (2026-09-28)

[UX-FIXES-ROUND1](UX-FIXES-ROUND1.md) (#225) left finding #3 "Mostly Fixed": English kept the short "Recycle" for
`action.recycle.name` (action-profile/status wording, e.g. "Done: {actionName}") and `enum.fileOperation.recycle`
(Action profiles combo, Recovery window), while every other English string says "Move to Recycle Bin".

Options put to the user: keep the short form (brevity in combos/status lines; Vietnamese users never see it) or
unify. **Decision: unify** — both keys now read "Move to Recycle Bin". Only the English catalog changes; no test
asserted the old text (the "Recycle" literals in tests are the `FileOperationType` enum member names used by the
journal JSON, unaffected). Embedding sentences still read naturally ("Done: Move to Recycle Bin", "Run action
'Move to Recycle Bin' on the current image?").
