using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Viewing;
using Xunit.Abstractions;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// WP-26: hình học thanh cuộn tự vẽ (<see cref="ScrollBarGeometry"/>) so với thanh cuộn WPF THẬT của <c>MainWindow.ImageScroll</c>
/// (ScrollViewer Auto/Auto + <c>Themes/DarkScrollBars.xaml</c> thật). Mỗi ca dựng một ScrollViewer tách rời trên luồng STA (không
/// cửa sổ), Measure/Arrange, đặt offset rồi đọc hộp của ScrollBar, Track, Thumb và ô góc bằng <c>TranslatePoint</c> - đúng các
/// hộp mà shell vẽ. Sai số chấp nhận 1 px (thẻ WP-26); thực đo được in ra. Đây là "golden" của thanh cuộn trong G-VIEW: engine
/// viewport (WP-16) đã khớp ScrollViewer tới 0 DIP, nên khớp ở đây nghĩa là shell vẽ đúng chỗ mà WPF vẽ.
/// </summary>
[Trait("Category", "UI")]
public sealed class ScrollBarWpfParityTests(ITestOutputHelper output)
{
    private const double Tolerance = 1.0;
    private const double Thickness = 10;

    private static readonly (double W, double H)[] Clients = [(800, 600), (1280, 720), (1920, 1080), (1366, 728), (300, 200), (120, 90),
        // Rãnh gần/không đủ chỗ cho con trượt (sàn 8): 24 -> rãnh 24-10-4 = 10 (ngang), 18 -> 4 ; 14 -> 0 ; 40x10 -> thanh dọc 10 cao 0.
        (24, 24), (18, 40), (14, 14), (40, 10), (27, 31), (200, 17)];

    private static readonly (double W, double H)[] Extents =
        [(3000, 2000), (1000, 4000), (4000, 1000), (800, 590), (1300, 700), (60000, 40000), (1200, 800), (130, 95)];

    private static readonly double[] OffsetFractions = [0, 0.1, 0.5, 0.97, 1];

    /// <summary>Hộp đọc từ WPF cho một trục.</summary>
    private sealed record WpfBox(bool Visible, RectD Bar, RectD Track, RectD Thumb);

    private sealed record WpfResult(WpfBox Vertical, WpfBox Horizontal, RectD Corner, double HorizontalOffset, double VerticalOffset);

    [Fact]
    public void Grid_ThumbTrackAndBarBoxes_MatchWpfWithinOnePixel()
    {
        var worst = (Name: "-", Deviation: 0.0);
        var failures = new List<string>();
        var count = 0;
        Sta.Run(() =>
        {
            var theme = LoadTheme();
            foreach (var (cw, ch) in Clients)
            {
                foreach (var (ew, eh) in Extents)
                {
                    foreach (var fx in OffsetFractions)
                    {
                        foreach (var fy in OffsetFractions)
                        {
                            var wpf = ReadWpf(theme, cw, ch, ew, eh, fx, fy);
                            var input = new ViewportInput(cw, ch, Thickness, ScrollBarPolicy.Auto, ViewerStretchMode.None, ew, eh,
                                double.PositiveInfinity, double.PositiveInfinity, ew, eh);
                            var layout = ViewportLayoutEngine.Compute(input);
                            var geometry = ScrollBarGeometry.Compute(new SizeD(cw, ch), layout, wpf.HorizontalOffset, wpf.VerticalOffset,
                                new ScrollBarMetrics { MinThumbLength = MinThumb });
                            var name = $"client {cw}x{ch} extent {ew}x{eh} offset {wpf.HorizontalOffset:F1},{wpf.VerticalOffset:F1}";
                            var deviation = Compare(wpf, geometry, name, failures);
                            count++;
                            if (deviation > worst.Deviation)
                            {
                                worst = (name, deviation);
                            }
                        }
                    }
                }
            }
        });
        output.WriteLine($"{count} ca, lệch lớn nhất {worst.Deviation:G4} DIP ở {worst.Name}");
        Assert.True(failures.Count == 0, $"{failures.Count} ca lệch:\n" + string.Join("\n", failures.Take(20)));
    }

