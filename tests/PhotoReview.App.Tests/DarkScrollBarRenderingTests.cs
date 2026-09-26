using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using PhotoReview.App;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PhotoReview.App.Tests;

/// <summary>
/// UI dark chrome: MainWindow's ImageScroll (a plain Auto/Auto ScrollViewer) must render a dark
/// scrollbar thumb and a dark bottom-right corner -- matching the near-black viewer background --
/// instead of the default (light/white) Aero chrome. Renders the real
/// <c>Themes/DarkScrollBars.xaml</c> resources (the same dictionary MainWindow and every dialog
/// window merge) through <see cref="RenderTargetBitmap"/> and samples the actual pixels, so reverting
/// the merge or the ScrollViewer corner template fails this test.
/// </summary>
public sealed class DarkScrollBarRenderingTests
{
    [Fact(DisplayName = "ScrollViewer styled with the app's dark theme renders a dark thumb and a dark corner")]
    public void ScrollViewer_WithAppDarkTheme_RendersDarkThumbAndCorner()
    {
        Exception? threadException = null;
        Color thumbColor = default;
        Color cornerColor = default;

        var thread = new Thread(() =>
        {
            try
            {
                (thumbColor, cornerColor) = RenderAndSample();
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "ScrollViewer render timed out.");
        Assert.Null(threadException);

        AssertDark(thumbColor, "scrollbar thumb");
        AssertDark(cornerColor, "scrollbar corner");
    }

    private static (Color Thumb, Color Corner) RenderAndSample()
    {
        if (Application.Current is null)
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Merely creating an Application and resolving any "pack://application:,,,/..." URI (even one
        // that names its own assembly explicitly, as below) can lazily latch
        // Application.ResourceAssembly to Assembly.GetEntryAssembly() (testhost.exe here) as a side
        // effect. That is process-wide and one-shot, so it would break any other test in this run that
        // resolves an *assembly-less* pack URI (e.g. MainWindow's Icon) via the simpler reflection
        // fallback other tests use (which only overwrites the field, without also resetting the
        // resource-manager cache -- see PhotoReview.Integration.Tests.Infrastructure.WpfResourceLookup's
        // remarks). Redirecting it to PhotoReview.App up front keeps it correct for everyone.
        RedirectPackUriResourceAssembly();

        // White background: if the dark theme fails to apply (missing merge, reverted template),
        // whatever shows through the scrollbar/corner region is this white, not a dark grey.
        var root = new Border { Width = 220, Height = 170, Background = Brushes.White };
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PhotoReview.App;component/Themes/DarkPalette.xaml", UriKind.Relative),
        });
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/PhotoReview.App;component/Themes/DarkScrollBars.xaml", UriKind.Relative),
        });

        var scroll = new ScrollViewer
        {
            Width = 220,
            Height = 170,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Border { Width = 1000, Height = 1000, Background = Brushes.Black },
        };
        root.Child = scroll;

        root.Measure(new Size(220, 170));
        root.Arrange(new Rect(0, 0, 220, 170));
        root.UpdateLayout();

        var verticalBar = (ScrollBar?)scroll.Template.FindName("PART_VerticalScrollBar", scroll)
            ?? throw new InvalidOperationException("PART_VerticalScrollBar not found -- ScrollViewer template missing.");
        var thumb = FindDescendant<Thumb>(verticalBar)
            ?? throw new InvalidOperationException("Thumb not found inside the vertical ScrollBar.");
        var corner = (Rectangle?)scroll.Template.FindName("PART_ScrollCorner", scroll)
            ?? throw new InvalidOperationException("PART_ScrollCorner not found -- ScrollViewer template missing the corner.");

        var bitmap = new RenderTargetBitmap(220, 170, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        return (SamplePixel(bitmap, root, thumb), SamplePixel(bitmap, root, corner));
    }

    private static void AssertDark(Color color, string what)
    {
        // Near-black grey (Dark.ScrollTrack/#4A4A4A thumb): every channel comfortably below the
        // default Aero scrollbar/corner tone (light grey/white, channels ~200-255).
        Assert.True(color.R < 100 && color.G < 100 && color.B < 100,
            $"Expected a dark {what} pixel, got #{color.R:X2}{color.G:X2}{color.B:X2}.");
    }

    private static Color SamplePixel(RenderTargetBitmap bitmap, Visual root, FrameworkElement target)
    {
        var bounds = target.TransformToAncestor(root)
            .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        var x = (int)Math.Clamp(bounds.Left + bounds.Width / 2, 0, bitmap.PixelWidth - 1);
        var y = (int)Math.Clamp(bounds.Top + bounds.Height / 2, 0, bitmap.PixelHeight - 1);

        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]); // Pbgra32 byte order: B, G, R, A.
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Points WPF's "pack://application:,,,/..." lookup (used for an assembly-less pack URI, e.g.
    /// MainWindow's Icon) at PhotoReview.App instead of whatever it would otherwise lazily latch onto
    /// (testhost.exe). Mirrors PhotoReview.Integration.Tests.Infrastructure.WpfResourceLookup (test-only
    /// infrastructure; not shareable across those two test assemblies).
    /// </summary>
    private static void RedirectPackUriResourceAssembly()
    {
        var app = typeof(MainWindow).Assembly;
        try
        {
            Application.ResourceAssembly = app;
            return;
        }
        catch (InvalidOperationException)
        {
            // Already latched by an earlier test on another thread; force it via reflection below.
        }

        const BindingFlags AnyStatic = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        typeof(Application).GetField("_resourceAssembly", AnyStatic)?.SetValue(null, app);

        var baseUriHelper = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MS.Internal.BaseUriHelper", throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (baseUriHelper is not null)
        {
            var property = baseUriHelper.GetProperty("ResourceAssembly", AnyStatic);
            if (property?.SetMethod is not null) property.SetValue(null, app);
            else baseUriHelper.GetField("_resourceAssembly", AnyStatic)?.SetValue(null, app);
        }

        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("MS.Internal.AppModel.ResourceContainer", throwOnError: false))
            .FirstOrDefault(t => t is not null)
            ?.GetField("_resourceManagerWrapper", AnyStatic)?.SetValue(null, null);
    }
}
