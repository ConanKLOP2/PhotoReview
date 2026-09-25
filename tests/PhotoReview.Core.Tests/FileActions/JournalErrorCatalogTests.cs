using System.IO;
using System.Reflection;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// Every persisted <see cref="JournalErrors"/> code is described in both shipped languages, is stored as invariant
/// English (never the UI language), and unknown or cancelled failures fall back to the OS message without a code.
/// Serial collection: switches the process-wide <see cref="Localizer.Current"/>.
/// </summary>
[Collection("GlobalState")]
public sealed class JournalErrorCatalogTests : IDisposable
{
    private readonly Localizer _previous = Localizer.Current;

    public void Dispose() => Localizer.SetCurrent(_previous);

    public static TheoryData<string> AllCodes()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(JournalErrors).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }
        return data;
    }

    [Fact(DisplayName = "The code list is discovered (guards the theory against silently running on nothing)")]
    public void AllCodes_IsNotEmpty() => Assert.True(typeof(JournalErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Count(f => f.IsLiteral) >= 6);

    [Theory(DisplayName = "Each journal error code is known and has distinct English and Vietnamese text")]
    [MemberData(nameof(AllCodes))]
    public void Code_IsDescribedInBothLanguages(string code)
    {
        Assert.True(JournalErrors.IsKnown(code));

        var english = JournalErrors.EnglishText(code);
        Localizer.SetCurrent(TestLocalization.Vietnamese);
        var vietnamese = JournalErrors.LocalizedText(code);

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(vietnamese));
        Assert.NotEqual(english, vietnamese);
        Assert.DoesNotContain(code, english, StringComparison.Ordinal); // not a raw key echoed back
    }

    [Theory(DisplayName = "A coded failure is stored as its code plus English even while the UI is Vietnamese")]
    [MemberData(nameof(AllCodes))]
    public void CodedException_IsJournaledInEnglish_UiShowsVietnamese(string code)
    {
        Localizer.SetCurrent(TestLocalization.Vietnamese);
        var ex = new JournalCodedException(code);

        var (storedCode, storedText) = JournalErrors.ForJournal(ex);

        Assert.Equal(code, storedCode);
        Assert.Equal(JournalErrors.EnglishText(code), storedText);
        Assert.Equal(JournalErrors.LocalizedText(code), ex.Message);
    }

    [Fact(DisplayName = "Cancellation, and OS errors are journaled with their own message and no code")]
    public void NonCodedFailures_KeepTheirMessage_WithoutCode()
    {
        foreach (var ex in new Exception[]
                 {
                     new OperationCanceledException("cancelled"),
                     new IOException("There is not enough space on the disk."),
                     new UnauthorizedAccessException("Access to the path is denied."),
                 })
        {
            var (code, text) = JournalErrors.ForJournal(ex);
            Assert.Null(code);
            Assert.Equal(ex.Message, text);
        }
    }
}
