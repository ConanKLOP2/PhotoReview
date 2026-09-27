# Whole-folder preload on F4 (Q-R17, Q-R26; 2026-09-26)

Q-R17 keeps the whole 13-16 GB folder in ~10 GB of RAM (AGENTS.md rule 2). First-visual P50/P95 was 2.5-3x the window mode (4.4/8.6 vs 1.6/2.8 ms). Ruled out: GC,
page faults, memory compression, decode contention. **Cause:** the synchronous `preloadKick` after each assign built a cache key and looked up every image in the
folder on the UI thread (P50 2.0-2.8 ms vs 0.25-0.37 ms). **Fix:** yield to the thread pool at the first candidate outside the 32/8 window (`PreloadKickOffCallerTests`);
`preloadKick` P50 0.13-0.26 ms, S2 first P50/P95 2.0/4.9 vs 1.9/3.6 ms in window mode, whole folder still preloaded (peak WS 10.8 GB).

Natural sort / Explorer snapshot validator (idle PC, old = `*Reference` oracles): `TryValidate` 50k 111 -> 47 ms (x2.36), `Array.Sort` 50k 655 -> 412 ms (x1.59), allocations -50 % to -99 %.
