using System.Windows.Input;
using PhotoReview.App.Input;
using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// G-KEY (C-06): <see cref="KeyIdMapping"/> must agree with WPF's own <see cref="KeyInterop"/> for every virtual-key code and every
/// <see cref="Key"/> value (this needs WPF, which is why it lives here and not next to the WPF-free mapping).
/// </summary>
public class KeyIdMappingTests
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    [Fact(DisplayName = "FromVirtualKey(vk, false) equals KeyInterop.KeyFromVirtualKey for every code 0..255 and outside it")]
    public void FromVirtualKey_MatchesKeyInterop_ForEveryCode()
    {
        foreach (var vk in Enumerable.Range(0, 256).Concat([-1, 256, 0x1000, int.MaxValue, int.MinValue]))
        {
            var expected = (KeyId)(int)KeyInterop.KeyFromVirtualKey(vk);
            Assert.True(expected == KeyIdMapping.FromVirtualKey(vk, isExtended: false), $"vk 0x{vk:X}");
        }
    }

    [Fact(DisplayName = "FromVirtualKey with the extended flag changes only the generic Control and Alt codes (to the right-hand keys)")]
    public void FromVirtualKey_Extended_OnlyControlAndAltBecomeRight()
    {
        foreach (var vk in Enumerable.Range(0, 256))
        {
            var expected = vk switch
            {
                VkControl => KeyId.RightCtrl,
                VkMenu => KeyId.RightAlt,
                _ => (KeyId)(int)KeyInterop.KeyFromVirtualKey(vk),
            };
            Assert.True(expected == KeyIdMapping.FromVirtualKey(vk, isExtended: true), $"vk 0x{vk:X}");
        }
    }

    [Fact(DisplayName = "ToVirtualKey equals KeyInterop.VirtualKeyFromKey for every Key value and for values outside the enum")]
    public void ToVirtualKey_MatchesKeyInterop_ForEveryKey()
    {
        foreach (var value in Enumerable.Range(-2, 180).Concat([1000, int.MaxValue, int.MinValue]))
        {
            var expected = KeyInterop.VirtualKeyFromKey((Key)value);
            Assert.True(expected == KeyIdMapping.ToVirtualKey((KeyId)value), $"key {value}");
        }
    }

    [Fact(DisplayName = "Every KeyId that has a virtual-key code maps back to a key with that same code")]
    public void ToVirtualKey_ThenFromVirtualKey_KeepsTheCode()
    {
        foreach (var key in Enum.GetValues<KeyId>())
        {
            var vk = KeyIdMapping.ToVirtualKey(key);
            if (vk == 0) continue;
            Assert.Equal(vk, KeyIdMapping.ToVirtualKey(KeyIdMapping.FromVirtualKey(vk, false)));
        }
    }

    [Fact(DisplayName = "The keys without a virtual-key code are exactly None, LineFeed, System and DeadCharProcessed")]
    public void ToVirtualKey_ZeroOnlyForKeysWithoutACode()
    {
        var withoutCode = Enum.GetValues<KeyId>().Where(k => KeyIdMapping.ToVirtualKey(k) == 0).Distinct().OrderBy(k => (int)k).ToArray();
        Assert.Equal([KeyId.None, KeyId.LineFeed, KeyId.System, KeyId.DeadCharProcessed], withoutCode);
    }

    [Theory(DisplayName = "TryParse takes WPF key names (aliases canonicalised, case of unknown names strict)")]
    [InlineData("Enter", KeyId.Enter)]
    [InlineData("Return", KeyId.Enter)]
    [InlineData("enter", KeyId.Enter)]
    [InlineData("PageUp", KeyId.PageUp)]
    [InlineData("Prior", KeyId.PageUp)]
    [InlineData("F11", KeyId.F11)]
    [InlineData("A", KeyId.A)]
    [InlineData("D2", KeyId.D2)]
    [InlineData(" Delete ", KeyId.Delete)]
    public void TryParse_ValidName_Parses(string name, KeyId expected)
    {
        Assert.True(KeyIdMapping.TryParse(name, out var key));
        Assert.Equal(expected, key);
    }

    [Theory(DisplayName = "TryParse rejects numbers, lists, unknown names, wrong case and blanks")]
    [InlineData("999")]
    [InlineData("1")]
    [InlineData("172")]
    [InlineData("-1")]
    [InlineData("+44")]
    [InlineData("A,B")]
    [InlineData("NotAKey")]
    [InlineData("a")]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParse_Invalid_Rejected(string name)
    {
        Assert.False(KeyIdMapping.TryParse(name, out var key));
        Assert.Equal(KeyId.None, key);
    }

    [Fact(DisplayName = "TryParse and ShortcutKeyName agree on every WPF key name that can be a shortcut")]
    public void TryParse_AgreesWithShortcutKeyName_ForEveryKeyName()
    {
        foreach (var name in Enum.GetNames<KeyId>())
        {
            if (!KeyIdMapping.TryParse(name, out var viaMapping)) continue;
            var accepted = ShortcutKeyName.TryParse(name, out var viaShortcut);
            Assert.True(accepted == !ShortcutKeyName.IsReserved(viaMapping), name);
            if (accepted) Assert.Equal(viaMapping, viaShortcut);
        }
    }
}
