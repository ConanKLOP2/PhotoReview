# Power-User Customization & Automation Feature Ideas

**Date:** 2026-09-28  
**Context:** Brainstorm for features targeting photographers and reviewers who perform repeated, consistent review tasks on similar photo collections.  
**Scope:** Desktop-only, no cloud, no plugin runtime. Stays consistent with existing ActionProfiles/ReviewAction mechanism; builds on it rather than parallel systems.

---

## Survivors: Ideas That Passed Self-Critique

### 1. Review Session Presets

**User Scenario:**  
A photographer culls multiple fashion shoots following the same pattern: open a folder, zoom to 150%, show file info but not folder info, have action profiles "Keep" and "Reject" active, and always start with Fit View. Instead of manually reconfiguring each time, they save a "Fashion Cull v2" preset, then load it with one shortcut to restore the entire session context.

**Rough Mechanism:**  
- New "Review Presets" section in Settings > General.
- "Save Current Session" button captures: folder path (or "last opened"), zoom level, scroll position, which action profiles are visible/highlighted in the UI, ShowFileInfo/ShowFolderInfo state, window size/position (optional).
- "Load Preset" dropdown (or shortcut) restores the captured state.
- Import/Export presets to `.json` files (like ActionProfiles already support).
- Presets are stored in `AppSettings.ReviewSessionPresets : List<ReviewSessionPreset>`.

**Self-Critique:**  
- ✅ Does NOT duplicate ActionProfiles — presets capture *session context*, not action definitions.
- ✅ Does NOT require arbitrary script execution — just snapshots of known UI state.
- ✅ Does NOT duplicate existing settings — settings are *persistent*, presets are *context snapshots* you can swap quickly.
- ✅ Aligns with "keep it simple, fast" — power users benefit, casual users never touch it.

**Complexity:** M

---

### 2. Folder-Based Action Profile Selection

**User Scenario:**  
When a photographer opens a folder named "Fashion/BTS" (behind-the-scenes), they want the "BTS Review" action profile to auto-select or be highlighted. When they open "Fashion/Finals", the "Finals Review" profile activates. Pattern matching, not hardcoding per-folder.

**Rough Mechanism:**  
- Extend `ReviewAction` with an optional `FolderPatternToActivate : string` field (e.g., `"*BTS*"`, `"Finals"`, `"*Test*"`).
- Or: new structure `FolderProfileMapping : List<(glob pattern, action profile name)>` in AppSettings.
- On folder open, scan active action profiles and highlight/select the first one matching the current folder path.
- Optional: also load a Review Preset if one is tagged with the same folder pattern.

**Self-Critique:**  
- ✅ Does NOT duplicate ActionProfiles — extends them with a metadata field.
- ✅ Does NOT require arbitrary script execution — glob patterns only, no code execution.
- ✅ Does NOT duplicate existing settings — folder selection is new, not covered by existing settings.
- ✅ Safe for minimalist defaults — opt-in, pattern-based, fails gracefully if pattern doesn't match.

**Complexity:** M

---

### 3. Action Profiles: Template Destinations & Smart Prompting

**User Scenario:**  
A photographer wants to organize outputs by date taken. Instead of hardcoding "2026-09-28", they use a template `"{date-taken}/Finals"` in the destination field. The action moves the file to a folder created with today's date.  
Alternative: Shift+shortcut always pops the folder picker (currently may already work), while unmodified uses the action's destination directly.

**Rough Mechanism:**  
- **Option A (Template):** Extend `ReviewAction.Destination` to support simple templates:
  - `{date-taken}` → extracts from EXIF, falls back to file modified date if missing.
  - `{date-modified}` → file's modified date.
  - `{group-N}` → Group-N folder (already supported).
  - Literal folder paths still work as-is.
  - Validation/error handling: if template can't resolve, prompt user.
- **Option B (Smart Prompt):** Add `ReviewAction.AlwaysPromptDestination : bool` — if true, Shift+shortcut opens picker, unmodified uses hardcoded destination.

**Self-Critique:**  
- ✅ Does NOT duplicate existing ActionProfiles — just extends the Destination field.
- ✅ Requires safe templating only, NOT arbitrary code execution — whitelist templates.
- ✅ Does NOT duplicate existing settings — folder picker already exists; this just makes it smarter.
- ✅ Safe: template resolution failures fall back to prompting user.

**Complexity:** M (templating adds validation work)

---

### 4. Multi-Step Actions

