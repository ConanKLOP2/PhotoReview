using PhotoReview.App.ViewModels;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

public sealed class SettingsReloadPathTests
{
    private const string Jpeg = @"C:\p\a.jpg";
    private const string Raw = @"C:\p\a.cr2";
    private static readonly CaptureGroup Group = new(Jpeg, Raw);

    [Theory]
    [InlineData(RawPairMode.Separate)]
    [InlineData(RawPairMode.PreferJpeg)]
    [InlineData(RawPairMode.PreferRaw)]
    public void Choose_RawSupportOffAndRawMemberPresented_FallsBackToJpegPath(RawPairMode mode)
    {
        Assert.Equal(Jpeg, SettingsReloadPath.Choose(Raw, Group, Jpeg, rawEnabled: false, mode));
    }

    [Fact]
    public void Choose_RawSupportOffAndRawMemberPresentedWithDifferentCase_FallsBackToJpegPath()
    {
        Assert.Equal(Jpeg, SettingsReloadPath.Choose(Raw.ToUpperInvariant(), Group, Jpeg, false, RawPairMode.PreferJpeg));
    }

    [Theory]
    [InlineData(RawPairMode.Separate)]
    [InlineData(RawPairMode.PreferJpeg)]
    [InlineData(RawPairMode.PreferRaw)]
    public void Choose_RawSupportOffAndJpegMemberPresented_KeepsJpeg(RawPairMode mode)
    {
        Assert.Equal(Jpeg, SettingsReloadPath.Choose(Jpeg, Group, Jpeg, false, mode));
    }

    [Theory]
    [InlineData(RawPairMode.Separate)]
    [InlineData(RawPairMode.PreferJpeg)]
    [InlineData(RawPairMode.PreferRaw)]
    public void Choose_RawSupportOnAndRawMemberPresented_KeepsTheRawPath(RawPairMode mode)
    {
        // Separate: the RAW becomes its own entry; grouped modes: the group still resolves both members.
        Assert.Equal(Raw, SettingsReloadPath.Choose(Raw, Group, Jpeg, true, mode));
    }

    [Fact]
    public void Choose_NoPresentedPath_UsesTheEntryPath()
    {
        Assert.Equal(Jpeg, SettingsReloadPath.Choose(null, null, Jpeg, true, RawPairMode.Separate));
        Assert.Null(SettingsReloadPath.Choose(null, null, null, true, RawPairMode.Separate));
    }

    [Fact]
    public void Choose_RawSupportOffWithoutGroup_KeepsThePresentedPath()
    {
        Assert.Equal(Raw, SettingsReloadPath.Choose(Raw, null, Raw, false, RawPairMode.Separate));
    }

    [Theory]
    [InlineData(RawPairMode.PreferJpeg)]
    [InlineData(RawPairMode.PreferRaw)]
    public void Choose_NoPresentedPathRawEntryAndRawSupportOff_FallsBackToJpegMember(RawPairMode mode)
    {
        Assert.Equal(Jpeg, SettingsReloadPath.Choose(null, Group, Raw, rawEnabled: false, mode));
    }

    [Fact]
    public void Choose_NoPresentedPathRawEntryAndRawSupportOn_KeepsTheEntryPath()
    {
        Assert.Equal(Raw, SettingsReloadPath.Choose(null, Group, Raw, rawEnabled: true, RawPairMode.PreferRaw));
    }
}
