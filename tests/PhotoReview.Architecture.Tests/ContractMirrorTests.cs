using System.Windows.Input;
using System.Windows.Threading;
using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Architecture.Tests;

/// <summary>
/// C-04/C-06 (NO-WPF-EXEC-PLAN mục 5): các enum không-WPF là bản sao của enum WPF để config.json (tên phím) và adapter
/// <c>(KeyId)(int)e.Key</c>, <c>(PointerButton)(int)e.ChangedButton</c>, <c>(KeyModifiers)(int)Keyboard.Modifiers</c> đúng.
/// So bằng reflection với WPF của .NET đang chạy test (project này UseWPF).
/// </summary>
public sealed class ContractMirrorTests
{
    private static Dictionary<string, long> NamesAndValues(Type enumType) =>
        enumType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .ToDictionary(f => f.Name, f => Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);

    [Theory(DisplayName = "C-06: KeyId/PointerButton/KeyModifiers mirror WPF Key/MouseButton/ModifierKeys 1:1 (names and values, aliases included)")]
    [InlineData(typeof(KeyId), typeof(Key))]
    [InlineData(typeof(PointerButton), typeof(MouseButton))]
    [InlineData(typeof(KeyModifiers), typeof(ModifierKeys))]
    [Trait("Category", "Architecture")]
    public void Enum_MirrorsWpfEnum(Type contract, Type wpf)
    {
        var expected = NamesAndValues(wpf);
        var actual = NamesAndValues(contract);

        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), actual.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(wpf.IsDefined(typeof(FlagsAttribute), false), contract.IsDefined(typeof(FlagsAttribute), false));
    }

    [Fact(DisplayName = "C-04: every UiPriority maps by name to a WPF DispatcherPriority")]
    [Trait("Category", "Architecture")]
    public void UiPriority_NamesExistInDispatcherPriority()
    {
        var names = Enum.GetNames<UiPriority>();

        Assert.Equal(["Send", "Normal", "Render", "Background"], names);
        Assert.All(names, n => Assert.True(Enum.TryParse<DispatcherPriority>(n, ignoreCase: false, out _), n));
    }
}