**User Scenario:**  
A photographer wants to "Accept & Backup" in one keystroke: move to Finals folder AND copy to an external backup folder. Currently, they need two separate actions and two key presses.

**Rough Mechanism:**  
- Extend `ReviewAction` with optional `SecondaryAction : ReviewAction?` field.
- If set, the shortcut executes the primary action first, then the secondary action on the same file.
- Both actions share the same file; the secondary target is independent of the primary.
- UI: ActionProfilesWindow shows an optional "Secondary Action" section when editing a profile.
- Fallback: if either action fails, the first is already committed (journal entry exists), so the secondary skip is safe.

**Self-Critique:**  
- ✅ Does NOT duplicate ActionProfiles — just adds an optional link to another profile.
- ✅ Does NOT require arbitrary script execution — just sequential action calls.
- ✅ Does NOT duplicate existing settings — multi-step operations are new.
- ✅ Safe: journal already handles partial failures; this is just two sequential ops in one key press.

**Complexity:** M

---

### 5. Keyboard Chording: Multi-Key Shortcuts

**User Scenario:**  
A power user wants to use a two-key sequence to save keyboard real estate: "Z" then "1" to zoom 100%, "Z" then "2" to zoom 200%. Or "A" then "K" to "Accept & Keep", "A" then "R" to "Accept & Review".

**Rough Mechanism:**  
- Extend `ShortcutMappings` to allow optional two-key sequences.
- Syntax: `"Z+1"` or `"Z,1"` or just `"Zx1"` (TBD).
- On first key press, enter "chord mode" (visual hint in status bar: "Waiting for Z…").
- Second key press completes the chord and executes the action.
- Timeout after N seconds (e.g., 2s) to avoid getting stuck.
- Don't chord common modifiers (Ctrl, Shift, Alt) — only letter/number/symbol chords.

**Self-Critique:**  
- ✅ Does NOT duplicate ActionProfiles — just adds a shortcut syntax.
- ✅ Does NOT require arbitrary script execution — chord is just a shortcut syntax.
- ✅ Does NOT duplicate existing settings — multi-key shortcuts don't exist yet.
- ⚠️ Minimalist defaults: This adds complexity to the Shortcuts page UI. Could be a power-user-only feature flag (off by default).

**Complexity:** M (UI state machine for chord mode)

---

### 6. Session Auto-Load on Folder

**User Scenario:**  
When a photographer opens a folder in `C:\Shoots\2026-09\Fashion\Finals`, they want to auto-load the "Fashion Finals Cull" session preset based on the folder path pattern. This combines Idea #1 (Session Presets) with #2 (Folder Patterns).

**Rough Mechanism:**  
- Extend `ReviewSessionPreset` with optional `FolderPatternToAutoLoad : string`.
- On folder open, scan presets and auto-load the first matching pattern.
- Optional visual hint: "Auto-loaded preset: Fashion Finals Cull" in the status bar.
- User can override by manually loading a different preset.

**Self-Critique:**  
- ✅ Builds on #1 and #2, no duplication.
- ✅ Pattern-based, safe, no arbitrary execution.
- ✅ Safe fallback: if no pattern matches, just open with current settings.