    /// <summary>Độ dài tối thiểu của con trượt mà WPF dùng ở máy này (ghi ra để so với <see cref="ScrollBarMetrics.DefaultMinThumbLength"/>).</summary>
    private static double MinThumb { get; set; } = ScrollBarMetrics.DefaultMinThumbLength;

    [Fact]
    public void MinimumThumbLength_OfWpf_IsWhatTheShellDefaultsTo()
    {
        double wpfMinimum = 0;
        Sta.Run(() =>
        {
            var theme = LoadTheme();
            // Đoạn cuộn rất dài so với vùng nhìn: con trượt chạm sàn.
            var wpf = ReadWpf(theme, 1280, 720, 1_000_000, 1_000_000, 0, 0);
            wpfMinimum = wpf.Vertical.Thumb.Height;
            output.WriteLine($"độ dài con trượt tối thiểu của WPF: {wpfMinimum:G6} DIP; SystemParameters.VerticalScrollBarButtonHeight={SystemParameters.VerticalScrollBarButtonHeight}, " +
                $"HorizontalScrollBarThumbWidth={SystemParameters.HorizontalScrollBarThumbWidth}, VerticalScrollBarThumbHeight={SystemParameters.VerticalScrollBarThumbHeight}");
        });
        Assert.True(wpfMinimum > 0);
        Assert.Equal(wpfMinimum, MinThumb, 3);
    }

    [Fact]
    public void RepeatTiming_IsTheDefaultOfAWpfRepeatButton_OnThisMachine()
    {
        Sta.Run(() =>
        {
            var button = new RepeatButton();
            var (delay, interval) = ScrollBarNative.RepeatTiming();
            Assert.Equal(button.Delay, delay);
            Assert.Equal(button.Interval, interval);
            output.WriteLine($"RepeatButton Delay={button.Delay} ms, Interval={button.Interval} ms");
        });
    }

    [Fact]
    public void ThumbColors_AndRadius_AreThoseOfTheDarkScrollThumbStyle()
    {
        Sta.Run(() =>
        {
            var theme = LoadTheme();
            var style = (Style)theme["Dark.ScrollThumb"];
            var thumb = new Thumb { Style = style, Width = 8, Height = 100 };
            var host = new Border { Width = 8, Height = 100, Child = thumb };
            host.Resources.MergedDictionaries.Add(theme);
            Layout(host, 8, 100);
            thumb.ApplyTemplate();
            var template = thumb.Template;
            var border = (Border)template.FindName("Bd", thumb);

            static (byte A, byte R, byte G, byte B) Rgba(object? brush) =>
                brush is SolidColorBrush { Color: var c } ? (c.A, c.R, c.G, c.B) : throw new InvalidOperationException("not a solid brush");

            static (byte A, byte R, byte G, byte B) Expected(ColorF color) =>
                ((byte)Math.Round(color.A * 255), (byte)Math.Round(color.R * 255), (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255));

            Assert.Equal(Expected(ScrollBarColors.Thumb), Rgba(border.Background));
            Assert.Equal(new CornerRadius(4), border.CornerRadius);
            Assert.Equal(4, new ScrollBarMetrics().ThumbRadius);

            var hover = template.Triggers.OfType<Trigger>().Single(t => t.Property == UIElement.IsMouseOverProperty);
            var pressed = template.Triggers.OfType<Trigger>().Single(t => t.Property == Thumb.IsDraggingProperty);
            Assert.Equal(Expected(ScrollBarColors.ThumbHover), Rgba(hover.Setters.OfType<Setter>().Single().Value));
            Assert.Equal(Expected(ScrollBarColors.ThumbPressed), Rgba(pressed.Setters.OfType<Setter>().Single().Value));
            // IsDragging sau IsMouseOver trong danh sách: khi vừa hover vừa kéo thì màu kéo thắng (shell: pressed ưu tiên hover).
            Assert.True(template.Triggers.IndexOf(pressed) > template.Triggers.IndexOf(hover));

            // Nền thanh cuộn = Dark.ScrollTrack.
            Assert.Equal(Expected(ScrollBarColors.Track), Rgba(theme["Dark.ScrollTrack"]));
        });
    }

