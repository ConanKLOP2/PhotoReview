using System.IO;
using System.Reflection;
using PhotoReview.App.Composition;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.Composition;

/// <summary>Guards the two DI wirings of App.xaml.cs that no other test observes (both went through <see cref="ServiceFactories"/>).</summary>
public sealed class ServiceFactoriesTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>The Raw file lives on a "drive without a Recycle Bin": only a permanent delete may touch it.</summary>
    private sealed class NoBinForRawRecycleBin(string rawPath) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> PermanentlyDeleted { get; } = [];

        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;

        public bool CanRecycle(string path) => !string.Equals(path, rawPath, StringComparison.OrdinalIgnoreCase);

        public void DeletePermanently(string path)
        {
            PermanentlyDeleted.Add(path);
            File.Delete(path);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempRoot _root = new("service-factories");
        public string Jpg { get; }
        public string Raw { get; }
        public NoBinForRawRecycleBin Bin { get; }
        public SettingsStore Settings { get; }
        public JournalEntry Failed { get; }
        public RecoveryRetryService Service { get; }

        public Rig()
        {
            var fs = new PhysicalFileSystem();
            Jpg = _root.File(@"photos\a.jpg", 1, 2, 3, 4);
            Raw = _root.File(@"photos\a.cr2", 1, 2, 3, 4, 5, 6, 7, 8);
            File.SetLastWriteTimeUtc(Jpg, Stamp);
            File.SetLastWriteTimeUtc(Raw, Stamp);
            Bin = new NoBinForRawRecycleBin(Raw);
            var paths = new AppPaths(_root.Combine("data"));
            Settings = new SettingsStore(paths, fs, NullLog.Instance);
            var journal = new OperationJournal(paths, fs, new FixedClock(Stamp.AddMinutes(1)));
            var members = new[]
            {
                new JournalGroupMember(Jpg, null, 4, Stamp),
                new JournalGroupMember(Raw, null, 8, Stamp, Permanent: true),
            };
            Failed = new JournalEntry("delete-group", FileOperationType.Recycle, JournalState.Failed, Jpg, null, 4, Stamp, Stamp,
                Error: "x", ErrorCode: JournalErrors.SourceStillExistsAfterRecovery, Permanent: true,
                GroupId: "capture-a", GroupMembers: members);
            journal.Append(Failed);
            Service = ServiceFactories.CreateRecoveryRetryService(journal, fs, new FixedClock(Stamp.AddMinutes(2)), Bin, Settings);
        }

        public void SetAllowPermanent(bool value) => Settings.Save(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = value });

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task RecoveryRetry_PermanentMemberIsRefusedWhileTheSettingIsOff()
    {
        using var rig = new Rig();
        rig.SetAllowPermanent(false);

        var result = await rig.Service.RetryMoveOrCopyAsync(rig.Failed);

        Assert.False(result.Succeeded);
        Assert.Empty(rig.Bin.Recycled);
        Assert.Empty(rig.Bin.PermanentlyDeleted);
        Assert.True(File.Exists(rig.Jpg));
        Assert.True(File.Exists(rig.Raw));
    }

    [Fact]
    public async Task RecoveryRetry_HonoursTheSettingChangedAfterConstruction()
    {
        using var rig = new Rig();
        rig.SetAllowPermanent(false);
        Assert.False((await rig.Service.RetryMoveOrCopyAsync(rig.Failed)).Succeeded);

        rig.SetAllowPermanent(true); // a NEW AppSettings instance: the delegate must read store.Current at call time

        var result = await rig.Service.RetryMoveOrCopyAsync(rig.Failed);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([rig.Jpg], rig.Bin.Recycled);
        Assert.Equal([rig.Raw], rig.Bin.PermanentlyDeleted);
    }

    private sealed class StubDecoder : IImageDecoder
    {
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();
        public IDecodedImage Decode(DecodeRequest request) => throw new NotSupportedException();
    }

    private sealed class StubFallback : IRawPreviewFallback
    {
        public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format) => ReadOnlyMemory<byte>.Empty;
    }

    private static object? Field(RawDecoder decoder, string name) =>
        typeof(RawDecoder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decoder);

    [Fact]
    public void CreateRawDecoder_LibRawAvailable_WiresBothThePreviewFallbackAndTheNoPreviewDecoder()
    {
        var noPreview = new StubDecoder();
        var fallback = new StubFallback();

        var decoder = ServiceFactories.CreateRawDecoder(new StubDecoder(), PhysicalSourceReader.Instance, null,
            (out string? reason) => { reason = null; return true; }, () => fallback, () => noPreview);

        Assert.Same(fallback, Field(decoder, "_previewFallback"));
        Assert.Same(noPreview, Field(decoder, "_noPreviewDecoder"));
    }

    [Fact]
    public void CreateRawDecoder_LibRawMissing_WiresNeitherAndNeverCreatesThem()
    {
        var created = 0;

        var decoder = ServiceFactories.CreateRawDecoder(new StubDecoder(), PhysicalSourceReader.Instance, null,
            (out string? reason) => { reason = "libraw.dll not found"; return false; },
            () => { created++; return new StubFallback(); }, () => { created++; return new StubDecoder(); });

        Assert.Null(Field(decoder, "_previewFallback"));
        Assert.Null(Field(decoder, "_noPreviewDecoder"));
        Assert.Equal(0, created);
    }
}
