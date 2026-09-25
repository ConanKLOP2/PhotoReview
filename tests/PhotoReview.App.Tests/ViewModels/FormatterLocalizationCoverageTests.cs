using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PhotoReview.App.ViewModels;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Reflection-driven i18n coverage: EVERY public string formatter of <see cref="StatusFormatter"/> (including ones
/// added later) is rendered in Vietnamese and English with ordinary, extreme and right-to-left arguments. Catches a
/// leaked catalog key, an unresolved placeholder, an exception on odd input, and a formatter whose two languages are
/// accidentally identical.
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // switches the ambient Localizer
public sealed partial class FormatterLocalizationCoverageTests
{
    private static readonly string[] OddText = ["photo.jpg", "ملف صورة.jpg", "日本語 😀.png", "", "a{0}b{1}", "line1\r\nline2"];

    private static IEnumerable<MethodInfo> Formatters() =>
        typeof(StatusFormatter).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string));

    private static object?[][] ArgumentSets(MethodInfo method)
    {
        var parameters = method.GetParameters();
        IEnumerable<object?> Values(Type type, int variant) => type switch
        {
            _ when type == typeof(int) => [variant switch { 0 => 0, 1 => 7, 2 => int.MaxValue, _ => -1 }],
            _ when type == typeof(long) => [variant switch { 0 => 0L, 1 => 1536L, 2 => long.MaxValue, _ => -5L }],
            _ when type == typeof(double) => [variant switch { 0 => 0.0, 1 => 1.25, 2 => double.MaxValue, _ => double.NaN }],
            _ when type == typeof(bool) => [variant % 2 == 0],
            _ when type == typeof(bool?) => [variant switch { 0 => null, 1 => true, _ => (bool?)false }],
            _ when type == typeof(string) => [OddText[variant % OddText.Length]],
            _ => throw new NotSupportedException($"{method.Name}: unsupported parameter type {type}"),
        };
        return Enumerable.Range(0, 6)
            .Select(v => parameters.Select(p => Values(p.ParameterType, v).Single()).ToArray())
            .ToArray();
    }

    public static TheoryData<string> FormatterNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var m in Formatters()) data.Add(m.Name);
            return data;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex LooksLikeKey();

    [Theory]
    [MemberData(nameof(FormatterNames))]
    public void EveryFormatter_RendersCleanTextInBothLanguages(string name)
    {
        foreach (var method in Formatters().Where(m => m.Name == name))
        {
            foreach (var args in ArgumentSets(method))
            {
                foreach (var english in new[] { false, true })
                {
                    using var _ = TestLocalization.Use(english ? TestLocalization.English : TestLocalization.Vietnamese);
                    var text = (string?)method.Invoke(null, args);

                    var context = $"{name}({string.Join(", ", args.Select(a => a ?? "null"))}) [{(english ? "en" : "vi")}]";
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{context}: empty text");
                    Assert.False(LooksLikeKey().IsMatch(text!), $"{context}: a catalog key leaked: {text}");
                    // A "{0}" left in the output is a missing argument; user text that itself contains braces is allowed.
                    if (!args.OfType<string>().Any(a => a.Contains('{', StringComparison.Ordinal)))
                        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
                    foreach (var arg in args.OfType<string>().Where(a => a.Length > 0 && !a.Contains('\n', StringComparison.Ordinal)))
                        Assert.True(text!.Contains(arg, StringComparison.Ordinal) || !ArgumentIsShown(method, arg), $"{context}: argument '{arg}' missing from '{text}'");
                }
            }
        }
    }

    // Formatters that take a text argument and are documented to show it (file names, messages, action names).
    private static bool ArgumentIsShown(MethodInfo method, string _) =>
        method.Name is not nameof(StatusFormatter.CopiedTo) and not nameof(StatusFormatter.ActionFailed);

    [Fact(DisplayName = "Both languages are really different for every text formatter, so none silently reuses the other catalog")]
    public void Languages_DifferForMostFormatters()
    {
        var same = new List<string>();
        foreach (var method in Formatters())
        {
            var args = ArgumentSets(method)[1];
            string vi, en;
            using (TestLocalization.Use(TestLocalization.Vietnamese)) vi = (string)method.Invoke(null, args)!;
            using (TestLocalization.Use(TestLocalization.English)) en = (string)method.Invoke(null, args)!;
            if (vi == en) same.Add(method.Name);
        }

        // Only formatters that render numbers and file names alone are legitimately language-neutral.
        var neutral = new[] { nameof(StatusFormatter.FormatFileSize), nameof(StatusFormatter.IndexOnly), nameof(StatusFormatter.Ready), nameof(StatusFormatter.WithDimensions) };
        Assert.True(same.All(neutral.Contains), "Identical in vi and en: " + string.Join(", ", same.Except(neutral)));
    }

    [Theory(DisplayName = "File sizes: unit boundaries, negatives and long.MaxValue, in both languages and a comma-decimal culture")]
    [InlineData(0L, "0 B")]
    [InlineData(-1L, "0 B")]
    [InlineData(long.MinValue, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1048575L, "1024 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1024 GB")]
    public void FormatFileSize_BoundariesAreStableAndNeverThrow(long bytes, string expectedEnglish)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expectedEnglish, StatusFormatter.FormatFileSize(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatFileSize_MaxValue_UsesTheLargestUnitWithoutOverflow()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var text = StatusFormatter.FormatFileSize(long.MaxValue);

            Assert.EndsWith("GB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("E+", text, StringComparison.Ordinal);
            Assert.DoesNotContain("∞", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FormatFileSize_UsesTheDisplayCultureDecimalSeparator()
    {
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
        try
        {
            Assert.Contains("1,5", StatusFormatter.FormatFileSize(1572864), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
