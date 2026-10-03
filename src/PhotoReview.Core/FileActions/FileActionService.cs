using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Dịch vụ điều phối thực thi các thao tác tệp tin (Move, Copy, Recycle) kèm ghi nhật ký (OperationJournal)
/// và cổng bảo vệ tránh thao tác đồng thời (INV-4).
/// </summary>
public sealed class FileActionService
{
    private readonly OperationJournal _journal;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly IRecycleBin _recycleBin;
    private readonly Func<string, string, Task>? _moveOverride;

    private int _inProgress;

    public FileActionService(
        OperationJournal journal,
        IFileSystem fileSystem,
        IClock clock,
        IRecycleBin recycleBin,
        Func<string, string, Task>? moveOverride = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _moveOverride = moveOverride;
    }

    /// <summary>
    /// Cho biết hiện tại có thao tác tệp tin nào đang chạy hay không.
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _inProgress) != 0;

    /// <summary>
    /// Thử khóa cổng thao tác tệp tin (INV-4). Trả về true nếu thành công khóa cổng.
    /// </summary>
    public bool TryBegin() => Interlocked.CompareExchange(ref _inProgress, 1, 0) == 0;

    /// <summary>
    /// Mở khóa cổng thao tác tệp tin sau khi hoàn thành.
    /// </summary>
    public void End() => Volatile.Write(ref _inProgress, 0);

    /// <summary>
    /// Q-R8: true when a Recycle of <paramref name="path"/> cannot go to a Recycle Bin (removable/network/unknown drive), i.e.
    /// it would be a PERMANENT delete. Callers use it to decide whether the user must confirm (and whether the setting applies).
    /// </summary>
    public bool LacksRecycleBin(string path) => !_recycleBin.CanRecycle(path);

    public bool FileExists(string path) => _fileSystem.FileExists(path);

    /// <summary>
    /// Executes one capture-group operation. Every member is preflighted before the group manifest is journaled;
    /// the manifest is Prepared before the first mutation and remains one Recovery item if any member fails.
    /// </summary>
    public async Task<CaptureGroupActionResult> ExecuteGroupAsync(
        CaptureGroupActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Group);
        var groupId = Guid.NewGuid().ToString("N");
        if (!TryBegin())
        {
            return new(false, true, request.Operation, groupId, null, [], Tr.CoreFileActionBusy);
        }

        JournalTransaction? tx = null;
        List<JournalGroupMember> manifest = [];
        var missingSources = new List<string>();
        // Members whose operation finished and verified, and the one being processed when the failure hit: the inputs of
        // the compensation below (Copy: delete the copies this operation made; Move: put the moved files back).
        var done = new List<JournalGroupMember>();
        JournalGroupMember? inFlight = null;
        // True only while a Copy of the in-flight member may have created its destination: proven by TryCopyNew (it returns
        // false, touching nothing, when the destination already existed; a throw means this call created/was creating it).
        var inFlightDestinationIsOurs = false;
        try
        {
            if (request.Group.Paths.Count < 2)
                throw new ArgumentException("A grouped file action requires at least two paths.", nameof(request));
            if (request.Operation is not (FileOperationType.Move or FileOperationType.Copy or FileOperationType.Recycle))
                throw new NotSupportedException(Tr.CoreFileActionUnsupportedOperation(request.Operation));

            var members = new List<JournalGroupMember>(request.Group.Paths.Count);
            string? destinationFolder = null;
            if (request.Operation is FileOperationType.Move or FileOperationType.Copy)
            {
                if (string.IsNullOrWhiteSpace(request.Destination))
                    throw new IOException(Tr.CoreFileActionNoDestination);
                if (ActionDestinationPolicy.Validate(request.Destination) != ActionDestinationCheck.Ok)
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);
                var firstSourceFolder = Path.GetFullPath(Path.GetDirectoryName(request.Group.Paths[0]) ?? string.Empty);
                destinationFolder = Path.GetFullPath(Path.IsPathRooted(request.Destination)
                    ? request.Destination
                    : Path.Combine(firstSourceFolder, request.Destination));
                if (IsSamePath(destinationFolder, firstSourceFolder))
                    throw new IOException(Tr.CoreFileActionSameFolder);
                if (!Path.IsPathRooted(request.Destination)
                    && !destinationFolder.StartsWith(firstSourceFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);
            }

            var imagePaths = request.Group.ImagePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var source in request.Group.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A stale group: a partner (RAW/JPEG/XMP) vanished outside the app. It is skipped and reported instead of
                // making the whole capture un-actionable; the action then covers the members that still exist.
                var stat = _fileSystem.GetFileStat(source);
                if (stat is null)
                {
                    missingSources.Add(source);
                    continue;
                }
                string? destination = null;
                if (destinationFolder is not null)
                {
                    var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source) ?? string.Empty);
                    if (!Path.IsPathRooted(request.Destination!)
                        && !string.Equals(sourceFolder, Path.GetFullPath(Path.GetDirectoryName(request.Group.Paths[0]) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
                        throw new JournalCodedException(JournalErrors.DestinationOutsideSource);
                    destination = Path.Combine(destinationFolder, Path.GetFileName(source));
                    if (_fileSystem.FileExists(destination))
                        throw new IOException(Tr.CoreFileActionDestinationExists(destination));
                    if (!Path.IsPathRooted(request.Destination!)
                        && ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(sourceFolder, destination, _fileSystem) != ActionDestinationCheck.Ok)
                        throw new JournalCodedException(JournalErrors.DestinationOutsideSource);
                }

                var permanent = false;
                if (request.Operation == FileOperationType.Recycle)
                {
                    permanent = !_recycleBin.CanRecycle(source);
                    if (permanent && !request.AllowPermanentDelete)
                        throw new IOException(Tr.CoreRecycleUnsupportedDrive(Path.GetFileName(source)));
                }

                members.Add(new JournalGroupMember(source, destination, stat.Length, stat.LastWriteUtc, permanent));
            }

            // Only a sidecar left (or nothing): the photo itself is gone, there is nothing to act on.
            if (!members.Any(member => imagePaths.Contains(member.Source)))
            {
                var gone = missingSources.FirstOrDefault(imagePaths.Contains) ?? request.Group.Paths[0];
                throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(gone), gone);
            }

            if (request.Operation == FileOperationType.Recycle)
            {
                // F-WIN-2 for the whole capture: the bin of a volume must hold ALL of that volume's members together,
                // otherwise the shell deletes the overflow permanently while the journal says "recycle".
                if (RecycleBinCapacity.FirstOverflow(_recycleBin, members) is { } overflow)
                    throw new IOException(Tr.CoreRecycleBinCannotHold(Path.GetFileName(overflow.Source)));
            }
            manifest = members;

            if (destinationFolder is not null) _fileSystem.CreateDirectory(destinationFolder);
            var firstMember = manifest[0];
            var prepared = new JournalEntry(
                Guid.NewGuid().ToString("N"), request.Operation, JournalState.Prepared,
                firstMember.Source, firstMember.Destination, firstMember.Size, firstMember.LastWriteUtc, _clock.UtcNow,
                Permanent: manifest.All(member => member.Permanent) && manifest.Any(member => member.Permanent) ? true : null,
                GroupId: groupId, GroupMembers: manifest);
            tx = new JournalTransaction(_journal, _clock, prepared);
            await tx.BeginAsync().ConfigureAwait(false);

            foreach (var member in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inFlight = member;
                inFlightDestinationIsOurs = false;
                if (request.Operation == FileOperationType.Copy)
                {
                    // Create-new copy: a file that appeared after the preflight is never overwritten and never claimed as ours,
                    // so the compensation cannot delete it. The flag is raised inside the delegate, right before the call, so a
                    // Task.Run cancelled before it starts never claims a destination (a throw after that may leave our partial file).
                    var created = await Task.Run(() =>
                    {
                        inFlightDestinationIsOurs = true;
                        return _fileSystem.TryCopyNew(member.Source, member.Destination!);
                    }, cancellationToken).ConfigureAwait(false);
                    if (!created)
                    {
                        inFlightDestinationIsOurs = false;
                        throw new IOException(Tr.CoreFileActionDestinationExists(member.Destination!));
                    }
                    VerifyGroupDestination(member);
                }
                else if (request.Operation == FileOperationType.Move)
                {
                    // A cross-volume Move is copy + delete: a failure in between can leave a partial destination. It is only ours
                    // to clean when nothing was at the destination right before this member's Move started.
                    inFlightDestinationIsOurs = _fileSystem.GetFileStat(member.Destination!) is null;
                    try
                    {
                        if (_moveOverride is not null)
                            await _moveOverride(member.Source, member.Destination!).ConfigureAwait(false);
                        else
                            await Task.Run(() => _fileSystem.Move(member.Source, member.Destination!), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception moveEx) when (FileSystemErrors.IsDestinationExists(moveEx))
                    {
                        // "Destination exists" is raised before the move touches anything: the file there appeared after our
                        // pre-check (someone else's) and is never ours to delete, whatever its size.
                        inFlightDestinationIsOurs = false;
                        throw;
                    }
                    VerifyGroupMove(member);
                }
                else if (member.Permanent)
                {
                    await Task.Run(() => _recycleBin.DeletePermanently(member.Source), cancellationToken).ConfigureAwait(false);
                    if (_fileSystem.FileExists(member.Source)) throw new JournalCodedException(JournalErrors.SourceStillExistsAfterRecovery);
                }
                else
                {
                    await Task.Run(() => _recycleBin.SendToRecycleBin(member.Source), cancellationToken).ConfigureAwait(false);
                    if (_fileSystem.FileExists(member.Source)) throw new JournalCodedException(JournalErrors.SourceStillExistsAfterRecovery);
                }
                done.Add(member);
                inFlight = null;
            }

            tx.MarkMutationCompleted();
            var committed = tx.Commit(out var commitError);
            var complete = manifest.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray();
            return new(true, false, request.Operation, groupId, committed, complete, null,
                JournalPersisted: commitError is null, JournalError: commitError,
                PermanentlyDeleted: request.Operation == FileOperationType.Recycle && manifest.Any(member => member.Permanent),
                SkippedMissing: missingSources.Count > 0 ? missingSources : null);
        }
        catch (Exception ex)
        {
            try
            {
                // Compensate first (the outcome decides what the journal says), then inspect the real state.
                var stuck = 0;
                if (tx is { IsPrepared: true } && request.Operation is FileOperationType.Move or FileOperationType.Copy)
                {
                    stuck = await Task.Run(() => request.Operation == FileOperationType.Copy
                        ? RemoveCreatedCopies(done, inFlight, inFlightDestinationIsOurs)
                        : RestoreMovedMembers(done, inFlight, inFlightDestinationIsOurs)).ConfigureAwait(false);
                }

                var states = manifest.Select(member => InspectGroupMember(request.Operation, member, ex.Message)).ToArray();
                // Only the message shown to the user carries the (localized) rollback note; the journal always keeps the ORIGINAL
                // failure (invariant code + English text, so Recovery can localize it).
                var message = stuck > 0 ? Tr.CoreGroupActionRollbackFailed(ex.Message, stuck) : ex.Message;

                // Fully rolled back = every member is provably back in its original state (source untouched, nothing at the
                // destination). Nothing is left to retry, so it is not a Recovery item (an unconditional Failed record would
                // offer "retry" for an operation the user cancelled that left the disk unchanged).
                var rolledBack = stuck == 0 && states.Length > 0 && tx is { IsPrepared: true }
                    && request.Operation is FileOperationType.Move or FileOperationType.Copy
                    && states.All(state => state is { StateKnown: true, Completed: false, Conflict: false, SourceExists: true, DestinationExists: false });
                string? journalError = null;
                var failed = rolledBack ? tx!.DismissRolledBack(out journalError) : tx?.Fail(ex, out journalError);
                return new(false, false, request.Operation, groupId, failed, states, message,
                    JournalPersisted: journalError is null,
                    JournalError: journalError,
                    PermanentlyDeleted: request.Operation == FileOperationType.Recycle && states.Any(member => member.Completed && member.Member.Permanent),
                    SkippedMissing: missingSources.Count > 0 ? missingSources : null);
            }
            catch (Exception compensationFailure) when (compensationFailure is not OutOfMemoryException)
            {
                // The compensation or the state inspection itself failed with a type it does not expect (a fake, a filter driver, ...).
                // It must not escape to the caller: no outcome line is written, so the Prepared line stays and startup reconcile judges
                // the disk later; every member is reported as not completed with an unproven state (never as cleanly rolled back).
                var unknown = manifest.Select(member => new CaptureGroupMemberResult(member, false, true, ex.Message, StateKnown: false)).ToArray();
                return new(false, false, request.Operation, groupId, null, unknown,
                    Tr.CoreGroupActionRollbackFailed(ex.Message, manifest.Count),
                    PermanentlyDeleted: false,
                    SkippedMissing: missingSources.Count > 0 ? missingSources : null);
            }
        }
        finally
        {
            tx?.Dispose();
            End();
        }
    }

    /// <summary>
    /// Copy compensation: deletes the destination files this operation created so a failed or cancelled Copy leaves no
    /// half capture behind (and a re-run is not blocked by the "destination exists" preflight). A verified copy is only
    /// deleted while it still has the size that was copied; the member being copied when the failure hit is only touched
    /// when <see cref="IFileSystem.TryCopyNew"/> proved this operation created its destination (so it is our partial file,
    /// never a foreign one that appeared between the preflight and the copy).
    /// Sources and the Recycle Bin are never touched. Returns how many files could not be cleaned up.
    /// </summary>
    private int RemoveCreatedCopies(IReadOnlyList<JournalGroupMember> done, JournalGroupMember? inFlight, bool inFlightDestinationIsOurs)
    {
        var stuck = 0;
        foreach (var member in done.Reverse())
        {
            try
            {
                var stat = _fileSystem.GetFileStat(member.Destination!);
                if (stat is null) continue;
                if (stat.Length != member.Size)
                {
                    stuck++;
                    continue;
                }
                _fileSystem.Delete(member.Destination!);
                if (_fileSystem.FileExists(member.Destination!)) stuck++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                stuck++;
            }
        }

        if (inFlight?.Destination is { } partial && inFlightDestinationIsOurs)
        {
            try
            {
                if (_fileSystem.FileExists(partial))
                {
                    _fileSystem.Delete(partial);
                    if (_fileSystem.FileExists(partial)) stuck++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                stuck++;
            }
        }

        return stuck;
    }

    /// <summary>
    /// Move compensation: puts members that were already moved back to their sources so a failed or cancelled Move does
    /// not leave a split pair. A member is moved back only when that is provably safe: its source is absent and its
    /// destination is still the file that was moved (same size and last-write time). Anything else (a new file at the
    /// source, an edited or vanished destination) is left alone. A plain file-system move is used, not the forward Move
    /// override. The member being moved when the failure hit (a cross-volume Move is copy + delete) may have left a partial
    /// copy at its destination while its source is still there: that partial is deleted, but only when nothing was at the
    /// destination before this Move started, the source is still exactly the manifest file, and the destination cannot be a
    /// complete file (it is strictly shorter than the source: a full-size destination is left for Recovery to judge, as a
    /// finished copy whose source could not be removed is a retryable failure); otherwise it is left alone. Returns how many already-moved members (or undeletable partials) could not be put back.
    /// </summary>
    private int RestoreMovedMembers(IReadOnlyList<JournalGroupMember> done, JournalGroupMember? inFlight, bool inFlightDestinationIsOurs)
    {
        var stuck = 0;
        var candidates = new List<(JournalGroupMember Member, bool WasDone)>();
        if (inFlight is not null) candidates.Add((inFlight, false));
        foreach (var member in done.Reverse()) candidates.Add((member, true));

        foreach (var (member, wasDone) in candidates)
        {
            try
            {
                var source = _fileSystem.GetFileStat(member.Source);
                var destination = _fileSystem.GetFileStat(member.Destination!);
                if (source is not null && destination is null) continue; // already back
                if (!wasDone && inFlightDestinationIsOurs && source is not null && destination is not null
                    && source.Length == member.Size && source.LastWriteUtc == member.LastWriteUtc
                    && destination.Length < member.Size)
                {
                    _fileSystem.Delete(member.Destination!);
                    if (_fileSystem.FileExists(member.Destination!)) stuck++;
                    continue;
                }

                if (source is not null || destination is null)
                {
                    // Both present (a conflict) or neither (lost): only a finished member counts as "could not be put back".
                    if (wasDone) stuck++;
                    continue;
                }

                // RV-C01: destination-side compare, tolerant of a destination volume that rounds the write time (FAT 2 s).
                if (!FileFingerprint.MatchesMovedDestination(destination, member.Size, member.LastWriteUtc))
                {
                    stuck++;
                    continue;
                }

                _fileSystem.Move(member.Destination!, member.Source);
                var back = _fileSystem.GetFileStat(member.Source);
                if (back is null || back.Length != member.Size || _fileSystem.FileExists(member.Destination!)) stuck++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                stuck++;
            }
        }

        return stuck;
    }

    private void VerifyGroupDestination(JournalGroupMember member)
    {
        var destination = _fileSystem.GetFileStat(member.Destination!);
        if (destination is null || destination.Length != member.Size)
            throw new JournalCodedException(JournalErrors.VerifySizeChanged);
    }

    private void VerifyGroupMove(JournalGroupMember member)
    {
        if (_fileSystem.FileExists(member.Source)) throw new JournalCodedException(JournalErrors.MoveSourceNotRemoved);
        VerifyGroupDestination(member);
    }

    private CaptureGroupMemberResult InspectGroupMember(FileOperationType operation, JournalGroupMember member, string error)
    {
        try
        {
            if (operation == FileOperationType.Recycle)
            {
                var source = _fileSystem.GetFileStat(member.Source);
                return new(member, source is null, source is not null &&
                    (source.Length != member.Size || source.LastWriteUtc != member.LastWriteUtc), source is null ? null : error,
                    SourceExists: source is not null);
            }

            var destination = member.Destination is null ? null : _fileSystem.GetFileStat(member.Destination);
            var sourceStat = _fileSystem.GetFileStat(member.Source);
            var completed = operation == FileOperationType.Move
                ? sourceStat is null && destination?.Length == member.Size
                : destination?.Length == member.Size;
            // A Move member the compensation put back made a round trip through the destination volume, which may have rounded
            // its write time (RV-C01): tolerant compare. A Copy source never left its volume: exact.
            var sourceUnchanged = sourceStat is not null && (operation == FileOperationType.Move
                ? FileFingerprint.MatchesMovedDestination(sourceStat, member.Size, member.LastWriteUtc)
                : sourceStat.Length == member.Size && sourceStat.LastWriteUtc == member.LastWriteUtc);
            var conflict = !completed && (!sourceUnchanged || destination is not null);
            return new(member, completed, conflict, completed ? null : error,
                SourceExists: sourceStat is not null, DestinationExists: destination is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(member, false, true, ex.Message, StateKnown: false);
        }
    }

    /// <summary>
    /// Thực thi yêu cầu thao tác tệp tin không đồng bộ.
    /// </summary>
    public async Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryBegin())
        {
            return new FileActionResult(
                Succeeded: false,
                Operation: request.Operation,
                Source: request.Source,
                DestinationPath: null,
                Size: 0,
                LastWriteUtc: DateTime.MinValue,
                Error: Tr.CoreFileActionBusy,
                Rejected: true);
        }

        var operationId = Guid.NewGuid().ToString("N");
        JournalTransaction? tx = null;
        string? destinationPath = null;
        long sourceSize = 0;
        var sourceLastWriteUtc = DateTime.MinValue;
        var permanent = false;
        // RV-C03: true only while this Copy may have created its destination (raised inside the delegate right before the
        // create-new copy; a TryCopyNew that found the destination taken touched nothing and resets it).
        var copyDestinationIsOurs = false;

        try
        {
            if (request.Operation is FileOperationType.Move or FileOperationType.Copy)
            {
                if (string.IsNullOrWhiteSpace(request.Destination))
                    throw new IOException(Tr.CoreFileActionNoDestination);

                // Enforce the whole ActionDestinationPolicy at run time, not only what Settings validated (review r7):
                // drive-relative "a:b" and rooted-without-drive "\photos" resolve against whatever drive/current
                // directory the process has, i.e. neither an explicit absolute path nor a folder inside the photo folder.
                if (ActionDestinationPolicy.Validate(request.Destination) != ActionDestinationCheck.Ok)
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);

                var source = request.Source;
                var destinationFolder = Path.IsPathRooted(request.Destination)
                    ? request.Destination
                    : Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, request.Destination);

                destinationFolder = Path.GetFullPath(destinationFolder);
                var sourceFolder = Path.GetFullPath(Path.GetDirectoryName(source) ?? string.Empty);

                if (IsSamePath(destinationFolder, sourceFolder))
                    throw new IOException(Tr.CoreFileActionSameFolder);

                if (!Path.IsPathRooted(request.Destination)
                    && !destinationFolder.StartsWith(sourceFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);

                destinationPath = Path.Combine(destinationFolder, Path.GetFileName(source));

                // SEC-01: the lexical checks above (Validate + the StartsWith above) can be satisfied by a
                // destination that traverses an existing symlink/junction whose real target is outside the photo
                // folder. Only a relative destination is checked — an absolute one is an explicit user choice
                // that is allowed to leave the folder (unchanged behavior).
                if (!Path.IsPathRooted(request.Destination)
                    && ActionDestinationPolicy.ValidateNoEscapeViaReparsePoint(sourceFolder, destinationPath, _fileSystem) != ActionDestinationCheck.Ok)
                    throw new JournalCodedException(JournalErrors.DestinationOutsideSource);

                if (_fileSystem.FileExists(destinationPath))
                    throw new IOException(Tr.CoreFileActionDestinationExists(destinationPath));

                var sourceStat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(source), source);

                sourceSize = sourceStat.Length;
                sourceLastWriteUtc = sourceStat.LastWriteUtc;

                // Only after every pre-check passed: a missing source must not leave an empty destination folder behind.
                _fileSystem.CreateDirectory(destinationFolder);

                tx = new JournalTransaction(_journal, _clock, new JournalEntry(
                    operationId,
                    request.Operation,
                    JournalState.Prepared,
                    source,
                    destinationPath,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow));
                await tx.BeginAsync().ConfigureAwait(false);

                if (request.Operation == FileOperationType.Copy)
                {
                    // Create-new copy (RV-C03): a file that appeared at the destination after the preflight is never
                    // overwritten and never claimed as ours, so the failure cleanup below cannot delete it.
                    var created = await Task.Run(() =>
                    {
                        copyDestinationIsOurs = true;
                        return _fileSystem.TryCopyNew(source, destinationPath);
                    }, cancellationToken).ConfigureAwait(false);
                    if (!created)
                    {
                        copyDestinationIsOurs = false;
                        throw new IOException(Tr.CoreFileActionDestinationExists(destinationPath));
                    }
                }
                else
                {
                    if (_moveOverride is not null)
                    {
                        await _moveOverride(source, destinationPath).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Run(() => _fileSystem.Move(source, destinationPath), cancellationToken).ConfigureAwait(false);
                    }
                }

                // Journaled after Prepared: persisted as a code + English, shown via ex.Message (UI language).
                if (request.Operation == FileOperationType.Move)
                    tx.VerifyMoved(_fileSystem, source, destinationPath, JournalErrors.VerifySizeChanged);
                else
                    tx.VerifyDestination(_fileSystem, destinationPath, JournalErrors.VerifySizeChanged);

                _ = tx.Commit(out var journalError);
                if (journalError is not null)
                {
                    return new FileActionResult(true, request.Operation, source, destinationPath,
                        sourceSize, sourceLastWriteUtc, null, JournalPersisted: false,
                        JournalError: journalError);
                }

                return new FileActionResult(
                    Succeeded: true,
                    Operation: request.Operation,
                    Source: source,
                    DestinationPath: destinationPath,
                    Size: sourceSize,
                    LastWriteUtc: sourceLastWriteUtc,
                    Error: null);
            }
            else if (request.Operation == FileOperationType.Recycle)
            {
                var source = request.Source;
                var sourceStat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(source), source);

                sourceSize = sourceStat.Length;
                sourceLastWriteUtc = sourceStat.LastWriteUtc;

                // Q-R8: on a drive without a Recycle Bin the file is only deleted (permanently) when the caller opted in
                // (setting + confirmation). Otherwise refuse BEFORE anything is journaled, exactly as R2-F-05 requires.
                permanent = !_recycleBin.CanRecycle(source);
                if (permanent && !request.AllowPermanentDelete)
                    throw new IOException(Tr.CoreRecycleUnsupportedDrive(Path.GetFileName(source)));

                // F-WIN-2: a fixed drive whose Recycle Bin is turned off, or too small for this file, makes the shell delete
                // it permanently without asking while the journal would say "recycle" (Ctrl+Z could not restore it).
                // Refuse BEFORE anything is journaled; the size is the stat above, so no extra file-system read.
                // AllowPermanentDelete does not apply here: it only covers drives that have no Recycle Bin at all.
                if (!permanent && !_recycleBin.FitsInRecycleBin(source, sourceSize))
                    throw new IOException(Tr.CoreRecycleBinCannotHold(Path.GetFileName(source)));

                tx = new JournalTransaction(_journal, _clock, new JournalEntry(
                    operationId,
                    FileOperationType.Recycle,
                    JournalState.Prepared,
                    source,
                    null,
                    sourceSize,
                    sourceLastWriteUtc,
                    _clock.UtcNow,
                    Permanent: permanent ? true : null));
                await tx.BeginAsync().ConfigureAwait(false);

                if (permanent)
                    await Task.Run(() => _recycleBin.DeletePermanently(source), cancellationToken).ConfigureAwait(false);
                else
                    await Task.Run(() => _recycleBin.SendToRecycleBin(source), cancellationToken).ConfigureAwait(false);
                tx.MarkMutationCompleted();

                _ = tx.Commit(out var journalError);
                if (journalError is not null)
                {
                    return new FileActionResult(true, FileOperationType.Recycle, source, null,
                        sourceSize, sourceLastWriteUtc, null, JournalPersisted: false,
                        JournalError: journalError, PermanentlyDeleted: permanent);
                }

                return new FileActionResult(
                    Succeeded: true,
                    Operation: FileOperationType.Recycle,
                    Source: source,
                    DestinationPath: null,
                    Size: sourceSize,
                    LastWriteUtc: sourceLastWriteUtc,
                    Error: null,
                    PermanentlyDeleted: permanent);
            }
            else
            {
                throw new NotSupportedException(Tr.CoreFileActionUnsupportedOperation(request.Operation));
            }
        }
        catch (Exception ex)
        {
            // RV-C03: a Copy cut short (disk full, ...) must not leave its partial file: Recovery would call the entry a
            // Conflict and a retry would refuse "destination exists".
            if (copyDestinationIsOurs && destinationPath is not null)
                RemovePartialCopy(destinationPath, sourceSize);

            string? journalError = null;
            var mutationCompleted = tx?.MutationCompleted ?? false;
            // RV-C02: cancelled before the mutation touched anything (Task.Run cancelled before its delegate ran): nothing is
            // left to retry or recover, so the outcome is a terminal Dismissed line, not a Failed Recovery item (same rule
            // as a fully rolled back group, ExecuteGroupAsync).
            if (ex is OperationCanceledException && tx is { IsPrepared: true } && !mutationCompleted
                && IsUntouched(request.Source, sourceSize, sourceLastWriteUtc, destinationPath))
                _ = tx.DismissRolledBack(out journalError);
            else
                _ = tx?.Fail(ex, out journalError);
            // F3: only a Move that was journaled (Prepared) and then failed can have removed the source; ask the disk.
            var sourceRemoved = !mutationCompleted && request.Operation == FileOperationType.Move && tx is { IsPrepared: true }
                && IsSourceGone(request.Source);

            return new FileActionResult(
                Succeeded: mutationCompleted,
                Operation: request.Operation,
                Source: request.Source,
                DestinationPath: destinationPath,
                Size: sourceSize,
                LastWriteUtc: sourceLastWriteUtc,
                Error: mutationCompleted ? null : ex.Message,
                JournalPersisted: journalError is null,
                JournalError: journalError,
                PermanentlyDeleted: permanent && mutationCompleted,
                SourceRemoved: sourceRemoved);
        }
        finally
        {
            tx?.Dispose(); // Q-R27 safety net: the live marker normally ends with Commit/Fail
            End();
        }
    }

    /// <summary>
    /// RV-C03: deletes the destination of a failed single Copy that this call created, only while it is provably incomplete
    /// (strictly shorter than the source). A complete copy is left for Recovery to judge. Best effort: a file that cannot be
    /// inspected or deleted stays (Recovery then shows the conflict).
    /// </summary>
    private void RemovePartialCopy(string destination, long sourceSize) =>
        PartialDestinationCleanup.RemoveIfPartial(_fileSystem, destination, sourceSize);

    /// <summary>RV-C02: true when the source is still exactly the preflight file (same volume: exact compare) and nothing is at
    /// the destination. Any inspection error counts as "touched" (keeps the Failed Recovery line) and never escapes.</summary>
    private bool IsUntouched(string source, long size, DateTime lastWriteUtc, string? destination)
    {
        try
        {
            var stat = _fileSystem.GetFileStat(source);
            return stat is not null && stat.Length == size && stat.LastWriteUtc == lastWriteUtc
                && (destination is null || !_fileSystem.FileExists(destination));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>True only when the file system positively reports the source missing; an inspection error counts as "not gone" (keeps the catalog entry).</summary>
    private bool IsSourceGone(string source)
    {
        try
        {
            return !_fileSystem.FileExists(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSamePath(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }
}
