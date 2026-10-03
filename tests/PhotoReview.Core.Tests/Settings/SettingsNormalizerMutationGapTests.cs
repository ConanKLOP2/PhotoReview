using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Mutation-testing gaps in <see cref="SettingsNormalizer"/> and <see cref="ShortcutKeyCanonical"/>: boundaries and undefined enum values.</summary>
public sealed class SettingsNormalizerMutationGapTests
{
    [Fact]
    public void Normalize_DefaultSettings_ReportsNothing()
    {
        Assert.Empty(SettingsNormalizer.Normalize(new AppSettings()));
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-1L, true)]
    [InlineData(1L, false)]
    public void Normalize_SourceBytesCapacity_MustBePositive(long value, bool reset)
    {
        var settings = new AppSettings { SourceBytesCapacityBytes = value };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Equal(reset, repaired.Contains(nameof(AppSettings.SourceBytesCapacityBytes)));
        Assert.Equal(reset ? PerformanceOptions.SourceBytesCapacityBytes : value, settings.SourceBytesCapacityBytes);
    }

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(0L, false)] // no reserve at all is a valid choice
    [InlineData(1L, false)]
    public void Normalize_MemoryReserve_MayBeZeroButNotNegative(long value, bool reset)
    {
        var settings = new AppSettings { MemoryReserveBytes = value };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Equal(reset, repaired.Contains(nameof(AppSettings.MemoryReserveBytes)));
        Assert.Equal(reset ? PerformanceOptions.MemoryReserveBytes : value, settings.MemoryReserveBytes);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(-0.5, true)]
    [InlineData(0.01, false)]
    [InlineData(1.0, false)] // exactly the whole memory is the upper bound
    [InlineData(1.0001, true)]
    [InlineData(double.NaN, true)]
    public void Normalize_PreloadMemoryLoadLimit_MustBeInZeroExclusiveToOneInclusive(double value, bool reset)
    {
        var settings = new AppSettings { PreloadMemoryLoadLimit = value };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Equal(reset, repaired.Contains(nameof(AppSettings.PreloadMemoryLoadLimit)));
        if (reset) Assert.Equal(PerformanceOptions.PreloadMemoryLoadLimit, settings.PreloadMemoryLoadLimit);
        else Assert.Equal(value, settings.PreloadMemoryLoadLimit);
    }

    [Fact]
    public void Normalize_UndefinedJournalDurability_IsResetToFast()
    {
        var settings = new AppSettings { JournalDurability = (JournalDurability)99 };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Contains(nameof(AppSettings.JournalDurability), repaired);
        Assert.Equal(JournalDurability.Fast, settings.JournalDurability);
    }

    [Fact]
    public void Normalize_UndefinedRawFullDecode_IsResetToItsDefault()
    {
        var settings = new AppSettings { RawFullDecode = (RawFullDecode)99 };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Contains(nameof(AppSettings.RawFullDecode), repaired);
        Assert.Equal(new AppSettings().RawFullDecode, settings.RawFullDecode);
    }

    [Fact]
    public void Normalize_UndefinedRawPairMode_IsResetToItsDefault()
    {
        var settings = new AppSettings { RawPairMode = (RawPairMode)99 };

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Contains(nameof(AppSettings.RawPairMode), repaired);
        Assert.Equal(new AppSettings().RawPairMode, settings.RawPairMode);
    }

    [Fact]
    public void Normalize_ActionWithUndefinedOperation_BecomesMoveAndIsReported()
    {
        var settings = new AppSettings();
        settings.Actions.Clear();
        settings.Actions.Add(new ReviewAction { Name = "A", Shortcut = "J", Destination = @"C:\x", Operation = (FileOperationType)99 });

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.Contains(nameof(AppSettings.Actions), repaired);
        Assert.Equal(FileOperationType.Move, Assert.Single(settings.Actions).Operation);
    }

    [Fact]
    public void Normalize_ValidActions_AreNotReported()
    {
        var settings = new AppSettings();
        settings.Actions.Clear();
        settings.Actions.Add(new ReviewAction { Name = "A", Shortcut = "J", Destination = @"C:\x", Operation = FileOperationType.Copy });

        var repaired = SettingsNormalizer.Normalize(settings);

        Assert.DoesNotContain(nameof(AppSettings.Actions), repaired);
        Assert.Equal(FileOperationType.Copy, Assert.Single(settings.Actions).Operation);
    }

    [Fact]
    public void DisableConflictingOptionalShortcuts_NoActionsList_DoesNotThrow()
    {
        var settings = new AppSettings { Actions = null! };

        var disabled = Record.Exception(() => SettingsNormalizer.DisableConflictingOptionalShortcuts(settings));

        Assert.Null(disabled);
    }

    [Fact]
    public void CanonicalizeAll_NoActionsList_StillCanonicalisesTheShortcuts()
    {
        var settings = new AppSettings { Actions = null! };
        settings.Shortcuts.Next = "Return";

        var ex = Record.Exception(() => ShortcutKeyCanonical.CanonicalizeAll(settings));

        Assert.Null(ex);
        Assert.Equal(ShortcutKeyCanonical.Canonicalize("Return"), settings.Shortcuts.Next);
    }
}