    [Fact]
    public void ThumbDragDistance_AndPageSteps_FollowTheSameRulesAsWpf()
    {
        Sta.Run(() =>
        {
            var theme = LoadTheme();
            foreach (var (cw, ch, ew, eh, fx, fy) in new[]
            {
                (800.0, 600.0, 3000.0, 2000.0, 0.0, 0.0),
                (1280.0, 720.0, 9000.0, 1000.0, 0.3, 0.0),
                (300.0, 200.0, 800.0, 5000.0, 0.0, 0.6),
            })
            {
                var content = new Rectangle { Width = ew, Height = eh };
                var scroll = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Width = cw,
                    Height = ch,
                    Content = content,
                };
                var root = new Border { Width = cw, Height = ch, Child = scroll };
                root.Resources.MergedDictionaries.Add(theme);
                Layout(root, cw, ch);
                scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth * fx);
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fy);
                Layout(root, cw, ch);

                var input = new ViewportInput(cw, ch, Thickness, ScrollBarPolicy.Auto, ViewerStretchMode.None, ew, eh,
                    double.PositiveInfinity, double.PositiveInfinity, ew, eh);
                var layout = ScrollBarGeometry.Compute(new SizeD(cw, ch), ViewportLayoutEngine.Compute(input), scroll.HorizontalOffset,
                    scroll.VerticalOffset, new ScrollBarMetrics());

                foreach (var (name, axis, vertical) in new[] { ("PART_VerticalScrollBar", ScrollAxis.Vertical, true), ("PART_HorizontalScrollBar", ScrollAxis.Horizontal, false) })
                {
                    var bar = (ScrollBar)scroll.Template.FindName(name, scroll);
                    var track = (Track)bar.Template.FindName("PART_Track", bar);
                    var box = layout.Box(axis);
                    Assert.True(box.Visible && box.ThumbVisible, $"{cw}x{ch} {axis}");
                    foreach (var distance in new[] { 1.0, 10.0, 37.5, -12.0 })
                    {
                        var wpfDelta = vertical ? track.ValueFromDistance(0, distance) : track.ValueFromDistance(distance, 0);
                        var unclamped = (distance / box.Travel) * box.MaxOffset;
                        Assert.Equal(wpfDelta, unclamped, 6);

                        // Cùng ánh xạ khi con trượt vẫn nằm trong rãnh (ngoài rãnh: kẹp, WPF kẹp Value vào [0, max]).
                        var target = box.Offset + wpfDelta;
                        if (target >= 0 && target <= box.MaxOffset)
                        {
                            Assert.Equal(target, ScrollBarGeometry.OffsetForThumbStart(box, box.ThumbStart + distance), 6);
                        }
                    }
                }

                // Nhấn rãnh: PageUp/PageDown/PageLeft/PageRight của ScrollViewer = đúng một vùng nhìn (kẹp).
                var before = (scroll.HorizontalOffset, scroll.VerticalOffset);
                scroll.PageDown();
                Layout(root, cw, ch);
                Assert.Equal(Math.Min(scroll.ScrollableHeight, before.VerticalOffset + scroll.ViewportHeight), scroll.VerticalOffset, 6);
                scroll.PageRight();
                Layout(root, cw, ch);
                Assert.Equal(Math.Min(scroll.ScrollableWidth, before.HorizontalOffset + scroll.ViewportWidth), scroll.HorizontalOffset, 6);
                scroll.PageUp();
                scroll.PageLeft();
                Layout(root, cw, ch);
                Assert.Equal(Math.Max(0, Math.Min(scroll.ScrollableHeight, before.VerticalOffset + scroll.ViewportHeight) - scroll.ViewportHeight), scroll.VerticalOffset, 6);
            }
        });
    }

    private static double Compare(WpfResult wpf, ScrollBarLayout geometry, string name, List<string> failures)
    {
        var worst = 0.0;
        void Check(string what, RectD expected, RectD actual)
        {
            var d = new[] { expected.X - actual.X, expected.Y - actual.Y, expected.Width - actual.Width, expected.Height - actual.Height }
                .Select(Math.Abs).Max();
            worst = Math.Max(worst, d);
            if (d > Tolerance)
            {
                failures.Add($"{name}: {what} WPF {expected} / shell {actual}");
            }
        }

        foreach (var (axis, wpfBox, box) in new[] { ("dọc", wpf.Vertical, geometry.Vertical), ("ngang", wpf.Horizontal, geometry.Horizontal) })
        {
            if (wpfBox.Visible != box.Visible)
            {
                failures.Add($"{name}: thanh {axis} WPF visible={wpfBox.Visible} / shell {box.Visible}");
                worst = 1000;
                continue;
            }

            if (!wpfBox.Visible)
            {
                continue;
            }

            Check($"thanh {axis}", wpfBox.Bar, box.Bar);
            Check($"rãnh {axis}", wpfBox.Track, box.Track);
            if (wpfBox.Thumb.Width > 0 && wpfBox.Thumb.Height > 0)
            {
                if (!box.ThumbVisible)
                {
                    failures.Add($"{name}: con trượt {axis} WPF {wpfBox.Thumb} / shell ẩn");
                    worst = 1000;
                }
                else
                {
                    Check($"con trượt {axis}", wpfBox.Thumb, box.Thumb);
                }
            }
            else if (box.ThumbVisible)
            {
                failures.Add($"{name}: con trượt {axis} WPF ẩn / shell {box.Thumb}");
                worst = 1000;
            }
        }

        if (wpf.Corner.Width > 0 != geometry.HasCorner)
        {
            failures.Add($"{name}: ô góc WPF {wpf.Corner} / shell {geometry.HasCorner}");
            worst = 1000;
        }
        else if (geometry.HasCorner)
        {
            Check("ô góc", wpf.Corner, geometry.Corner);
        }

        return worst;
    }

    private static WpfResult ReadWpf(ResourceDictionary theme, double cw, double ch, double ew, double eh, double fx, double fy)
    {
        var content = new Rectangle { Width = ew, Height = eh };
        var scroll = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = cw,
            Height = ch,
            Content = content,
        };
        var root = new Border { Width = cw, Height = ch, Child = scroll };
        root.Resources.MergedDictionaries.Add(theme);
        Layout(root, cw, ch);
        scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth * fx);
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * fy);
        Layout(root, cw, ch);

        var vertical = ReadBar(scroll, root, "PART_VerticalScrollBar");
        var horizontal = ReadBar(scroll, root, "PART_HorizontalScrollBar");
        var corner = scroll.Template.FindName("PART_ScrollCorner", scroll) as Rectangle;
        var cornerRect = default(RectD);
        if (corner is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            var p = corner.TranslatePoint(default, root);
            cornerRect = new RectD(p.X, p.Y, corner.ActualWidth, corner.ActualHeight);
        }

        return new WpfResult(vertical, horizontal, cornerRect, scroll.HorizontalOffset, scroll.VerticalOffset);
    }

    private static void Layout(FrameworkElement root, double cw, double ch)
    {
        root.Measure(new Size(cw, ch));
        root.Arrange(new Rect(0, 0, cw, ch));
        root.UpdateLayout();
    }

    private static WpfBox ReadBar(ScrollViewer scroll, UIElement root, string partName)
    {
        if (scroll.Template.FindName(partName, scroll) is not ScrollBar bar || bar.Visibility != Visibility.Visible)
        {
            return new WpfBox(false, default, default, default);
        }

        var barOrigin = bar.TranslatePoint(default, root);
        var barRect = new RectD(barOrigin.X, barOrigin.Y, bar.ActualWidth, bar.ActualHeight);
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        var trackOrigin = track.TranslatePoint(default, root);
        var trackRect = new RectD(trackOrigin.X, trackOrigin.Y, track.ActualWidth, track.ActualHeight);
        var thumb = track.Thumb;
        var thumbRect = default(RectD);
        if (thumb is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            var p = thumb.TranslatePoint(default, root);
            thumbRect = new RectD(p.X, p.Y, thumb.ActualWidth, thumb.ActualHeight);
        }

        return new WpfBox(true, barRect, trackRect, thumbRect);
    }

    // ---- nạp Themes/DarkScrollBars.xaml thật ----

    private static ResourceDictionary LoadTheme()
    {
        if (Application.Current is null)
        {
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
            catch (InvalidOperationException)
            {
                // Một test khác vừa tạo Application: dùng chung.
            }
        }

        return new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PhotoReview.App.WpfWindows;component/Themes/DarkScrollBars.xaml", UriKind.Absolute),
        };
    }

    private static class Sta
    {
        public static void Run(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}

