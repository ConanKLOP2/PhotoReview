# SEC-02 — external static review of `InstanceForwardClient`/`ForwardedPathProtocol`

External static review (2026-09-27) raised two claims about the Q-R10 second-instance forwarding path. Both investigated with real evidence before deciding whether to fix, per the lead session's request.

## Claim A — `SendAsync`'s `written` flag: `WriteAsync` succeeds, `FlushAsync` fails → misreported as `NoInstance`

**Verdict: false positive. No source change.**

The claim was that `InstanceForwardClient.SendAsync` (`src/PhotoReview.Platform.Windows/InstanceForwardClient.cs`) sets `written = true` only after both `pipe.WriteAsync` and `pipe.FlushAsync` succeed, so a `WriteAsync` that reaches the server followed by a `FlushAsync` that then throws would report `NoInstance` (nobody home) instead of `Unknown`/`Delivered`, even though the server may already have the request.

Read the actual .NET runtime source for `PipeStream` (`dotnet/runtime`, `src/libraries/System.IO.Pipes/src/System/IO/Pipes/PipeStream.Win32.cs`):

```csharp
public override void Flush()
{
    CheckWriteOperations();
    // Does nothing on PipeStreams...
}

public override Task FlushAsync(CancellationToken cancellationToken)
{
    try { Flush(); return Task.CompletedTask; }
    catch (Exception ex) { return Task.FromException(ex); }
}
```

and `CheckWriteOperations()` (`PipeStream.cs`):

```csharp
protected internal void CheckWriteOperations()
{
    if (_state == PipeState.WaitingToConnect) throw new InvalidOperationException(...);
    if (_state == PipeState.Disconnected) throw new InvalidOperationException(...);
    if (CheckOperationsRequiresSetHandle && _handle == null) throw new InvalidOperationException(...);
    if (_state == PipeState.Broken) throw new IOException(SR.IO_PipeBroken);
    if ((_state == PipeState.Closed) || (_handle != null && _handle.IsClosed)) throw Error.GetPipeNotOpen();
}
```

`PipeStream` never overrides `Flush`/`FlushAsync` to do any I/O beyond this — the comment ("Does nothing on PipeStreams...") reflects that calling `FlushFileBuffers` on a named pipe could deadlock the other end, so the BCL deliberately made pipe flush a pure local-state check. `Write`/`WriteAsync` on `PipeStream` call into the OS `WriteFile` per invocation (no internal user-mode write buffer to flush) — there is nothing for `Flush` to push out.

Consequences for this claim:

- `FlushAsync` can only throw by inspecting the client's own cached `_state` field (`WaitingToConnect`/`Disconnected`/`Broken`/`Closed`) — it performs no syscall and cannot independently discover a transport failure the preceding `WriteAsync` didn't already see.
- For `FlushAsync` to throw where `WriteAsync` just returned successfully, `_state` would have to flip to `Broken`/`Closed`/`Disconnected` in the gap between the two `await`s with no I/O in between to cause that flip — not reachable via any code path in `PipeStream`. The only way `_state` changes between those two calls is another thread calling `Dispose()`/`Close()` concurrently on the same client-owned pipe object, which this code never does (single sequential `await using` block, no other reference to `pipe` escapes).
- This matches the lead's tentative counter-argument exactly: named-pipe `FlushAsync` is a local no-op that cannot meaningfully fail independently of a `WriteAsync` that already succeeded.

No fake/mock `PipeStream` seam was introduced to "prove" a failure, because the investigation shows the failure mode described (write delivered, flush independently fails) has no code path in the BCL implementation to reproduce it honestly — a hand-rolled fake would only be asserting the test author's assumption, not real pipe behavior. `SendAsync`'s existing R08 comment (owner-accepted-then-hangs-up → `Unknown`, never a false `NoInstance` when data was truly delivered) remains accurate for the pipe-error catch block; this claim's specific write/flush split is not an additional gap.

## Claim B — `Encode` can produce a message `TryDecode`/`IsAcceptablePath` rejects

**Verdict: real. Fixed.**

`ForwardedPathProtocol.Encode` (`src/PhotoReview.Core/Instance/ForwardedPathProtocol.cs`) validated only path count (`MaxPaths`) and total encoded size (`MaxMessageBytes`). `TryDecode`'s `IsAcceptablePath` additionally rejects: empty/over-length paths, control characters, device/extended-length/NT-object namespace prefixes (`\\?\`, `\\.\`, `\??\` and `/` spellings), `..` segments, and non-fully-qualified paths.

Concrete reproduction (pre-fix): `ForwardedPathProtocol.Encode(["relative\\a.jpg"])` or `Encode(["C:\\a\tb.jpg"])` returned a well-formed byte message with no exception, but `TryDecode` on the receiving side rejected it (`IsAcceptablePath` returns `false` for a non-fully-qualified path or one containing a control character). Consequence: `InstanceForwardServer.HandleAsync` treats the whole message as unaccepted, replies `ERR`, `InstanceForwardClient.SendAsync` reports `ForwardOutcome.Rejected`, and `SecondInstanceHandoff.TryForwardAsync` returns `false` — the second instance falls back to opening its own window instead of handing off, even though the path the caller wanted forwarded was never inherently unopenable, just unencodable-safely by the protocol's own receiver-side rules. This is silent in the sense that the failure is only discovered after a full round trip over the pipe, not locally before ever sending.

Fix: `Encode` now calls the same `IsAcceptablePath` predicate `TryDecode` uses (extracted as one shared private method in `ForwardedPathProtocol`, called from both `Encode` and `TryDecode`), throwing `ArgumentException` for any path that would have been rejected on the receiving side. `InstanceForwardClient.SendAsync` already catches `ArgumentException` from `Encode` and returns `ForwardOutcome.Rejected` immediately, so behavior for a bad path is now a same-process, no-pipe-round-trip `Rejected` instead of a pipe round trip that ends the same way — same outcome, but fails fast and can never drift out of sync with `TryDecode` again since both call sites now share one validator.

Tests added (`tests/PhotoReview.Core.Tests/Instance/ForwardingCoreTests.cs`):
- `Protocol_Encode_RejectsWhatDecodeWouldReject` — theory covering control character, relative path, `..` segment, extended-length/device/NT-object prefixes, and empty string; asserts `Encode` now throws `ArgumentException` for each (pre-fix, none of these threw).
- `Protocol_Encode_OutputIsAlwaysAcceptedByDecode` — round-trip property test: several realistic accepted paths (spaces, Unicode, dots, hidden-file-style names, deep nesting) round-trip through `Encode` → `TryDecode` successfully.

Mutation check: reviewed by inspection against the pre-fix `Encode` (which had no path-shape validation at all) — every input in `Protocol_Encode_RejectsWhatDecodeWouldReject` previously encoded successfully (confirmed by reading the original method body, which only checked `paths.Count` and total byte length), so the new theory fails without the fix and passes with it.
