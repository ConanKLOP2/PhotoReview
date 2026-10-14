namespace PhotoReview.Core.Tests.FileActions;

public sealed class RenameNamePolicyTests
{
    private const string Current = @"C:\Photos\IMG_0001.jpg";

    [Theory]
    [InlineData("", RenameNameProblem.Empty)]
    [InlineData("   ", RenameNameProblem.Empty)]
    [InlineData(".jpg", RenameNameProblem.Empty)]
    [InlineData(".JPG", RenameNameProblem.Empty)]
    [InlineData("  .jpg ", RenameNameProblem.Empty)]
    public void Check_EmptyOrOnlyExtension_ReturnsEmpty(string typed, RenameNameProblem expected)
    {
        var check = RenameNamePolicy.Check(typed, Current);

        Assert.Equal(expected, check.Problem);
        Assert.False(check.IsValid);
        Assert.Equal(string.Empty, check.NewFileName);
    }

    [Fact]
    public void Check_NullStem_ReturnsEmpty()
    {
        var check = RenameNamePolicy.Check(null, Current);

        Assert.Equal(RenameNameProblem.Empty, check.Problem);
    }

    [Theory]
    [InlineData("a", "a.jpg")]
    [InlineData("  a  ", "a.jpg")]
    [InlineData("\ta\t", "a.jpg")]
    [InlineData("a.jpg", "a.jpg")]
    [InlineData("A.JPG", "A.jpg")]
    [InlineData("a .jpg", "a.jpg")]
    [InlineData("a.v2", "a.v2.jpg")]
    [InlineData("a.jpg.jpg", "a.jpg.jpg")]
    [InlineData("a b", "a b.jpg")]
    [InlineData("a.png", "a.png.jpg")]
    public void Check_TypedStem_AppendsOriginalExtensionOnce(string typed, string expected)
    {
        var check = RenameNamePolicy.Check(typed, Current);

        Assert.True(check.IsValid);
        Assert.Equal(expected, check.NewFileName);
        Assert.Null(check.InvalidCharacter);
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void Check_ForbiddenCharacter_ReturnsInvalidCharacterWithThatChar(char c)
    {
        var check = RenameNamePolicy.Check("a" + c + "b", Current);

        Assert.Equal(RenameNameProblem.InvalidCharacter, check.Problem);
        Assert.Equal(c, check.InvalidCharacter);
        Assert.Equal(string.Empty, check.NewFileName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(27)]
    [InlineData(31)]
    public void Check_ControlCharacterInside_ReturnsInvalidCharacter(int code)
    {
        var c = (char)code;

        var check = RenameNamePolicy.Check("a" + c + "b", Current);

        Assert.Equal(RenameNameProblem.InvalidCharacter, check.Problem);
        Assert.Equal(c, check.InvalidCharacter);
    }

    [Fact]
    public void Check_SeveralForbiddenCharacters_ReportsFirstOne()
    {
        var check = RenameNamePolicy.Check("ab?c*d", Current);

        Assert.Equal(RenameNameProblem.InvalidCharacter, check.Problem);
        Assert.Equal('?', check.InvalidCharacter);
    }

    [Theory]
    // Built from code units in the body: xUnit's data serialization would replace a lone surrogate in an argument.
    [InlineData("a", 0xD800, "b")]
    [InlineData("a", 0xDC00, "b")]
    [InlineData("a", 0xD800, "")]
    [InlineData("", 0xDE00, "a")]
    public void Check_LoneSurrogate_ReturnsInvalidCharacter(string before, int unit, string after)
    {
        var expected = (char)unit;
        var typed = before + expected + after;
        var check = RenameNamePolicy.Check(typed, Current);

        Assert.Equal(RenameNameProblem.InvalidCharacter, check.Problem);
        Assert.Equal(expected, check.InvalidCharacter);
    }

    [Fact]
    public void Check_ValidSurrogatePair_IsAccepted()
    {
        var check = RenameNamePolicy.Check("a\uD83D\uDE00", Current);

        Assert.True(check.IsValid);
        Assert.Equal("a\uD83D\uDE00.jpg", check.NewFileName);
    }

    [Theory]
    [InlineData("a.")]
    [InlineData("a..")]
    [InlineData("a. ")]
    public void Check_TrailingDot_ReturnsTrailingDotOrSpace(string typed)
    {
        var check = RenameNamePolicy.Check(typed, Current);

        Assert.Equal(RenameNameProblem.TrailingDotOrSpace, check.Problem);
        Assert.Equal(string.Empty, check.NewFileName);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Prn")]
    [InlineData("AUX")]
    [InlineData("nul")]
    [InlineData("COM0")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT0")]
    [InlineData("lpt5")]
    [InlineData("LPT9")]
    [InlineData("COM\u00B9")]
    [InlineData("COM\u00B2")]
    [InlineData("LPT\u00B3")]
    [InlineData("CONIN$")]
    [InlineData("conout$")]
    [InlineData("CON.txt")]
    [InlineData("con .txt")]
    [InlineData("NUL.v2")]
    public void Check_DeviceName_ReturnsReservedDeviceName(string typed)
    {
        var check = RenameNamePolicy.Check(typed, Current);

        Assert.Equal(RenameNameProblem.ReservedDeviceName, check.Problem);
        Assert.Equal(string.Empty, check.NewFileName);
    }

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con", true)]
    [InlineData("CON.txt", true)]
    [InlineData("con .txt", true)]
    [InlineData("PRN.jpg", true)]
    [InlineData("AUX", true)]
    [InlineData("NUL", true)]
    [InlineData("COM0", true)]
    [InlineData("com7.jpg", true)]
    [InlineData("LPT9", true)]
    [InlineData("COM\u00B9", true)]
    [InlineData("COM\u00B2.txt", true)]
    [InlineData("LPT\u00B3", true)]
    [InlineData("CONIN$", true)]
    [InlineData("CONOUT$.jpg", true)]
    [InlineData("COM10", false)]
    [InlineData("COM", false)]
    [InlineData("CONSOLE", false)]
    [InlineData("a.CON", false)]
    [InlineData("xCON", false)]
    [InlineData("LPT10", false)]
    [InlineData("COMA", false)]
    [InlineData("", false)]
    public void IsReservedDeviceName_Table(string fileName, bool expected)
    {
        Assert.Equal(expected, RenameNamePolicy.IsReservedDeviceName(fileName));
    }

    [Theory]
    [InlineData("COM10")]
    [InlineData("CONSOLE")]
    [InlineData("a.CON")]
    public void Check_NotADeviceName_IsAccepted(string typed)
    {
        var check = RenameNamePolicy.Check(typed, Current);

        Assert.True(check.IsValid);
        Assert.Equal(typed + ".jpg", check.NewFileName);
    }

    [Fact]
    public void Check_FileNameOf255_IsAccepted()
    {
        var stem = new string('x', 255 - ".jpg".Length);

        // longPathAware: 255 is the NTFS component limit; the MAX_PATH budget is a different rule.
        var check = RenameNamePolicy.Check(stem, Current, longPathAware: true);

        Assert.True(check.IsValid);
        Assert.Equal(255, check.NewFileName.Length);
    }

    [Fact]
    public void Check_FileNameOf256_ReturnsNameTooLong()
    {
        var stem = new string('x', 256 - ".jpg".Length);

        var check = RenameNamePolicy.Check(stem, Current);

        Assert.Equal(RenameNameProblem.NameTooLong, check.Problem);
        Assert.Equal(string.Empty, check.NewFileName);
    }

    [Fact]
    public void CheckFileName_Table()
    {
        Assert.Equal(RenameNameProblem.None, RenameNamePolicy.CheckFileName("a.jpg"));
        Assert.Equal(RenameNameProblem.Empty, RenameNamePolicy.CheckFileName(null));
        Assert.Equal(RenameNameProblem.Empty, RenameNamePolicy.CheckFileName("  "));
        Assert.Equal(RenameNameProblem.InvalidCharacter, RenameNamePolicy.CheckFileName("a|b.jpg"));
        Assert.Equal(RenameNameProblem.InvalidCharacter, RenameNamePolicy.CheckFileName("a\\b.jpg"));
        Assert.Equal(RenameNameProblem.TrailingDotOrSpace, RenameNamePolicy.CheckFileName("a."));
        Assert.Equal(RenameNameProblem.TrailingDotOrSpace, RenameNamePolicy.CheckFileName("a "));
        Assert.Equal(RenameNameProblem.ReservedDeviceName, RenameNamePolicy.CheckFileName("CON.jpg"));
        Assert.Equal(RenameNameProblem.None, RenameNamePolicy.CheckFileName(new string('x', 255)));
        Assert.Equal(RenameNameProblem.NameTooLong, RenameNamePolicy.CheckFileName(new string('x', 256)));
    }

    private static string LongFolder(int length) => @"C:\" + new string('d', length - 3);

    [Fact]
    public void Check_NewPathReaches260AndIsLonger_ReturnsPathTooLong()
    {
        var current = LongFolder(200) + @"\a.jpg";
        var stem = new string('x', 56); // 56 + 4 = 60; 200 + 1 + 60 = 261

        var check = RenameNamePolicy.Check(stem, current);

        Assert.Equal(RenameNameProblem.PathTooLong, check.Problem);
        Assert.Equal(string.Empty, check.NewFileName);
    }

    [Fact]
    public void Check_NewPathExactly259_IsAccepted()
    {
        var current = LongFolder(200) + @"\a.jpg";
        var stem = new string('x', 54); // 200 + 1 + 58 = 259

        var check = RenameNamePolicy.Check(stem, current);

        Assert.True(check.IsValid);
    }

    [Fact]
    public void Check_NewPathExactly260_ReturnsPathTooLong()
    {
        var current = LongFolder(200) + @"\a.jpg";
        var stem = new string('x', 55); // 200 + 1 + 59 = 260

        var check = RenameNamePolicy.Check(stem, current);

        Assert.Equal(RenameNameProblem.PathTooLong, check.Problem);
    }

    [Fact]
    public void Check_LongCurrentPathAndShorterNewName_IsAccepted()
    {
        var current = LongFolder(200) + @"\" + new string('y', 96) + ".jpg"; // 301 chars
        var stem = new string('x', 80); // 200 + 1 + 84 = 285, >= 260 but shorter than current

        var check = RenameNamePolicy.Check(stem, current);

        Assert.True(check.IsValid);
        Assert.Equal(stem + ".jpg", check.NewFileName);
    }

    [Fact]
    public void Check_LongCurrentPathAndSameLengthName_IsAccepted()
    {
        var current = LongFolder(200) + @"\" + new string('y', 96) + ".jpg";
        var stem = new string('x', 96);

        var check = RenameNamePolicy.Check(stem, current);

        Assert.True(check.IsValid);
    }

    [Fact]
    public void Check_LongPathAware_SkipsPathBudget()
    {
        var current = LongFolder(200) + @"\a.jpg";
        var stem = new string('x', 56);

        var check = RenameNamePolicy.Check(stem, current, longPathAware: true);

        Assert.True(check.IsValid);
        Assert.Equal(stem + ".jpg", check.NewFileName);
    }

    [Fact]
    public void ExceedsPathBudget_Table()
    {
        var current = LongFolder(200) + @"\a.jpg";

        Assert.True(RenameNamePolicy.ExceedsPathBudget(current, new string('x', 60)));
        Assert.False(RenameNamePolicy.ExceedsPathBudget(current, new string('x', 58)));
        Assert.False(RenameNamePolicy.ExceedsPathBudget(current, new string('x', 60), longPathAware: true));
        Assert.False(RenameNamePolicy.ExceedsPathBudget(current, "a.jpg"));
    }

    [Fact]
    public void Check_SameName_IsUnchanged()
    {
        var check = RenameNamePolicy.Check("IMG_0001", Current);

        Assert.True(check.IsValid);
        Assert.True(check.IsUnchanged);
        Assert.False(check.DiffersOnlyByCase);
        Assert.Equal("IMG_0001.jpg", check.NewFileName);
    }

    [Fact]
    public void Check_SameNameWithTypedExtension_IsUnchanged()
    {
        var check = RenameNamePolicy.Check("IMG_0001.jpg", Current);

        Assert.True(check.IsUnchanged);
    }

    [Fact]
    public void Check_OnlyCaseDiffers_IsCaseOnly()
    {
        var check = RenameNamePolicy.Check("img_0001", Current);

        Assert.True(check.IsValid);
        Assert.False(check.IsUnchanged);
        Assert.True(check.DiffersOnlyByCase);
        Assert.Equal("img_0001.jpg", check.NewFileName);
    }

    [Fact]
    public void Check_ExtensionCaseOfCurrentFile_IsKept()
    {
        var check = RenameNamePolicy.Check("a.jpg", @"C:\Photos\IMG.JPG");

        Assert.Equal("a.JPG", check.NewFileName);
    }

    [Fact]
    public void Check_LowerToUpperCaseOnlyOfA_IsCaseOnly()
    {
        var check = RenameNamePolicy.Check("A", @"C:\Photos\a.jpg");

        Assert.Equal("A.jpg", check.NewFileName);
        Assert.True(check.DiffersOnlyByCase);
        Assert.False(check.IsUnchanged);
    }

    [Fact]
    public void Check_DifferentName_IsNeitherUnchangedNorCaseOnly()
    {
        var check = RenameNamePolicy.Check("other", Current);

        Assert.True(check.IsValid);
        Assert.False(check.IsUnchanged);
        Assert.False(check.DiffersOnlyByCase);
    }

    [Fact]
    public void Check_NfdTypedForNfcCurrent_IsDifferentNameAndNotNormalized()
    {
        const string nfc = "\u00E9";
        const string nfd = "e\u0301";

        var check = RenameNamePolicy.Check(nfd, @"C:\Photos\" + nfc + ".jpg");

        Assert.True(check.IsValid);
        Assert.Equal(nfd + ".jpg", check.NewFileName);
        Assert.False(check.IsUnchanged);
        Assert.False(check.DiffersOnlyByCase);
    }

    [Fact]
    public void Check_NfcTypedForNfdCurrent_IsKeptAsTyped()
    {
        const string nfc = "\u00E9";
        const string nfd = "e\u0301";

        var check = RenameNamePolicy.Check(nfc, @"C:\Photos\" + nfd + ".jpg");

        Assert.Equal(nfc + ".jpg", check.NewFileName);
        Assert.False(check.IsUnchanged);
    }

    [Fact]
    public void Check_BlankCurrentPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => RenameNamePolicy.Check("a", " "));
    }
}
