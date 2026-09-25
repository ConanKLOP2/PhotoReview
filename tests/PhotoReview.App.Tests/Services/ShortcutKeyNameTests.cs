using System.Windows.Input;
using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Services;

public class ShortcutKeyNameTests
{
    [Theory(DisplayName = "Real command keys parse, case-insensitively and trimmed")]
    [InlineData("Right", Key.Right)]
    [InlineData(" delete ", Key.Delete)]
    [InlineData("F11", Key.F11)]
    [InlineData("Space", Key.Space)]
    public void TryParse_ValidKey_Parses(string name, Key expected)
    {
        Assert.True(ShortcutKeyName.TryParse(name, out var key));
        Assert.Equal(expected, key);
    }

    [Theory(DisplayName = "Numbers, comma lists, unknown names and never-firing keys are rejected")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("A,B")]
    [InlineData("NotAKey")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Escape")]
    [InlineData("Tab")]
    [InlineData("ImeProcessed")]
    [InlineData("None")]
    [InlineData("LeftCtrl")]
    public void TryParse_Unusable_Rejected(string? name)
    {
        Assert.False(ShortcutKeyName.TryParse(name, out _));
    }
}
