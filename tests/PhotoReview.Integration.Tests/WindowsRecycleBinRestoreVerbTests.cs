using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The shell's Recycle Bin verb names follow the OS display language. These tests use fake verb lists only;
/// nothing here touches the user's real Recycle Bin.
/// </summary>
public sealed class WindowsRecycleBinRestoreVerbTests
{
    public static TheoryData<string> RestoreLabels() => new()
    {
        "Restore", "&Restore", "Restore the selected items",
        "&Wiederherstellen", "Wiederherstellen",
        "&Khôi phục", "Khôi phục",
        "&Restaurer", "&Restaurar", "&Ripristina",
        "He&rstellen", "&Gendan", "&Återställ", "&Palauta", "Przywró&ć", "Geri &yükle",
        "&Восстановить", "Відновит&и",
        "还原(&E)", "還原(&E)", "元に戻す(&E)", "복원(&E)",
        "  &restore  ",
    };

    public static TheoryData<string> OtherLabels() => new()
    {
        "Cut", "Cu&t", "&Delete", "P&roperties", "Open", "",
        "Cắt", "Xóa", "Thuộc tính",
        "Ausschneiden", "&Löschen", "Eigenschaften",
        "剪切(&T)", "删除(&D)", "属性(&R)", "切り取り(&T)", "削除(&D)", "プロパティ(&R)",
        "Вырезать", "Удалить", "Свойства",
        "&Supprimer", "&Couper", "Propriétés", "&Eliminar", "Cor&tar", "&Elimina", "&Taglia", "Proprietà",
        "&Verwijderen", "&Knippen", "Eigenschappen", "&Slet", "&Klip", "Egenskaber", "&Ta bort", "Klipp &ut", "Egenskaper",
        "&Poista", "&Leikkaa", "Ominaisuudet", "&Usuń", "Wy&tnij", "Właściwości", "&Odstranit", "&Vyjmout", "Vlastnosti",
        "&Törlés", "&Kivágás", "&Sil", "&Kes", "Özellikler", "Видалити", "Вирізати", "Властивості",
        "Διαγραφή", "Αποκοπή", "Ιδιότητες", "삭제(&D)", "잘라내기(&T)", "속성(&R)", "حذف", "قص", "خصائص", "מחק", "גזור", "מאפיינים",
    };

    [Theory]
    [MemberData(nameof(RestoreLabels))]
    public void KnownRestoreLabelsInManyLanguagesAreRecognized(string label) =>
        Assert.True(RestoreVerb.IsRestoreName(label), label);

    [Theory]
    [MemberData(nameof(OtherLabels))]
    public void OtherRecycleBinVerbsAreNeverTakenForRestore(string label) =>
        Assert.False(RestoreVerb.IsRestoreName(label), label);

    [Fact]
    public void NullOrBlankNameIsNotRestore()
    {
        Assert.False(RestoreVerb.IsRestoreName(null));
        Assert.False(RestoreVerb.IsRestoreName("   "));
    }

    [Theory]
    [InlineData(new[] { "Restore", "Cut", "Delete", "Properties" }, 0)]
    [InlineData(new[] { "Cắt", "Xóa", "&Khôi phục", "Thuộc tính" }, 2)]
    [InlineData(new[] { "剪切(&T)", "删除(&D)", "还原(&E)" }, 2)]
    [InlineData(new[] { "&Wiederherstellen", "Restore" }, 0)]
    [InlineData(new[] { "Cut", "Delete" }, -1)]
    [InlineData(new string[0], -1)]
    public void FindIndexReturnsTheFirstRestoreVerb(string[] names, int expected) =>
        Assert.Equal(expected, RestoreVerb.FindIndex(names));

    [Fact(DisplayName = "A recognised name is invoked by index and the canonical verb is not")]
    public void RecognisedNameInvokesOnlyThatVerb()
    {
        var byIndex = new List<int>();
        var canonical = new List<string>();

        var route = RestoreVerb.Invoke(["Cắt", "Khôi phục", "Xóa"], byIndex.Add, canonical.Add);

        Assert.Equal([1], byIndex);
        Assert.Empty(canonical);
        Assert.Equal("verb:1", route);
    }

    [Theory(DisplayName = "An unrecognised OS language falls back to the language-independent canonical verb exactly once")]
    [InlineData("Cut", "Delete", "Properties")]
    [InlineData("Ausschneiden", "Löschen", "Eigenschaften")]
    [InlineData("Vágás", "Törlés", "Tulajdonságok")]
    public void UnrecognisedLanguageUsesCanonicalVerb(params string[] names)
    {
        var byIndex = new List<int>();
        var canonical = new List<string>();

        var route = RestoreVerb.Invoke(names, byIndex.Add, canonical.Add);

        Assert.Empty(byIndex);
        Assert.Equal(["undelete"], canonical);
        Assert.Equal("undelete", route);
    }

    [Fact]
    public void EmptyVerbListUsesCanonicalVerb()
    {
        var canonical = new List<string>();

        RestoreVerb.Invoke([], _ => throw new InvalidOperationException("no verb to invoke"), canonical.Add);

        Assert.Equal([RestoreVerb.CanonicalVerb], canonical);
    }

    [Fact(DisplayName = "A failing verb is not retried through the canonical verb (never restore twice)")]
    public void FailingVerbPropagatesWithoutSecondAttempt()
    {
        var canonical = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            RestoreVerb.Invoke(["Restore"], _ => throw new InvalidOperationException("shell refused"), canonical.Add));

        Assert.Empty(canonical);
    }

    [Fact]
    public void CanonicalVerbIsTheShellUndeleteName() => Assert.Equal("undelete", RestoreVerb.CanonicalVerb);
}