**Complexity:** S (small — just adds a pattern field to Idea #1)

---

### 7. Review Workflow Templates

**User Scenario:**  
A photographer buys a curated "Wedding Photography" workflow: pre-configured shortcuts (fast navigate, quick accept/reject), action profiles (Selects, Secondaries, Rejects), window layout, zoom defaults. Instead of learning all settings from scratch, they import the template bundle with one click.

**Rough Mechanism:**  
- Workflow templates are `.json` files (or `.zip` bundles) containing curated `AppSettings` snapshots + action profiles + review presets.
- Settings > General > "Import Workflow Template" button.
- Parse template, show preview of what will be applied, then import all at once.
- Templates are opt-in; default app has no templates built-in (avoid bloat).
- Community-shared workflows can be posted in docs (user-supplied, not code-signed).

**Self-Critique:**  
- ✅ Does NOT duplicate ActionProfiles or Presets — just bundles them.
- ✅ Safe: templates are just curated `AppSettings`, no code execution.
- ✅ Does NOT duplicate existing settings — workflow bundles are new.
- ⚠️ Lower priority than #1–5; solves onboarding, not power-user automation.
- ⚠️ Minimalist defaults: Don't ship templates; let users create/share them.

**Complexity:** M

---

## Dropped Ideas: Why They Failed Self-Critique

### ❌ Action Aliases / Templates (Redundant)
- **Issue:** Identical to creating multiple ReviewAction profiles with different names. Just use ActionProfiles.
- **Example:** Instead of an "alias" that points to "Keep", create a profile named "Keep" directly (already possible).

### ❌ Conditional Smart Rules (Script Execution Risk)
- **Issue:** Would require evaluating rules like `"if file size > 10MB then move to Large, else move to Small"`. Even with a "safe" DSL, this introduces:
  - UI complexity (rule builder or text DSL).
  - Parsing bugs (regex-like attack surface).
  - Debugging nightmares (what rule fired? why?).
  - Desktop app stability risk (a bad rule can hang/crash).
- **Verdict:** Security/stability risk for minimal benefit — users can just use multiple action profiles and navigate manually.

### ❌ Rating/Tagging System (Out of Scope)
- **Issue:** Separate from action profiles. Photographers want *metadata*, not just file moves.
- **Better Approach:** Extend ActionProfiles to support writing sidecar `.xmp` files or `Exif` stars (separate feature, big scope).
- **Verdict:** Out of scope for this brainstorm (custom actions, not metadata).

### ❌ Action History / Undo Profiles (Partially Existing)
- **Issue:** Undo is already limited to current session (P03 decision).
- **Status:** Undo beyond session was explicitly declined; don't re-propose.

### ❌ Arbitrary Script Execution (Security/Stability)
- **Issue:** Users request `.lua` / `.bat` / `.ps1` support. But:
  - Desktop app touches the file system directly (destructive operations).
  - Scripts can easily break (syntax errors, file-not-found, infinite loops).
  - Security risk: user could paste a malicious script.
  - Stability risk: bad script hangs the review loop.
- **Verdict:** Not happening in this architecture. Use safe, constrained mechanisms instead (templates, chords, presets).

---

## Summary Table

| Feature | Scenario | Mechanism | Survives Critique | Complexity |
|---------|----------|-----------|-------------------|-----------|
| Review Session Presets | Cull fashion shoot the same way each time | Save/load session context (folder, zoom, profiles, window state) | ✅ Yes | M |
| Folder-based Action Selection | Auto-pick action profiles by folder name pattern | Pattern field in ReviewAction; glob matching on folder open | ✅ Yes | M |
| Template Destinations | Move to `{date-taken}/Finals` instead of hardcoding | Safe template syntax in ReviewAction.Destination | ✅ Yes | M |
| Multi-Step Actions | Accept & Backup in one keystroke | Optional SecondaryAction field in ReviewAction | ✅ Yes | M |
| Keyboard Chording | Z+1 for Zoom 100%, Z+2 for Zoom 200% | Two-key syntax in ShortcutMappings; UI chord-mode state | ✅ Yes | M |
| Session Auto-Load on Folder | Load "Fashion Finals" preset when opening that folder | Pattern field in ReviewSessionPreset | ✅ Yes | S |
| Workflow Templates | Import pre-curated action profiles + shortcuts + presets | `.json` bundle; import via Settings dialog | ✅ Yes | M |
| *Dropped: Aliases* | Create "Keep" + "Keep (Confirm)" profiles | — | ❌ Redundant | — |
| *Dropped: Smart Rules* | `if file_size > 10MB then move to Large` | — | ❌ Script risk | — |
| *Dropped: Rating/Tags* | Write `.xmp` metadata; star ratings | — | ❌ Out of scope | — |
| *Dropped: Undo Profiles* | Save undo history across sessions | — | ❌ Explicitly declined (P03) | — |

---

## Next Steps (If Approved)

1. **Priority ranking:** User picks which ideas to pursue first. Recommend: Presets (#1) → Folder Patterns (#2) → Templates (#3) for immediate power-user wins.
2. **Design phase:** For each approved idea, detail the `.cs` changes, UI additions, and persistence layer (AppSettings fields).
3. **Phased rollout:** Don't implement all at once. Each could be a separate PR with its own decision doc.
4. **Testing strategy:** Each feature needs integration tests (file ops with presets, pattern matching, template validation).

---

## Notes on Architecture Consistency

- **No parallel systems:** All ideas extend `ReviewAction`, `AppSettings`, or `ShortcutMappings` — no new top-level concepts.
- **JSON persistence:** All new data lives in `AppSettings` and round-trips through `AppSettingsJsonContext`, just like existing fields.
- **Safe defaults:** Features are opt-in; minimalist defaults unchanged.
- **No runtime complexity:** No thread pools, event subscriptions, or new background tasks required.
