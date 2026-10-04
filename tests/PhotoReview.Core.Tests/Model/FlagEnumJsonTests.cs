using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Model;

/// <summary>W2-CC-03: explicit flag names on write, per-name parsing and negative numbers unreadable on read.</summary>
public sealed class FlagEnumJsonTests
{
    [Fact(DisplayName = "ExifInfoFields.Default is written as explicit flag names, not the Default alias")]
    public void Exif_Default_WritesExplicitNames()
    {
        var json = JsonSerializer.Serialize(ExifInfoFields.Default);

        Assert.Equal("\"DateTaken, Camera, Lens, Iso, FocalLength, Aperture, ShutterSpeed\"", json);
        Assert.Equal(ExifInfoFields.Default, JsonSerializer.Deserialize<ExifInfoFields>(json));
    }

    [Fact(DisplayName = "ExifInfoFields.All is written as every flag name and None as None")]
    public void Exif_AllAndNone_Write()
    {
        var all = JsonSerializer.Serialize(ExifInfoFields.All);

        Assert.DoesNotContain("All", all, StringComparison.Ordinal);
        Assert.Contains("ModifiedDate", all, StringComparison.Ordinal);
        Assert.Equal(ExifInfoFields.All, JsonSerializer.Deserialize<ExifInfoFields>(all));
        Assert.Equal("\"None\"", JsonSerializer.Serialize(ExifInfoFields.None));
        Assert.Equal(ExifInfoFields.None, JsonSerializer.Deserialize<ExifInfoFields>("\"None\""));
    }

    [Fact(DisplayName = "TitleBarFields.Default is written as FolderName (not the alias that shares its value)")]
    public void TitleBar_Default_WritesFolderName()
    {
        Assert.Equal("\"FolderName\"", JsonSerializer.Serialize(TitleBarFields.Default));
        Assert.DoesNotContain("All", JsonSerializer.Serialize(TitleBarFields.All), StringComparison.Ordinal);
        Assert.Equal(TitleBarFields.All, JsonSerializer.Deserialize<TitleBarFields>(JsonSerializer.Serialize(TitleBarFields.All)));
    }

    [Fact(DisplayName = "Old files that wrote the All / Default alias names or numbers still read")]
    public void OldAliasNamesAndNumbers_StillRead()
    {
        Assert.Equal(ExifInfoFields.All, JsonSerializer.Deserialize<ExifInfoFields>("\"All\""));
        Assert.Equal(ExifInfoFields.Default, JsonSerializer.Deserialize<ExifInfoFields>("\"Default\""));
        Assert.Equal(TitleBarFields.Default, JsonSerializer.Deserialize<TitleBarFields>("\"Default\""));
        Assert.Equal(TitleBarFields.FolderName | TitleBarFields.FileName, JsonSerializer.Deserialize<TitleBarFields>("9"));
        Assert.Equal(ExifInfoFields.FileName | ExifInfoFields.Camera, JsonSerializer.Deserialize<ExifInfoFields>("\"FileName, Camera\""));
    }

    [Fact(DisplayName = "A list with an unknown name keeps the valid names instead of resetting to Default")]
    public void UnknownName_KeepsTheValidOnes()
    {
        Assert.Equal(ExifInfoFields.FileName, JsonSerializer.Deserialize<ExifInfoFields>("\"FileName, Foo\""));
        Assert.Equal(TitleBarFields.FileName | TitleBarFields.Iso, JsonSerializer.Deserialize<TitleBarFields>("\"iso , Bogus,FileName\""));
        Assert.Equal(ExifInfoFields.Default, JsonSerializer.Deserialize<ExifInfoFields>("\"Foo, Bar\""));
    }

    [Theory(DisplayName = "Negative numbers are unreadable (Default), not 'everything on'")]
    [InlineData("-1")]
    [InlineData("-512")]
    [InlineData("\"-1\"")]
    public void NegativeNumbers_AreUnreadable(string token)
    {
        Assert.Equal(ExifInfoFields.Default, JsonSerializer.Deserialize<ExifInfoFields>(token));
        Assert.Equal(TitleBarFields.Default, JsonSerializer.Deserialize<TitleBarFields>(token));
    }
}
