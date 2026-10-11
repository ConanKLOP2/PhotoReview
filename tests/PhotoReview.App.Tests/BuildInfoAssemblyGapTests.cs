using System.Reflection;
using PhotoReview.Core.Localization;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>Pins for <see cref="BuildInfo"/> found by Stryker (mutation gaps): the assembly overloads and a leading '+'.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // reads the ambient Localizer through Tr
public sealed class BuildInfoAssemblyGapTests
{
    [Fact]
    public void GetVersion_OfTheAppAssembly_IsItsInformationalVersion()
    {
        var assembly = typeof(PhotoReview.App.App).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(expected, BuildInfo.GetVersion(assembly));
    }

    [Fact]
    public void Describe_OfTheAppAssembly_NamesTheVersionAndCommit()
    {
        var assembly = typeof(PhotoReview.App.App).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);

        var text = BuildInfo.Describe(assembly);

        Assert.Contains(informational[..plus], text, StringComparison.Ordinal);
        Assert.EndsWith(informational[(plus + 1)..], text, StringComparison.Ordinal);
        Assert.Contains("build", text, StringComparison.OrdinalIgnoreCase); // the BuildTime metadata is read too
    }

    [Fact]
    public void Describe_WithAVersionThatStartsWithAPlus_ShowsAnEmptyVersionAndTheCommit()
    {
        var text = BuildInfo.Describe("+abc1234", null);

        Assert.Equal(string.Join(" · ", Tr.SettingsVersionNumber(string.Empty), "abc1234"), text);
    }
}
