using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// APP-01/02 (accessibility, Q-R6): every input a user can reach in the user-facing windows has a UI Automation
/// name, either explicit (<c>AutomationProperties.Name</c>) or through its visible label
/// (<c>AutomationProperties.LabeledBy</c>). The name is what a screen reader announces; without it a text box
/// is just "edit". The check asks the real automation peer, so it fails for exactly what Narrator would miss.
/// </summary>
[Collection("GlobalState")]
public sealed class AccessibilityNamesTests
{
    public static TheoryData<string> Windows => new()
    {
        "Main", "Settings", "ActionProfiles", "Recovery", "BatchReview", "Diagnostics", "Benchmark",
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public async Task EveryInputHasAnAccessibleName(string windowName)
    {
        using var dataRoot = new DataRootFixture();
        using var temp = new TempRoot("a11y");
        await StaTestHost.RunAsync(() =>
        {
            var window = Create(windowName, temp.Path);
            try
            {
                var unnamed = new List<string>();
                var seen = 0;
                Walk(window, control =>
                {
                    if (control is not (TextBox or ComboBox or ToggleButton or Button)) return; // ToggleButton covers CheckBox, RadioButton and the tools "⋮" toggle
                    seen++;
                    var peer = UIElementAutomationPeer.CreatePeerForElement(control);
                    // UIA clients (Narrator) announce the Name property, or the LabeledBy element's name when Name is empty.
                    var name = peer?.GetName();
                    if (string.IsNullOrWhiteSpace(name)) name = LabelName(control);
                    // An icon-only button's peer name is its glyph text (e.g. an emoji); that is not an accessible name.
                    if (string.IsNullOrWhiteSpace(name) || !name.Any(char.IsLetterOrDigit))
                        unnamed.Add($"{control.GetType().Name} '{(control as FrameworkElement)?.Name}'");
                });

                Assert.True(seen > 0, $"{windowName}: no inputs found - the walker is broken");
                Assert.True(unnamed.Count == 0,
                    $"{windowName}: controls without an automation name (add x:Name to the label + AutomationProperties.LabeledBy, " +
                    $"or AutomationProperties.Name=\"{{loc:Tr ...}}\" for icon-only controls):\n{string.Join("\n", unnamed)}");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static Window Create(string name, string tempPath) => name switch
    {
        "Main" => TestAppHost.CreateMainWindow(null),
        "Settings" => new SettingsWindow(new AppSettings()),
        "ActionProfiles" => new ActionProfilesWindow(ReviewAction.Defaults()),
        "Recovery" => CreateRecovery(),
        "BatchReview" => new BatchReviewWindow([Path.Combine(tempPath, "a.jpg")]),
        "Diagnostics" => new DiagnosticsWindow(new PhotoReview.Core.Diagnostics.ReviewMetrics().Snapshot()),
        "Benchmark" => new BenchmarkWindow(null),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // The path panels get their title (the label of the path box) when an entry is selected; show both.
    private static RecoveryWindow CreateRecovery()
    {
        var window = new RecoveryWindow([]);
        var view = new RecoveryPathView("Source", "C:\a.jpg", string.Empty, System.Windows.Media.Brushes.Gray, string.Empty, string.Empty, null);
        window.SourceDetails.Show(view);
        window.DestinationDetails.Show(view with { Title = "Destination" });
        return window;
    }

    // Read the attribute and create the label's peer ourselves: AutomationPeer.GetLabeledBy() only sees peers a UIA
    // client has already created, and none is attached in a test.
    private static string? LabelName(Control control) =>
        System.Windows.Automation.AutomationProperties.GetLabeledBy(control) is { } label
            ? UIElementAutomationPeer.CreatePeerForElement(label)?.GetName()
            : null;

    /// <summary>Logical tree walk: covers everything declared in XAML (including Popup content) but not template internals.</summary>
    private static void Walk(object node, Action<Control> visit)
    {
        if (node is Control control) visit(control);
        if (node is not DependencyObject dep) return;
        foreach (var child in LogicalTreeHelper.GetChildren(dep)) Walk(child, visit);
    }
}
