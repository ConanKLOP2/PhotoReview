using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>The data-safety invariants every crash/fault scenario must satisfy (see <see cref="FaultInjectionMatrix"/>).</summary>
internal static class FaultInvariants
{
    private const string Foreign = @"C:\elsewhere";

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The source of a Move is still there. A case-only rename (<c>a.jpg</c> to <c>A.jpg</c>) is the same entry under both spellings, so
    /// "still there" means the entry is still stored under the SOURCE spelling (the disk's real stored names, not a case-insensitive exists).
    /// </summary>
    internal static bool SourceStillThere(FaultRig rig, string source, string? destination)
    {
        if (!rig.Disk.FileExists(source)) return false;
        if (destination is null || string.Equals(source, destination, StringComparison.Ordinal) || !Same(source, destination)) return true;
        var stored = rig.Disk.GetStoredFileNames(source);
        return stored.Count != 1 || !string.Equals(stored[0], Path.GetFileName(destination), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Snapshot(FaultRig rig, params string[] extraFolders) =>
        rig.Fs.Snapshot(FaultRig.Folders.Concat(extraFolders));

    /// <summary>True when a non-Copy operation (a later Delete/Move of the same file, in a "Copy then Delete" scenario) legitimately removed the source.</summary>
    private static bool SourceLeftByLaterOp(FaultRig rig, string source) =>
        rig.JournalLines().Any(line => line.Type != FileOperationType.Copy && FaultRig.MembersOf(line).Any(member => Same(member.Source, source)));

    private static bool IsPermanentlyDeleted(FaultRig rig, string path) => rig.Bin.PermanentlyDeleted.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>The effect a SUCCESS report (whole action, or one completed group member) promised, on the real disk.</summary>
    public static void CheckSucceededEffect(FaultRig rig, FileOperationType operation, string source, string? destination,
        string ctx, List<string> violations, bool permanent = false)
    {
        switch (operation)
        {
            case FileOperationType.Move:
                if (SourceStillThere(rig, source, destination)) violations.Add($"{ctx}: Move reported done but the source {source} is still there");
                if (destination is null || !rig.HasFull(destination)) violations.Add($"{ctx}: Move reported done but {destination} is not the complete file");
                break;
            case FileOperationType.Copy:
                if (destination is null || !rig.HasFull(destination)) violations.Add($"{ctx}: Copy reported done but {destination} is not the complete file");
                if (!SourceLeftByLaterOp(rig, source) && !rig.HasFull(source)) violations.Add($"{ctx}: Copy reported done but the source {source} is missing or damaged");
                break;
            case FileOperationType.Recycle:
                if (rig.Disk.FileExists(source)) violations.Add($"{ctx}: Delete reported done but {source} is still in the folder");
                if (permanent)
                {
                    if (!IsPermanentlyDeleted(rig, source) || rig.BinCount(source) != 0)
                        violations.Add($"{ctx}: permanent Delete reported done but {source} was not deleted permanently exactly like that");
                }
                else if (rig.BinCount(source) != 1) violations.Add($"{ctx}: Delete reported done but {source} is not (exactly once) in the Recycle Bin");
                break;
        }
    }

    /// <summary>
    /// State invariants over the disk, the bin and the journal: no photo lost, nothing moved/created without a journal line, nothing left
    /// Prepared after recovery, and every Committed line is true on the disk.
    /// </summary>
    public static void CheckState(FaultRig rig, string ctx, List<string> violations, bool afterRecovery)
    {
        var files = Snapshot(rig);
        var lines = rig.JournalLines();
        var latest = rig.Latest();

        if (afterRecovery)
        {
            foreach (var pending in latest.Values.Where(e => e.State == JournalState.Prepared))
                violations.Add($"{ctx}: {pending.Type} of {pending.Source} stayed Prepared after reconcile");
        }

        // NO LOSS / NO SILENT OVERWRITE: every photo exists complete somewhere (folder or bin). Unique contents make an
        // overwritten or truncated-and-forgotten photo show up as a missing token.
        foreach (var name in rig.Names)
        {
            var content = rig.ContentOf(name);
            var inFolder = files.Values.Any(text => text == content);
            var inBin = rig.Bin.Items.Any(item => item.Text == content);
            // A permanent delete (volume without a bin, user opted in) is the one INTENDED loss.
            var gonePermanently = rig.Bin.PermanentlyDeleted.Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
            if (!inFolder && !inBin && !gonePermanently) violations.Add($"{ctx}: PHOTO LOST: no complete {name} anywhere (folders or Recycle Bin)");
        }

        // NOTHING HAPPENS WITHOUT A JOURNAL LINE: a seed file that left its place, and every file at a place that is not a seed
        // place, must be named by some journal line (Prepared is written before the first mutation).
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            foreach (var member in FaultRig.MembersOf(line))
            {
                named.Add(member.Source);
                if (member.Destination is not null) named.Add(member.Destination);
            }
        }

        foreach (var seed in rig.SeedPaths)
        {
            if (!rig.HasFull(seed) && !named.Contains(seed))
                violations.Add($"{ctx}: {seed} left its place with NO journal line (unjournaled mutation)");
        }

        foreach (var path in files.Keys)
        {
            if (!rig.SeedPaths.Contains(path, StringComparer.OrdinalIgnoreCase) && !named.Contains(path))
                violations.Add($"{ctx}: {path} exists but no journal line mentions it (unjournaled mutation/partial file)");
        }

        // EVERY JOURNAL VERDICT IS TRUE.
        foreach (var entry in latest.Values.Where(e => e.State != JournalState.Dismissed))
        {
            // An undo (journaled for Move and group Delete) legitimately reverses its original line, which stays Committed.
            var supersededByUndo = entry.Undo != true && entry.Type != FileOperationType.Copy && lines.Any(u =>
                u.Undo == true && u.Id != entry.Id && u.Type == entry.Type && FaultRig.MembersOf(u).Any(um =>
                    FaultRig.MembersOf(entry).Any(em => Same(um.Source, entry.Type == FileOperationType.Move ? em.Destination : em.Source))));
            foreach (var member in FaultRig.MembersOf(entry))
            {
                // FEAT-RENAME: a Move line whose destination was moved/renamed on by a LATER line of the journal (rename twice, or
                // the same rename re-done by a Recovery retry after its Undo) no longer describes the disk; the later line does.
                var movedOn = entry.Type == FileOperationType.Move && member.Destination is not null
                    && lines.FindLastIndex(l => l.Id == entry.Id) is var entryAt
                    && latest.Values.Any(other => other.Id != entry.Id && other.Type == FileOperationType.Move
                        && other.State is JournalState.Committed or JournalState.Prepared or JournalState.Failed
                        && lines.FindLastIndex(l => l.Id == other.Id) > entryAt
                        && FaultRig.MembersOf(other).Any(om => Same(om.Source, member.Destination) && !string.Equals(om.Source, om.Destination, StringComparison.Ordinal))
                        && (other.State == JournalState.Committed || !rig.Disk.FileExists(member.Destination)));
                switch (entry.Type)
                {
                    case FileOperationType.Move when entry.State == JournalState.Committed && !supersededByUndo && !movedOn:
                        if (SourceStillThere(rig, member.Source, member.Destination)) violations.Add($"{ctx}: Move {member.Source} is Committed but the source is still there");
                        if (member.Destination is null || !rig.HasFull(member.Destination)) violations.Add($"{ctx}: Move {member.Source} is Committed but {member.Destination} is not the complete file");
                        break;
                    case FileOperationType.Copy:
                        if (!SourceLeftByLaterOp(rig, member.Source) && !rig.HasFull(member.Source)) violations.Add($"{ctx}: Copy of {member.Source} (state {entry.State}) left its source missing or damaged");
                        if (entry.State == JournalState.Committed && (member.Destination is null || !rig.HasFull(member.Destination)))
                            violations.Add($"{ctx}: Copy {member.Source} is Committed but {member.Destination} is not the complete file");
                        break;
                    case FileOperationType.Recycle:
                        // The photo is in exactly one place (folder or bin) in EVERY state: never both, never neither.
                        var permanentDelete = entry.Permanent == true || member.Permanent;
                        var places = (rig.HasFull(member.Source) ? 1 : 0) + (permanentDelete ? (IsPermanentlyDeleted(rig, member.Source) ? 1 : 0) : rig.BinCount(member.Source));
                        if (permanentDelete && rig.BinCount(member.Source) != 0) violations.Add($"{ctx}: permanent Delete of {member.Source} put a copy in the Recycle Bin");
                        // Another JOURNALED operation of the same file may have legitimately put it elsewhere meanwhile (a group Move that a
                        // Recovery retry finished after the user's Delete of the same capture failed): that destination is then its place.
                        if (places == 0 && lines.Any(other => other.Type == FileOperationType.Move && other.Id != entry.Id && FaultRig.MembersOf(other).Any(om =>
                                Same(om.Source, member.Source) && om.Destination is not null && rig.HasFull(om.Destination))))
                            places = 1;
                        if (places != 1) violations.Add($"{ctx}: Delete of {member.Source} (state {entry.State}, undo={entry.Undo}) leaves the photo in {places} places");
                        // The verdict is true: Committed means it left the folder (a single Delete that Ctrl+Z restored is not journaled again).
                        if (entry.State == JournalState.Committed && entry.Undo != true && !supersededByUndo && !rig.SingleRecycleMayBeRestored && rig.Disk.FileExists(member.Source))
                            violations.Add($"{ctx}: Delete of {member.Source} is Committed but the file is still in the folder");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// What a user does after the failure, in a healthy new process: retry every Recovery item, then try to move a DIFFERENT photo that
    /// has the same file name into the same folder.
    /// </summary>
    public static async Task FollowUpsAsync(FaultRig rig, string ctx, List<string> violations)
    {
        var retry = new RecoveryRetryService(rig.Journal, rig.Fs, rig.Clock, rig.Bin);
        foreach (var failed in rig.Journal.ReadFailedOperations().ToList())
        {
            try
            {
                _ = await retry.RetryMoveOrCopyAsync(failed);
            }
            catch (Exception ex)
            {
                violations.Add($"{ctx}: Recovery retry of {failed.Type} {failed.Source} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        CheckState(rig, ctx + " (after retrying every Recovery item)", violations, afterRecovery: true);
        var settled = rig.JournalText();
        _ = rig.Journal.ReconcilePendingOperations();
        if (rig.JournalText() != settled) violations.Add($"{ctx}: reconcile after the retries appended lines");

        foreach (var name in new[] { "a.jpg", "a.cr2", "a.xmp", "b.jpg" })
        {
            var before = Snapshot(rig, Foreign);
            var foreignPath = Path.Combine(Foreign, name);
            const string ForeignContent = "FOREIGN-PHOTO-THAT-MUST-SURVIVE";
            rig.Disk.AddFile(foreignPath, ForeignContent, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
            FileActionResult? result = null;
            try
            {
                result = await rig.Service.ExecuteAsync(new FileActionRequest(foreignPath, FileOperationType.Move, FaultRig.Sel));
            }
            catch (Exception ex)
            {
                violations.Add($"{ctx}: moving a same-named foreign photo threw {ex.GetType().Name}: {ex.Message}");
            }

            var after = Snapshot(rig, Foreign);
            foreach (var (path, text) in before)
            {
                if (!after.TryGetValue(path, out var now) || now != text)
                    violations.Add($"{ctx}: moving a same-named foreign photo (success={result?.Succeeded}) changed or removed {path}: SILENT OVERWRITE/LOSS");
            }

            if (!after.Values.Any(text => text == ForeignContent))
                violations.Add($"{ctx}: the foreign photo {name} was lost by its own Move");
            rig.Disk.Delete(foreignPath);
            var movedForeign = Path.Combine(FaultRig.Sel, name);
            if (rig.Disk.FileExists(movedForeign) && rig.Disk.ReadAllText(movedForeign) == ForeignContent) rig.Disk.Delete(movedForeign);
        }
    }
}
