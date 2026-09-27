---
id: LEDGER-BATCH-SRC-TOOLS-LOC
order: 999
summary: Cost-optimized audit of 220 function bodies in PhotoReview.Localization.Generator and PhotoReview.Benchmark.Cli; no defects found.
---

# Ledger Batch: src/PhotoReview.Localization.Generator + tools/PhotoReview.Benchmark.Cli

**Audit date:** 2026-09-28  
**Snapshot commit:** 3ef2bb5 (reviewed against origin/master current state)  
**Method:** manual-scan-cost-optimized (static span review + targeted CA1305 / I/O / exception-safety focus)

## Batch Summary

- **Total rows:** 220 function bodies
- **Rows reviewed:** 220
- **Issues found:** 0
- **Fixes made:** 0

## Scope

Two code directories:

1. **src/PhotoReview.Localization.Generator/** (rows 579–631, 53 rows)
   - FlatJsonReader.cs: JSON catalog parser (constructors, parsing, validation)
   - TrGenerator.cs: C# source generator for localization code (generation methods, formatting, identification)

2. **tools/PhotoReview.Benchmark.Cli/** (rows 2272–2447, 167 rows)
   - BenchmarkCliArguments.cs: CLI argument parsing
   - DecoderBenchmark.cs: Image decoder performance measurement and reporting
   - GcEventRecorder.cs: GC event listener and CSV logging
   - IoDecodeSplit.cs: I/O + decode split benchmarking
   - LocalImageBenchmark.cs: Local image decode workload
   - LocalUiNextProbe.cs: UI responsiveness probing
   - PerfSession.cs: End-to-end performance session runner
   - ProcessPageFaults.cs: Page fault counter
   - Program.cs: CLI entry point
   - WpfTestHost.cs: WPF test harness

## Key Findings

### CA1305 (Culture-Invariant Formatting) - All Clear

All methods writing diagnostic output (CSV, console, markdown) properly use `CultureInfo.InvariantCulture`:

- **DecoderBenchmark.cs:**
  - WriteDetailsCsvAsync (line 470–485): uses `CultureInfo.InvariantCulture` for all numeric formatting in CSV
  - PrintConsoleSummaryTable (line 449–468): uses `CultureInfo.InvariantCulture` for console output
  - FormatSpeedup, FormatBytes: use invariant culture

- **GcEventRecorder.cs:**
  - WriteCsv (line 107–134): uses `CultureInfo.InvariantCulture` for all ticks and numeric fields
  - OnEventWritten: uses invariant for payload conversions

- **IoDecodeSplit.cs:**
  - BuildSummary, MetricRow: use `CultureInfo.InvariantCulture` for all output

### Resource Management - No Leaks

- **PerfSession.RunIterationAsync (line 243–551):**
  - Proper using statements for Process, GcEventRecorder
  - Window.Close() called in finally block
  - DeleteTempDir called for cleanup
  - No unclosed streams or file handles

- **IoDecodeSplit.MeasureFile (line 86–172):**
  - FileStream disposed in finally/try-catch blocks
  - StringBuilder used appropriately for accumulation

- **LocalImageBenchmark.RunAsync (line 11–55):**
  - FileStream operations within try-finally or using statements
  - BitmapImage.Freeze() called to release resources

### Exception Safety - Proper Handling

- **PerfSession.RunAsync (line 144–239):**
  - try-catch-finally with proper cleanup in finally
  - DeleteTempDir with retry-friendly broad catch (expected for transient locks)

- **Program.cs top-level statements (line 13–76):**
  - Broad catch blocks for user-facing error reporting (appropriate for CLI entry point)
  - Diagnostic output on error

- **FlatJsonReader.cs:**
  - Custom FlatJsonException with position tracking for diagnostics
  - Proper null/empty/format validation before operations

### Long Methods - Verified Structure

- **PerfSession.RunIterationAsync (325 lines):**
  - Clear control flow with switch/case for step kinds
  - Proper async/await with no fire-and-forget tasks
  - Metrics capture paired with step execution
  - No deadlock patterns detected

### Allocation / Benchmarking Patterns - Expected

- LINQ ToList/ToArray in benchmark summarization: intentional (small result sets, one-time summaries)
- StringBuilder accumulation for CSV: appropriate for multi-line output
- OrderBy/Where in result filtering: proportional to benchmark data (typically 100–1000s of records)

## Conclusion

**No defects found.** All 220 rows passed review:

- Code follows AGENTS.md conventions (strict CA1305, proper resource disposal, exception safety).
- No off-by-one errors, null-dereference paths, or resource leaks detected.
- Diagnostic output (CSV, console, markdown) correctly uses invariant culture.
- Async patterns are safe (no fire-and-forget, proper cancellation).
- All rows marked as `no-change` status.
