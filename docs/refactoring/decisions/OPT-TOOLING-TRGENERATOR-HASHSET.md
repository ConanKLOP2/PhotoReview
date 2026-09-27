---
id: OPT-TOOLING-TRGENERATOR-HASHSET
order: 31
summary: |-
  TrGenerator.TryGetPlaceholders now dedups placeholder names with a HashSet instead of List.Contains, removing the O(n^2) scan (perf/trgenerator-hashset-placeholders).
---

# OPT-TOOLING-TRGENERATOR-HASHSET -- TrGenerator placeholder dedup

Scoped and approved in docs/refactoring/decisions/OPT-PROPOSALS-TOOLING.md (not yet merged to master as of this PR; from branch opt-proposals/tooling) (Low risk, Medium impact): `TrGenerator.TryGetPlaceholders`
(`src/PhotoReview.Localization.Generator/TrGenerator.cs`) deduplicated placeholder names found in each localization string with
`List<string>.Contains(name)` inside the scan loop -- O(n^2) for a string with n unique placeholders. This method runs once per
localization catalog key (~1000+ times per build, every build, incremental or full), so the wasted comparisons compound across
build cycles even though no single call is a bottleneck on its own.

**Fix**: added a `HashSet<string> seen` (ordinal comparer, matching the rest of the file's string comparisons) alongside the
existing `List<string> names`. The loop now does `if (seen.Add(name)) names.Add(name);` instead of
`if (!names.Contains(name)) names.Add(name);` -- O(1) membership check and insert instead of an O(n) scan, so the method is O(n)
overall instead of O(n^2). `names` is still returned as a `List<string>` in first-appearance order (unchanged return type and
ordering contract used by callers such as `KeyInfo.Placeholders` and the plural-parameter merge in `Execute`).

Behavior is identical: same set of placeholder names, same order, same validation and diagnostics. Verified with `dotnet test`
on `PhotoReview.Core.Tests` (`Localization` filter, 221 tests, all passing) both with the `HashSet` fix and with the fix
temporarily reverted back to `List.Contains` (mutation check) -- no test in the suite distinguishes the two implementations,
which is expected for a pure data-structure swap with no behavior change. Full `dotnet build -c Release` of
`PhotoReview.App.csproj` also passes with 0 warnings/errors.

Not touched: the similar `!parameters.Contains(name)` check in `Execute`'s plural-parameter merge (line ~135) is out of scope --
`OPT-PROPOSALS-TOOLING.md` only scoped the `TryGetPlaceholders` finding, and that other list is bounded by the small number of
plural parameters per key, not by the same per-character scan pattern.