# Kinetic pan / glide (2026-09-26, no app-side stutter cause found)

`KineticPanFrameMeasurementTests` (Manual): UI cost per frame 0.4-0.8 ms, no GC during glides. The judder is frame pacing (DWM/WPF with a mixed 240/60 Hz, hybrid-GPU setup); a
control scene without the photo behaves the same. Tried without effect: 1 ms timer resolution, thread priorities, extra render ticks. Not done: flip-model D3D11/DirectComposition swap chain (large, risky),
pointer interpolation on drag. Shipped: `KineticGlideSmoothing` (default `Predict`): vblank-aligned steps (`WindowsDisplayClock`, `VBlankEstimator`, `GlideFrameClock`); on a 59.94 Hz monitor speed error RMS
0.69 -> 0.11 and judder 43 -> 12 frames/s, on 240 Hz speed jumps drop but the hold pattern stays. 75/144 Hz only simulated in unit tests.
