using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// RV-T62: the right-click "Click zoom level" &gt; "Custom..." dialog accepts exactly
/// [<see cref="AppSettings.MinClickZoomPercent"/>, <see cref="AppSettings.MaxClickZoomPercent"/>] (trimmed integers) and
/// otherwise shows its inline error and stays open. The dialog is really shown modally (DialogResult can only be set then);
/// the OK click runs from its Loaded event and the verdict is read back before the dialog closes.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class ClickZoomCustomDialogTests
{
    private static readonly MethodInfo OkClick =
        typeof(ClickZoomCustomDialog).GetMethod("Ok_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed record Outcome(bool? ShownResult, int Value, Visibility ErrorVisibility, string ErrorText, bool? ResultAfterOk);

    private static async Task<Outcome> TypeAndOkAsync(string text)
    {
        Outcome? outcome = null;
        await StaTestHost.RunAsync(() =>
        {
            var dialog = new ClickZoomCustomDialog(100);
            Visibility errorVisibility = default;
            string errorText = string.Empty;
            bool? resultAfterOk = null;
            dialog.Loaded += (_, _) =>
            {
                dialog.PercentText.Text = text;
                OkClick.Invoke(dialog, [dialog, new RoutedEventArgs()]);
                errorVisibility = dialog.ErrorText.Visibility;
                errorText = dialog.ErrorText.Text;
                try { resultAfterOk = dialog.DialogResult; } catch (InvalidOperationException) { }
                if (dialog.IsVisible) dialog.Close(); // an accepted value already closed it
            };
            var shown = dialog.ShowDialog();
            outcome = new Outcome(shown, dialog.Value, errorVisibility, errorText, resultAfterOk);
            return Task.CompletedTask;
        });
        return outcome!;
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9")]    // Min - 1
    [InlineData("801")]  // Max + 1
    [InlineData("-5")]
    [InlineData("12.5")]
    public async Task Ok_InvalidInput_ShowsErrorAndDoesNotAccept(string text)
    {
        var outcome = await TypeAndOkAsync(text);

        Assert.Equal(Visibility.Visible, outcome.ErrorVisibility);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ErrorText));
        Assert.NotEqual(true, outcome.ResultAfterOk);
        Assert.NotEqual(true, outcome.ShownResult);
        Assert.Equal(0, outcome.Value);
    }

    [Theory]
    [InlineData("10", 10)]     // Min
    [InlineData("800", 800)]   // Max
    [InlineData(" 100 ", 100)] // surrounding blanks are tolerated
    [InlineData("250", 250)]
    public async Task Ok_ValidInput_SetsValueAndAccepts(string text, int expected)
    {
        var outcome = await TypeAndOkAsync(text);

        Assert.Equal(Visibility.Collapsed, outcome.ErrorVisibility);
        Assert.True(outcome.ShownResult);
        Assert.Equal(expected, outcome.Value);
    }
}
