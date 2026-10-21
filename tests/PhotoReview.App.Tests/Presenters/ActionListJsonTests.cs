using System.Text.Json;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>
/// An unrecognised <c>Operation</c> in a user-typed or imported action list is an error, not a silent Move
/// (the lenient enum converter alone would turn it into Move; config.json loading keeps that on purpose).
/// </summary>
public sealed class ActionListJsonTests
{
    private static string One(string operation) =>
        $$"""[{"Name":"A","Shortcut":"F6","Operation":{{operation}}}]""";

    [Theory(DisplayName = "Deserialize rejects an unknown operation name or an undefined number")]
    [InlineData("\"Teleport\"")]
    [InlineData("99")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void Deserialize_UnknownOperation_Throws(string operation)
    {
        Assert.Throws<JsonException>(() => ActionListJson.Deserialize(One(operation)));
        Assert.Throws<JsonException>(() => ActionListJson.DeserializeOrNull(One(operation)));
    }

    [Theory(DisplayName = "Deserialize keeps accepting names, aliases, case variants and defined numbers")]
    [InlineData("\"Move\"", FileOperationType.Move)]
    [InlineData("\"copy\"", FileOperationType.Copy)]
    [InlineData("\"Delete\"", FileOperationType.Recycle)]
    [InlineData("\"Recycle\"", FileOperationType.Recycle)]
    [InlineData("1", FileOperationType.Copy)]
    [InlineData("\"2\"", FileOperationType.Recycle)]
    public void Deserialize_KnownOperation_Parses(string operation, FileOperationType expected)
    {
        var action = Assert.Single(ActionListJson.Deserialize(One(operation)));
        Assert.Equal(expected, action.Operation);
    }

    [Fact(DisplayName = "A missing Operation still means the default and a null list is still empty")]
    public void Deserialize_MissingOperation_UsesDefault()
    {
        var action = Assert.Single(ActionListJson.Deserialize("""[{"Name":"A","Shortcut":"F6"}]"""));
        Assert.Equal(default, action.Operation);
        Assert.Empty(ActionListJson.Deserialize("null"));
        Assert.Null(ActionListJson.DeserializeOrNull("null"));
    }
}
