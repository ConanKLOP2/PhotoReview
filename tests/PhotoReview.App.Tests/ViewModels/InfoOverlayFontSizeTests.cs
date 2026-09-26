using System.Collections.Generic;
using System.ComponentModel;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>feat/ui-dark-chrome-toolbar: the status/folder line follow Settings.InfoOverlayFontSize; the EXIF line is always one point smaller.</summary>
[Trait("Category", "HotPath")]
public sealed class InfoOverlayFontSizeTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(24)]
    public void FontSize_ReadsFromSettings_ExifFontSizeIsOnePointSmaller(double configured)
    {
        var settings = new AppSettings { InfoOverlayFontSize = configured };
        var overlay = new InfoOverlayViewModel(() => settings, (_, _) => default);

        Assert.Equal(configured, overlay.FontSize);
        Assert.Equal(configured - 1, overlay.ExifFontSize);
    }

    [Fact]
    public void Refresh_RaisesPropertyChanged_ForFontSizeAndExifFontSize()
    {
        var settings = new AppSettings { InfoOverlayFontSize = 12 };
        var overlay = new InfoOverlayViewModel(() => settings, (_, _) => default);
        var raised = new List<string>();
        overlay.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        settings.InfoOverlayFontSize = 20;
        overlay.Refresh();

        Assert.Contains(nameof(InfoOverlayViewModel.FontSize), raised);
        Assert.Contains(nameof(InfoOverlayViewModel.ExifFontSize), raised);
        Assert.Equal(20, overlay.FontSize);
        Assert.Equal(19, overlay.ExifFontSize);
    }
}
