using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Null members, spelling variants and randomized conflicts for <see cref="SettingsValidator"/> (asserting the exact message, not just non-null).</summary>
public sealed class SettingsValidatorRobustnessTests
{
    /// <summary>Accepts any single key name made of letters/digits, the shape real WPF key names have.</summary>
    private sealed class LetterDigitKeys : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => keyName.Length > 0 && keyName.All(char.IsAsciiLetterOrDigit);
    }

    private static SettingsValidator Validator => new(new LetterDigitKeys());

    [Fact(DisplayName = "Null Shortcuts is reported as an invalid shortcut, not a reflection exception")]
    public void NullShortcuts_ReportsInvalid()
    {
        var settings = new AppSettings { Shortcuts = null! };

        Assert.Equal(Tr.CoreSettingsShortcutInvalid(nameof(AppSettings.Shortcuts)), Validator.ValidateShortcuts(settings));
    }

    [Fact(DisplayName = "A null action entry is reported as an invalid action, not a NullReferenceException")]
    public void NullActionEntry_ReportsInvalidAction()
    {
        var settings = new AppSettings { Actions = [null!] };

        Assert.Equal(Tr.CoreSettingsActionInvalid, Validator.ValidateShortcuts(settings));
    }

    [Fact(DisplayName = "Null Actions list is treated as no actions")]
    public void NullActions_IsFine()
    {
        Assert.Null(Validator.ValidateShortcuts(new AppSettings { Actions = null! }));
    }

    [Theory(DisplayName = "Each mandatory shortcut left blank names exactly that shortcut in the error")]
    [InlineData(nameof(ShortcutMappings.Next))]
    [InlineData(nameof(ShortcutMappings.Previous))]
    [InlineData(nameof(ShortcutMappings.SendToRecycleBin))]
    [InlineData(nameof(ShortcutMappings.Compare))]
    [InlineData(nameof(ShortcutMappings.NextFolder))]
    [InlineData(nameof(ShortcutMappings.PreviousFolder))]
    [InlineData(nameof(ShortcutMappings.FirstImage))]
    [InlineData(nameof(ShortcutMappings.ZoomIn))]
    [InlineData(nameof(ShortcutMappings.ZoomOut))]
    [InlineData(nameof(ShortcutMappings.ToggleFit))]
    [InlineData(nameof(ShortcutMappings.Skip))]
    [InlineData(nameof(ShortcutMappings.Undo))]
    [InlineData(nameof(ShortcutMappings.Fullscreen))]
    public void BlankMandatory_NamesTheProperty(string property)
    {
        var settings = new AppSettings();
        typeof(ShortcutMappings).GetProperty(property)!.SetValue(settings.Shortcuts, "  ");

        Assert.Equal(Tr.CoreSettingsShortcutInvalid(property), Validator.ValidateShortcuts(settings));
    }

    [Fact(DisplayName = "Optional shortcuts may be blank; a duplicate among optional ones is still a conflict")]
    public void Optional_BlankOk_DuplicateStillConflicts()
    {
        var ok = new AppSettings();
        ok.Shortcuts.LastImage = ok.Shortcuts.ZoomActualSize = ok.Shortcuts.ToggleInfoOverlay = ok.Shortcuts.MoveToFolder = ok.Shortcuts.CopyToFolder = "";
        Assert.Null(Validator.ValidateShortcuts(ok));

        var clash = new AppSettings();
        clash.Shortcuts.CopyToFolder = clash.Shortcuts.MoveToFolder;
        var error = Validator.ValidateShortcuts(clash);
        Assert.Equal(Tr.CoreSettingsShortcutDuplicate(clash.Shortcuts.MoveToFolder, "MoveToFolder, CopyToFolder"), error);
    }

    [Fact(DisplayName = "Whitespace and case differences do not hide a duplicate")]
    public void Duplicate_IgnoresCaseAndPadding()
    {
        var settings = new AppSettings { Actions = [new ReviewAction { Name = "A", Shortcut = " f7 " }, new ReviewAction { Name = "B", Shortcut = "F7" }] };

        var error = Validator.ValidateShortcuts(settings);

        Assert.NotNull(error);
        Assert.Contains("F7", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Tr.CoreSettingsActionBindingName("A"), error, StringComparison.Ordinal);
        Assert.Contains(Tr.CoreSettingsActionBindingName("B"), error, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Fuzz: random shortcut assignments are reported as conflicting exactly when two bindings share a key (oracle)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Fuzz_ConflictOracle(int seed)
    {
        var r = new Random(seed);
        string[] pool = ["A", "B", "C", "D", "E", "F1", "F2", "F3", "F4", "F5", "G", "H", "J", "K", "L", "N", "O", "P", "Q", "R", "S"];
        for (var round = 0; round < 500; round++)
        {
            var settings = new AppSettings { Actions = [] };
            var used = new List<string>();
            string Pick()
            {
                // Mostly unique keys with occasional deliberate repeats and case changes.
                var key = r.Next(6) == 0 && used.Count > 0 ? used[r.Next(used.Count)] : pool[r.Next(pool.Length)] + r.Next(0, 1000);
                if (r.Next(4) == 0) key = key.ToLowerInvariant();
                used.Add(key);
                return key;
            }
            foreach (var p in typeof(ShortcutMappings).GetProperties().Where(p => p.Name != nameof(ShortcutMappings.MoveToFolder2)))
            {
                p.SetValue(settings.Shortcuts, r.Next(8) == 0 && ShortcutMappings.IsOptional(p.Name) ? "" : Pick());
            }
            for (var i = r.Next(0, 4); i > 0; i--) settings.Actions.Add(new ReviewAction { Name = "Act" + i, Shortcut = Pick() });

            var keys = typeof(ShortcutMappings).GetProperties().Where(p => p.Name != nameof(ShortcutMappings.MoveToFolder2))
                .Select(p => (string)p.GetValue(settings.Shortcuts)!).Concat(settings.Actions.Select(a => a.Shortcut))
                .Where(k => k.Length > 0).Select(k => k.ToUpperInvariant()).ToList();
            var hasConflict = keys.Count != keys.Distinct().Count();

            var error = Validator.ValidateShortcuts(settings);

            Assert.Equal(hasConflict, error is not null);
        }
    }
}
