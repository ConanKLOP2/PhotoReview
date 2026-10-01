---
id: RV-D3
order: 53
summary: |-
  Decided 2026-10-01 (option A): `ShortcutRouter.TryResolve` fires Recycle and every user Action only with NO modifier (Ctrl+Delete, Shift+Delete, Ctrl+Enter, Ctrl+F5 no longer delete/move/copy); Move/Copy-to-folder keep "no Ctrl, Shift = picker", Undo/OpenFolder keep Ctrl, navigation/zoom/toggles still accept Ctrl.
---

# RV-D3 - modifier rule for plain-key shortcuts (RV-A03)

Shortcut settings store a bare key (no modifier syntax), so before this change the router ignored `modifiers` for most
commands: Ctrl+Delete / Shift+Delete recycled, Ctrl+Enter, Ctrl+F3/F4/F5 ran Move/Copy actions. Alt combinations never
matched (WPF reports them as `Key.System`; only the Fullscreen check reads `systemKey`), so Alt+F4 never triggers the F4 action.

Options considered:
- **A (chosen)** file-changing commands (Recycle, every user Action) require `modifiers == ModifierKeys.None`; the Move/Copy-to-folder
  block is unchanged (no Ctrl, Shift = force picker); Undo/OpenFolder unchanged; navigation, zoom and toggles still accept Ctrl.
  Smallest behaviour change that removes the data-risk (an accidental Ctrl/Shift chord moving or deleting a photo).
- B every plain-key command rejects Ctrl (Shift allowed): also breaks Ctrl+Right/Left navigation and Ctrl+zoom habits.
- C keep as is and pin with tests: leaves the accidental-delete/move chords.

Implementation: `Input/ShortcutRouter.cs` (`noModifier` guard on the Action loop and the Recycle check). Tests:
`ShortcutRouterTests` (`RecycleAndActions_WithAnyModifier_DoNotResolve` over Delete/Enter/F3/F4/F5 x Ctrl, Shift, Ctrl+Shift;
pins for Ctrl+Right/Left/Add/Subtract/F, Shift+M picker, Ctrl+M null, Alt+F4 null).
