# Branch và worktree cleanup — 2026-09-19

Cleanup được thực hiện từ `codex/w6-performance` tại commit `6f8ba8d`, sau khi T72 hoàn tất.

## Đã xóa

- 25 worktree cũ: 18 worktree `.claude/worktrees` và 7 worktree MR/T62–T64. Tất cả sạch, ngoại trừ worktree T03 có hai file log build không track (`build_output.txt`, `build_output_after.txt`).
- 72 nhánh local task/diagnosis/worktree đã nằm trong HEAD hoặc có patch tương đương.
- 28 nhánh remote task/refactor đã là ancestor của HEAD.

Các tip quan trọng của nhánh worker không phải ancestor trực tiếp nhưng đã được `git cherry HEAD <branch>` xác nhận tương đương trước khi xóa:

| Nhánh | Tip đã xóa |
|---|---|
| `codex/mr01-preload` | `683a890` |
| `codex/mr03-icc` | `8eeee8f` |
| `codex/mr04-turbo` | `6f26d2f` |
| `codex/mr05-cache` | `8f1f20c` |
| `codex/t62-journal` | `0415cd8` |
| `codex/t63-catalog-metadata` | `ace446a` |
| `codex/t64-ram-budget` | `cce611d` |

## Được giữ lại

| Local | Lý do |
|---|---|
| `codex/w6-performance` | Nhánh đang hoạt động, chứa T65–T72 và cleanup này |
| `refactor/integration` | Nhánh tích hợp dùng cho PR/release |
| `master` | Nhánh phát hành |
| `refactor/t47-delete-source-presence` | `git cherry` vẫn báo patch riêng `8513eb0`; giữ để không mất thay đổi chưa đối chiếu |

Remote còn `origin/master`, `origin/refactor/integration` và `origin/refactor/t47-delete-source-presence`.

## Khôi phục

Không chạy `git gc` hoặc prune object. Có thể tạo lại nhánh bằng `git branch <tên> <SHA>` từ bảng trên hoặc từ reflog. Manifest đầy đủ 108 ref/SHA trước cleanup được tạo tại `C:\Users\adminvti\AppData\Local\Temp\photoreview-branch-cleanup-20260919-234923.txt` trên máy thực hiện.
