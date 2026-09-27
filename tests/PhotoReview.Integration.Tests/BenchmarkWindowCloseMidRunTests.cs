using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Core.Localization;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// F-WIN-8: closing/disposing the Benchmark window while a multi-profile run is between profiles used to make the
/// next profile read <c>_cts.Token</c> from a nulled field (NullReferenceException) and record a bogus Fail row.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class BenchmarkWindowCloseMidRunTests
{
    [Fact(DisplayName = "Disposing the window between two profiles cancels the run without a bogus Fail row")]
    public async Task DisposeBetweenProfiles_EndsAsCancelled_NotAsFailRow()
    {
        using var temp = new TempRoot("bench-close-mid-run");
        var folder = temp.Dir("images");
        await StaTestHost.RunAsync(() => { WriteJpegs(folder); return Task.CompletedTask; });

        await StaTestHost.RunAsync(async () =>
        {
            var window = new BenchmarkWindow(folder) { ImageLimitText = { Text = "2" } };
            var items = window.ProfilesList.Items.Cast<BenchmarkProfileItem>().ToArray();
            window.ProfilesList.SelectedItems.Clear();
            window.ProfilesList.SelectedItems.Add(items.First(i => i.Profile.Id == "fast-sequential"));
            window.ProfilesList.SelectedItems.Add(items.First(i => i.Profile.Id == "no-preload-baseline"));

            // The first profile's row is added right before the loop moves on to the next profile: dispose there,
            // exactly as a window close landing between two profiles would.
            var disposed = false;
            ((INotifyCollectionChanged)window.ResultsGrid.ItemsSource).CollectionChanged += (_, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Add || disposed) return;
                disposed = true;
                window.Dispose();
            };

            window.RunButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var finished = await StaTestHost.WaitForAsync(() => disposed && window.RunButton.IsEnabled, TimeSpan.FromSeconds(20));

            Assert.True(finished, $"The run did not finish. Status={window.StatusText.Text}");
            var rows = window.ResultsGrid.Items.Cast<BenchmarkResultRow>().ToArray();
            Assert.Single(rows); // profile 1 only; profile 2 must not add a synthesized Fail row
            Assert.Equal(Tr.BenchmarkStatusCanceled, window.StatusText.Text);
        });
    }

    private static void WriteJpegs(string folder)
    {
        var pixels = new byte[16 * 16 * 3];
        var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Rgb24, null, pixels, 16 * 3);
        source.Freeze();
        foreach (var name in new[] { "a.jpg", "b.jpg" })
        {
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(Path.Combine(folder, name));
            encoder.Save(stream);
        }
    }
}
