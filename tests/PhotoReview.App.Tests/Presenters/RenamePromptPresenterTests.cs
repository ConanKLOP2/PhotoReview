using PhotoReview.App.Presenters;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>FEAT-RENAME (PR3): the rules behind the name box: the policy's verdict as localized text, the "taken" question, the request.</summary>
[Collection("GlobalState")] // switches the ambient localizer
public sealed class RenamePromptPresenterTests
{
    private const string Path = @"C:\Photos\IMG_0042.JPG";

    private static RenamePromptPresenter Create(Func<string, bool, bool>? taken = null) =>
        new(Path, typed => RenameNamePolicy.Check(typed, Path), taken);

    [Fact]
    public void Initial_IsTheStemWithoutTheExtension_AndTheExtensionKeepsItsCase()
    {
        var presenter = Create();

        Assert.Equal("IMG_0042", presenter.InitialText);
        Assert.Equal(".JPG", presenter.Extension);
        var request = presenter.ToRequest();
        Assert.Equal("IMG_0042", request.InitialName);
        Assert.Equal(".JPG", request.Extension);
    }

    [Fact]
    public void ADottedStem_AndAnExtensionlessFile_AreSplitLikeExplorer()
    {
        var dotted = new RenamePromptPresenter(@"C:\p\a.v2.final.png", t => RenameNamePolicy.Check(t, @"C:\p\a.v2.final.png"));
        var none = new RenamePromptPresenter(@"C:\p\README", t => RenameNamePolicy.Check(t, @"C:\p\README"));

        Assert.Equal("a.v2.final", dotted.InitialText);
        Assert.Equal(".png", dotted.Extension);
        Assert.Equal("README", none.InitialText);
        Assert.Equal(string.Empty, none.Extension);
    }

    [Theory]
    [InlineData("", RenameNameProblem.Empty)]
    [InlineData("a?b", RenameNameProblem.InvalidCharacter)]
    [InlineData("a.", RenameNameProblem.TrailingDotOrSpace)]
    [InlineData("CON", RenameNameProblem.ReservedDeviceName)]
    public void EveryPolicyProblem_MapsToItsOwnLocalizedText(string typed, RenameNameProblem problem)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var presenter = Create();

        var error = presenter.TryAccept(typed);

        Assert.Equal(problem, RenameNamePolicy.Check(typed, Path).Problem);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(Expected(problem), error);
    }

    private static string Expected(RenameNameProblem problem) => problem switch
    {
        RenameNameProblem.Empty => Tr.DialogRenameErrorEmpty,
        RenameNameProblem.InvalidCharacter => Tr.DialogRenameErrorInvalidCharacter,
        RenameNameProblem.TrailingDotOrSpace => Tr.DialogRenameErrorTrailingDot,
        RenameNameProblem.ReservedDeviceName => Tr.DialogRenameErrorReservedName,
        RenameNameProblem.NameTooLong => Tr.DialogRenameErrorTooLong,
        RenameNameProblem.PathTooLong => Tr.DialogRenameErrorPathTooLong,
        RenameNameProblem.SidecarNotMappable => Tr.DialogRenameErrorSidecarNotMappable,
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };

    [Fact]
    public void TooLongAndPathTooLong_AreRefused()
    {
        var presenter = Create();
        var longFolder = @"C:\" + new string('d', 230) + @"\IMG_0042.JPG";
        var deep = new RenamePromptPresenter(longFolder, t => RenameNamePolicy.Check(t, longFolder));

        Assert.Equal(Tr.DialogRenameErrorTooLong, presenter.TryAccept(new string('x', 300)));
        Assert.Equal(Tr.DialogRenameErrorPathTooLong, deep.TryAccept(new string('x', 40)));
        Assert.Null(deep.TryAccept("IMG_0042")); // keeping the length is fine
    }

    [Fact]
    public void NotRefused_TheSameName_AChangeOfCaseOnly_AndOrdinaryNames()
    {
        var asked = new List<string>();
        var presenter = Create((name, _) =>
        {
            asked.Add(name);
            return true; // everything "taken": the file itself must never be reported as a clash
        });

        Assert.Null(presenter.TryAccept("IMG_0042")); // unchanged
        Assert.Null(presenter.TryAccept("img_0042")); // case only
        Assert.Empty(asked); // the file itself is never looked up
        Assert.NotNull(presenter.TryAccept("other")); // a real clash
        Assert.Equal(["other.JPG"], asked);
    }

    [Fact]
    public void TheTakenQuestion_GetsTheCompleteNewFileName_AndKnowsWhetherItIsTheConfirmingCall()
    {
        var calls = new List<(string Name, bool Commit)>();
        var presenter = Create((name, commit) =>
        {
            calls.Add((name, commit));
            return false;
        });

        Assert.Null(presenter.TryAccept("  holiday  "));
        Assert.Null(presenter.TryAccept("holiday", isCommit: true));

        Assert.Equal([("holiday.JPG", false), ("holiday.JPG", true)], calls);
    }

    [Fact]
    public void ARefusedNameIsNeverLookedUp()
    {
        var calls = 0;
        var presenter = Create((_, _) =>
        {
            calls++;
            return false;
        });

        Assert.NotNull(presenter.TryAccept("a?b"));
        Assert.NotNull(presenter.TryAccept(null));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void WithoutATakenQuestion_NothingIsTaken()
    {
        Assert.Null(Create(null).TryAccept("anything goes"));
    }

    [Fact]
    public void TextIsLocalized_InVietnameseToo()
    {
        using var scope = TestLocalization.Use(TestLocalization.Vietnamese);
        var presenter = Create();

        Assert.Equal(Tr.DialogRenameErrorEmpty, presenter.TryAccept(""));
        Assert.Contains("trống", presenter.TryAccept("")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCaptureGroupCheck_CanBeInjected_ForThePairedJpegAndRaw()
    {
        // The view model passes the group's plan check; the presenter only maps its verdict.
        var presenter = new RenamePromptPresenter(Path, _ => new RenameNameCheck(RenameNameProblem.SidecarNotMappable, string.Empty));

        Assert.Equal(Tr.DialogRenameErrorSidecarNotMappable, presenter.TryAccept("anything"));
    }
}
