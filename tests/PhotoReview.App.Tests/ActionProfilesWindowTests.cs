using System.Reflection;
using System.Windows;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests;

/// <summary>
/// The list/detail editing of <see cref="ActionProfilesWindow"/> (selection loads the fields, switching saves the edits,
/// Add/Remove). The window is constructed for real on an STA thread but never shown; the button handlers are invoked
/// directly, and nothing here reaches a message box or file dialog (the guard in <see cref="StaUi"/> would fail the test).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class ActionProfilesWindowTests
{
    private static void EnsureResourceAssembly()
    {
        try
        {
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
        }
        catch
        {
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?.SetValue(null, typeof(MainWindow).Assembly);
        }
    }

    private static void Click(ActionProfilesWindow window, string handler)
        => typeof(ActionProfilesWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);

    private static List<ReviewAction> Source() =>
    [
        new() { Name = "Pick", Shortcut = "F3", Operation = FileOperationType.Move, Destination = "Picks", Confirm = true },
        new() { Name = "Backup", Shortcut = "F4", Operation = FileOperationType.Copy, Destination = "Bak", Confirm = false },
        new() { Name = "Trash", Shortcut = "F5", Operation = FileOperationType.Recycle, Destination = "", Confirm = true },
    ];

    [Fact]
    public void Constructor_ClonesTheActions_SelectsTheFirstAndLoadsItsFields()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var source = Source();

            var window = new ActionProfilesWindow(source);

            Assert.NotSame(source[0], window.Actions[0]);
            Assert.Equal(["Pick", "Backup", "Trash"], window.Actions.Select(a => a.Name));
            Assert.Equal(0, window.ActionList.SelectedIndex);
            Assert.Equal(("Pick", "F3", "Picks", true, 0),
                (window.NameText.Text, window.ShortcutText.Text, window.DestinationText.Text, window.ConfirmCheck.IsChecked == true, window.OperationCombo.SelectedIndex));
            window.Actions[0].Name = "edited";
            Assert.Equal("Pick", source[0].Name); // the caller's list is only replaced on Apply
        });
    }

    [Theory]
    [InlineData(1, "Backup", 1)]
    [InlineData(2, "Trash", 2)]
    public void SelectingAnotherAction_LoadsItsFieldsAndOperation(int index, string name, int operationIndex)
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var window = new ActionProfilesWindow(Source());

            window.ActionList.SelectedIndex = index;

            Assert.Equal(name, window.NameText.Text);
            Assert.Equal(operationIndex, window.OperationCombo.SelectedIndex);
            Assert.Equal(window.Actions[index].Confirm, window.ConfirmCheck.IsChecked);
        });
    }

    [Fact]
    public void SelectingAnotherAction_SavesTheEditsOfThePreviousOne()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var window = new ActionProfilesWindow(Source());

            window.NameText.Text = "  Renamed  ";
            window.ShortcutText.Text = " Return ";
            window.DestinationText.Text = "  Elsewhere ";
            window.ConfirmCheck.IsChecked = false;
            window.OperationCombo.SelectedIndex = 1;
            window.ActionList.SelectedIndex = 1;

            var saved = window.Actions[0];
            Assert.Equal(("Renamed", "Enter", "Elsewhere", false, FileOperationType.Copy),
                (saved.Name, saved.Shortcut, saved.Destination, saved.Confirm, saved.Operation));
            Assert.Equal("Backup", window.NameText.Text);
        });
    }

    [Theory]
    [InlineData(0, FileOperationType.Move)]
    [InlineData(1, FileOperationType.Copy)]
    [InlineData(2, FileOperationType.Recycle)]
    public void SavingAnAction_MapsTheOperationComboIndexToTheOperation(int comboIndex, FileOperationType expected)
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var window = new ActionProfilesWindow(Source());

            window.OperationCombo.SelectedIndex = comboIndex;
            window.ActionList.SelectedIndex = 1;

            Assert.Equal(expected, window.Actions[0].Operation);
        });
    }

    [Fact]
    public void Add_AppendsANewMoveActionWithTheNextFreeShortcut_AndSelectsIt()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var source = Source();
            source[1].Shortcut = "F6";
            source[2].Shortcut = "F7"; // F6 and F7 are taken, so the new action gets F8
            var window = new ActionProfilesWindow(source);
            window.NameText.Text = "Pending edit"; // Add saves the open action first

            Click(window, "Add_Click");

            Assert.Equal("Pending edit", window.Actions[0].Name);
            Assert.Equal(4, window.Actions.Count);
            var added = window.Actions[3];
            Assert.Equal((Tr.ActionProfilesNewActionName, "F8", FileOperationType.Move, "Output"),
                (added.Name, added.Shortcut, added.Operation, added.Destination));
            Assert.Same(added, window.ActionList.SelectedItem);
            Assert.Equal(Tr.ActionProfilesNewActionName, window.NameText.Text);
        });
    }

    [Fact]
    public void Remove_DropsTheSelectedAction_AndSelectsTheNeighbourAtTheSameIndex()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var window = new ActionProfilesWindow(Source());
            window.ActionList.SelectedIndex = 1;

            Click(window, "Remove_Click");

            Assert.Equal(["Pick", "Trash"], window.Actions.Select(a => a.Name));
            Assert.Equal(1, window.ActionList.SelectedIndex);
            Assert.Equal("Trash", window.NameText.Text);

            Click(window, "Remove_Click"); // the last one: selection falls back to the new last item
            Assert.Equal(["Pick"], window.Actions.Select(a => a.Name));
            Assert.Equal(0, window.ActionList.SelectedIndex);
            Assert.Equal("Pick", window.NameText.Text);
        });
    }

    [Fact]
    public void Remove_OfTheOnlyAction_ClearsTheDetailFields_AndAFurtherRemoveIsANoOp()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var window = new ActionProfilesWindow([new ReviewAction { Name = "Only", Shortcut = "F3", Destination = "D", Confirm = true }]);

            Click(window, "Remove_Click");

            Assert.Empty(window.Actions);
            Assert.Equal(("", "", "", false), (window.NameText.Text, window.ShortcutText.Text, window.DestinationText.Text, window.ConfirmCheck.IsChecked == true));
            Click(window, "Remove_Click"); // nothing selected: a no-op, not an exception
            Assert.Empty(window.Actions);
        });
    }

    [Fact]
    public void Remove_WithNothingSelected_DoesNothing()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();
            var window = new ActionProfilesWindow(Source());
            window.ActionList.SelectedIndex = -1;

            Click(window, "Remove_Click");

            Assert.Equal(3, window.Actions.Count);
        });
    }

    [Fact]
    public void ConstructorWithNoActions_SelectsNothing()
    {
        StaUi.Run(() =>
        {
            EnsureResourceAssembly();

            var window = new ActionProfilesWindow([]);

            Assert.Empty(window.Actions);
            Assert.Equal(-1, window.ActionList.SelectedIndex);
        });
    }
}
