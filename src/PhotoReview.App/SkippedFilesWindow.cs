using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.App;

/// <summary>
/// IO05 (ADR 0007 section 3): read-only list of the files a folder scan skipped because they could
/// not be read. Built in code (no XAML) - it is a dumb dark list with a Close button.
/// </summary>
internal sealed class SkippedFilesWindow : Window
{
    public SkippedFilesWindow(IReadOnlyList<SkippedEntry> entries)
    {
        Title = Tr.MainSkippedWindowTitle;
        Width = 720;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));

        var intro = new TextBlock
        {
            Text = Tr.MainSkippedWindowIntro,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var list = new ListBox
        {
            ItemsSource = entries.Select(e => $"{e.Path}  ({e.Reason})").ToList(),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
        };
        var close = new Button
        {
            Content = Tr.MainSkippedWindowClose,
            IsCancel = true,
            IsDefault = true,
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        close.Click += (_, _) => Close();

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(list, 1);
        Grid.SetRow(close, 2);
        grid.Children.Add(intro);
        grid.Children.Add(list);
        grid.Children.Add(close);
        Content = grid;
    }
}
