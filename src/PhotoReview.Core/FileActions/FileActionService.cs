using System.IO;
using PhotoReview.Core.Abstractions;
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

            foreach (var source in request.Group.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stat = _fileSystem.GetFileStat(source)
                    ?? throw new FileNotFoundException(Tr.CoreFileActionSourceMissing(source), source);
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
                    if (!permanent && !_recycleBin.FitsInRecycleBin(source, stat.Length))
                        throw new IOException(Tr.CoreRecycleBinCannotHold(Path.GetFileName(source)));
                }

                members.Add(new JournalGroupMember(source, destination, stat.Length, stat.LastWriteUtc, permanent));
            }
            manifest = members;

            if (destinationFolder is not null) _fileSystem.CreateDirectory(destinationFolder);
            var first = manifest[0];
            var prepared = new JournalEntry(
                Guid.NewGuid().ToString("N"), request.Operation, JournalState.Prepared,
                first.Source, first.Destination, first.Size, first.LastWriteUtc, _clock.UtcNow,
                Permanent: manifest.All(member => member.Permanent) && manifest.Any(member => member.Permanent) ? true : null,
                GroupId: groupId, GroupMembers: manifest);
            tx = new JournalTransaction(_journal, _clock, prepared);
            await tx.BeginAsync().ConfigureAwait(false);

            foreach (var member in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Operation == FileOperationType.Copy)
                {
                    await Task.Run(() => _fileSystem.Copy(member.Source, member.Destination!), cancellationToken).ConfigureAwait(false);
                    VerifyGroupDestination(member);
                }
                else if (request.Operation == FileOperationType.Move)
                {
                    if (_moveOverride is not null)
                        await _moveOverride(member.Source, member.Destination!).ConfigureAwait(false);
                    else
                        await Task.Run(() => _fileSystem.Move(member.Source, member.Destination!), cancellationToken).ConfigureAwait(false);
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
            }

            tx.MarkMutationCompleted();
            var committed = tx.Commit(out var commitError);
            var complete = manifest.Select(member => new CaptureGroupMemberResult(member, true, false)).ToArray();
            return new(true, false, request.Operation, groupId, committed, complete, null,
                JournalPersisted: commitError is null, JournalError: commitError,
                PermanentlyDeleted: request.Operation == FileOperationType.Recycle && manifest.Any(member => member.Permanent));
        }
        catch (Exception ex)
        {
            string? journalError = null;
            var failed = tx?.Fail(ex, out journalError);
            var states = manifest.Select(member => InspectGroupMember(request.Operation, member, ex.Message)).ToArray();
            return new(false, false, request.Operation, groupId, failed, states, ex.Message,
                JournalPersisted: journalError is null,
                JournalError: journalError,
                PermanentlyDeleted: request.Operation == FileOperationType.Recycle && states.Any(member => member.Completed && member.Member.Permanent));
        }
        finally
        {
            tx?.Dispose();
            End();
        }
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
            var conflict = !completed && (sourceStat is null || sourceStat.Length != member.Size
                || sourceStat.LastWriteUtc != member.LastWriteUtc || destination is not null);
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
                    await Task.Run(() => _fileSystem.Copy(source, destinationPath), cancellationToken).ConfigureAwait(false);
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
            string? journalError = null;
            _ = tx?.Fail(ex, out journalError);
            var mutationCompleted = tx?.MutationCompleted ?? false;
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
