using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>DiagOptions parses PHOTOREVIEW_DIAG_* once per snapshot; <see cref="DiagOptions.ResetForTests"/> re-reads the environment.</summary>
[Collection("GlobalState")] // mutates process environment variables and the DiagOptions snapshot
public sealed class DiagOptionsTests : IDisposable
{
    private static readonly string[] Vars =
        [DiagOptions.PreReadVar, DiagOptions.PreloadWorkersVar, DiagOptions.DisableDiskCacheVar, DiagOptions.ForceLogVar, DiagOptions.InstanceLabelVar];

    private readonly Dictionary<string, string?> _saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public DiagOptionsTests() => Apply();

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        DiagOptions.ResetForTests();
    }

    private static void Apply(params (string Name, string? Value)[] set)
    {
        foreach (var name in Vars) Environment.SetEnvironmentVariable(name, null);
        foreach (var (name, value) in set) Environment.SetEnvironmentVariable(name, value);
        DiagOptions.ResetForTests();
    }

    [Fact(DisplayName = "With no variable set every flag is off and Describe says (none)")]
    public void Defaults_AreAllDisabled()
    {
        Assert.False(DiagOptions.PreRead);
        Assert.Null(DiagOptions.PreloadWorkers);
        Assert.False(DiagOptions.DisableDiskCache);
        Assert.False(DiagOptions.ForceLog);
        Assert.Null(DiagOptions.InstanceLabel);
        Assert.False(DiagOptions.AnyEnabled);
        Assert.Equal("(none)", DiagOptions.Describe());
    }

    [Theory(DisplayName = "Each boolean flag is on only for the value 1")]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("", false)]
    public void BooleanFlags_OnlyOneEnables(string value, bool expected)
    {
        foreach (var name in new[] { DiagOptions.PreReadVar, DiagOptions.DisableDiskCacheVar, DiagOptions.ForceLogVar })
        {
            Apply((name, value));

            var actual = name switch
            {
                DiagOptions.PreReadVar => DiagOptions.PreRead,
                DiagOptions.DisableDiskCacheVar => DiagOptions.DisableDiskCache,
                _ => DiagOptions.ForceLog,
            };
            Assert.Equal(expected, actual);
            Assert.Equal(expected, DiagOptions.AnyEnabled);
        }
    }

    [Fact(DisplayName = "Turning on exactly one flag turns on only that flag")]
    public void SingleFlag_DoesNotLeakIntoOthers()
    {
        Apply((DiagOptions.PreReadVar, "1"));
        Assert.Equal((true, false, false), (DiagOptions.PreRead, DiagOptions.DisableDiskCache, DiagOptions.ForceLog));

        Apply((DiagOptions.DisableDiskCacheVar, "1"));
        Assert.Equal((false, true, false), (DiagOptions.PreRead, DiagOptions.DisableDiskCache, DiagOptions.ForceLog));

        Apply((DiagOptions.ForceLogVar, "1"));
        Assert.Equal((false, false, true), (DiagOptions.PreRead, DiagOptions.DisableDiskCache, DiagOptions.ForceLog));
    }

    [Theory(DisplayName = "PRELOAD_WORKERS accepts 0..16 and treats everything else as unset")]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("8", 8)]
    [InlineData("16", 16)]
    [InlineData(" 5 ", 5)]
    [InlineData("17", null)]
    [InlineData("-1", null)]
    [InlineData("100", null)]
    [InlineData("abc", null)]
    [InlineData("2.5", null)]
    [InlineData("", null)]
    public void PreloadWorkers_Range(string raw, int? expected)
    {
        Apply((DiagOptions.PreloadWorkersVar, raw));

        Assert.Equal(expected, DiagOptions.PreloadWorkers);
        Assert.Equal(expected.HasValue, DiagOptions.AnyEnabled);
    }

    [Fact(DisplayName = "PRELOAD_WORKERS=0 alone counts as an active diagnostic flag")]
    public void PreloadWorkersZero_IsEnabled()
    {
        Apply((DiagOptions.PreloadWorkersVar, "0"));

        Assert.True(DiagOptions.AnyEnabled);
        Assert.Equal("PRELOAD_WORKERS=0", DiagOptions.Describe());
    }

    [Theory(DisplayName = "INSTANCE_LABEL is the raw text, or null when blank")]
    [InlineData("Agent run", "Agent run")]
    [InlineData("  padded  ", "  padded  ")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void InstanceLabel_NullWhenBlank(string raw, string? expected)
    {
        Apply((DiagOptions.InstanceLabelVar, raw));

        Assert.Equal(expected, DiagOptions.InstanceLabel);
        Assert.Equal(expected is not null, DiagOptions.AnyEnabled);
    }

    [Fact(DisplayName = "Describe lists every active flag in a stable order")]
    public void Describe_ListsActiveFlagsInOrder()
    {
        Apply(
            (DiagOptions.PreReadVar, "1"),
            (DiagOptions.PreloadWorkersVar, "2"),
            (DiagOptions.DisableDiskCacheVar, "1"),
            (DiagOptions.ForceLogVar, "1"),
            (DiagOptions.InstanceLabelVar, "lbl"));

        Assert.Equal("PREREAD=1;PRELOAD_WORKERS=2;DISABLE_DISKCACHE=1;FORCE_LOG=1;INSTANCE_LABEL=lbl", DiagOptions.Describe());
        Assert.True(DiagOptions.AnyEnabled);
    }

    [Fact(DisplayName = "The snapshot is read once: changing the environment has no effect until ResetForTests")]
    public void Snapshot_IsStableUntilReset()
    {
        Apply((DiagOptions.ForceLogVar, "1"));
        Assert.True(DiagOptions.ForceLog);

        Environment.SetEnvironmentVariable(DiagOptions.ForceLogVar, null);
        Assert.True(DiagOptions.ForceLog);

        DiagOptions.ResetForTests();
        Assert.False(DiagOptions.ForceLog);
    }
}
