using System.Text;
using PhotoReview.Core.Instance;

namespace PhotoReview.Core.Tests.Instance;

/// <summary>Mutation-testing gaps in <see cref="ForwardedPathProtocol"/>: every limit at Max and Max+1, framing, and the device-namespace rejection.</summary>
public sealed class ForwardedPathProtocolLimitTests
{
    private static readonly Func<string, bool> Exists = _ => true;

    private static string PathOfLength(int length) => @"C:\" + new string('a', length - 3);

    private static byte[] Raw(params string[] paths)
    {
        var sb = new StringBuilder(ForwardedPathProtocol.Header).Append('\n');
        foreach (var p in paths) sb.Append(p).Append('\n');
        sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // header (18) + '\n' = 19, each path adds length + 1, the terminator adds 1: 19 + (l1 + 1) + (l2 + 1) + 1
    private static string[] TwoPathsFillingTheMessageTo(int totalBytes)
    {
        var rest = totalBytes - 19 - 1 - 2; // path characters left for both paths
        var first = ForwardedPathProtocol.MaxPathChars;
        return [PathOfLength(first), PathOfLength(rest - first)];
    }

    [Fact]
    public void Encode_ExactlyMaxPaths_IsAcceptedAndOneMoreThrows()
    {
        var sixteen = Enumerable.Range(0, ForwardedPathProtocol.MaxPaths).Select(i => $@"C:\p{i}").ToList();

        var bytes = ForwardedPathProtocol.Encode(sixteen);

        Assert.True(ForwardedPathProtocol.TryDecode(bytes, Exists, out var decoded));
        Assert.Equal(sixteen, decoded);
        sixteen.Add(@"C:\extra");
        Assert.Throws<ArgumentException>(() => ForwardedPathProtocol.Encode(sixteen));
    }

    [Fact]
    public void Encode_MessageOfExactlyMaxBytes_IsAcceptedAndOneByteMoreThrows()
    {
        var atLimit = TwoPathsFillingTheMessageTo(ForwardedPathProtocol.MaxMessageBytes);
        var bytes = ForwardedPathProtocol.Encode(atLimit);
        Assert.Equal(ForwardedPathProtocol.MaxMessageBytes, bytes.Length);

        var overLimit = TwoPathsFillingTheMessageTo(ForwardedPathProtocol.MaxMessageBytes + 1);
        Assert.Throws<ArgumentException>(() => ForwardedPathProtocol.Encode(overLimit));
    }

    [Fact]
    public void TryDecode_MessageOfExactlyMaxBytes_IsAccepted()
    {
        var paths = TwoPathsFillingTheMessageTo(ForwardedPathProtocol.MaxMessageBytes);
        var message = Raw(paths);
        Assert.Equal(ForwardedPathProtocol.MaxMessageBytes, message.Length);

        var ok = ForwardedPathProtocol.TryDecode(message, Exists, out var decoded);

        Assert.True(ok);
        Assert.Equal(paths, decoded);
    }

    [Fact]
    public void TryDecode_CompleteMessageOfMaxBytesPlusOne_IsRejected()
    {
        var message = Raw(TwoPathsFillingTheMessageTo(ForwardedPathProtocol.MaxMessageBytes + 1));
        Assert.Equal(ForwardedPathProtocol.MaxMessageBytes + 1, message.Length);

        Assert.False(ForwardedPathProtocol.TryDecode(message, Exists, out var decoded));
        Assert.Empty(decoded);
    }

    [Fact]
    public void TryDecode_ExactlyMaxPaths_IsAcceptedAndMaxPlusOneRejected()
    {
        var sixteen = Enumerable.Range(0, ForwardedPathProtocol.MaxPaths).Select(i => $@"C:\p{i}").ToArray();
        var seventeen = sixteen.Append(@"C:\extra").ToArray();

        Assert.True(ForwardedPathProtocol.TryDecode(Raw(sixteen), Exists, out var decoded));
        Assert.Equal(sixteen, decoded);
        Assert.False(ForwardedPathProtocol.TryDecode(Raw(seventeen), Exists, out _));
    }

    [Fact]
    public void TryDecode_FifteenPaths_IsAccepted()
    {
        var fifteen = Enumerable.Range(0, ForwardedPathProtocol.MaxPaths - 1).Select(i => $@"C:\p{i}").ToArray();

        Assert.True(ForwardedPathProtocol.TryDecode(Raw(fifteen), Exists, out var decoded));
        Assert.Equal(fifteen, decoded);
    }

    [Fact]
    public void Encode_PathOfExactlyMaxPathChars_IsAcceptedAndOneCharMoreThrows()
    {
        var atLimit = PathOfLength(ForwardedPathProtocol.MaxPathChars);
        Assert.Equal(ForwardedPathProtocol.MaxPathChars, atLimit.Length);

        var bytes = ForwardedPathProtocol.Encode([atLimit]);

        Assert.True(ForwardedPathProtocol.TryDecode(bytes, Exists, out var decoded));
        Assert.Equal([atLimit], decoded);
        Assert.Throws<ArgumentException>(() => ForwardedPathProtocol.Encode([PathOfLength(ForwardedPathProtocol.MaxPathChars + 1)]));
    }

    [Fact]
    public void TryDecode_PathOfMaxPathCharsPlusOne_IsRejected()
    {
        var message = Raw(PathOfLength(ForwardedPathProtocol.MaxPathChars + 1));

        Assert.False(ForwardedPathProtocol.TryDecode(message, Exists, out _));
    }

    [Fact]
    public void TryDecode_EmptyPathLine_IsRejected()
    {
        Assert.False(ForwardedPathProtocol.TryDecode(Raw(@"C:\a", ""), Exists, out _));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("\n", false)]
    [InlineData("a", false)]
    [InlineData("ab", false)]
    [InlineData("abc", false)]
    [InlineData("x\n", false)]
    [InlineData("ab\n", false)]
    [InlineData("\nx", false)]
    [InlineData("\n\n", true)]
    [InlineData("x\n\n", true)]
    [InlineData("PHOTOREVIEW-OPEN 1\n\n", true)]
    public void IsComplete_OnlyAMessageEndingWithAnEmptyLineIsComplete(string text, bool expected)
    {
        Assert.Equal(expected, ForwardedPathProtocol.IsComplete(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void TryDecode_EmptyMessage_IsRejectedWithoutThrowing()
    {
        Assert.False(ForwardedPathProtocol.TryDecode([], Exists, out var paths));
        Assert.Empty(paths);
    }

    [Theory]
    [InlineData("PHOTOREVIEW-OPEN 1\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\nC:\\a.jpg")]
    [InlineData("PHOTOREVIEW-OPEN 1\nC:\\a.jpg\n")]
    [InlineData("PHOTOREVIEW-OPEN 1\nC:\\a.jpg\nx")]
    public void TryDecode_UnterminatedMessage_IsRejectedEvenWhenEveryPathExists(string text)
    {
        Assert.False(ForwardedPathProtocol.TryDecode(Encoding.UTF8.GetBytes(text), Exists, out var paths));
        Assert.Empty(paths);
    }

    [Fact]
    public void TryDecode_HeaderOnly_IsAnEmptyValidRequest()
    {
        Assert.True(ForwardedPathProtocol.TryDecode(Raw(), Exists, out var paths));
        Assert.Empty(paths);
    }

    [Theory]
    [InlineData(@"\\?\C:\a.jpg")]
    [InlineData(@"\\.\C:\a.jpg")]
    [InlineData("//?/C:/a.jpg")]
    [InlineData("//./C:/a.jpg")]
    [InlineData(@"\??\C:\a.jpg")]
    [InlineData(@"/??/C:/a.jpg")]
    [InlineData(@"\\?")]
    [InlineData(@"\\.")]
    [InlineData("//?")]
    [InlineData("//.")]
    [InlineData(@"\??")]
    public void Path_DeviceOrExtendedNamespace_IsRejectedByEncodeAndDecode(string path)
    {
        Assert.Throws<ArgumentException>(() => ForwardedPathProtocol.Encode([path]));
        Assert.False(ForwardedPathProtocol.TryDecode(Raw(path), Exists, out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("C:")]
    [InlineData(@"\")]
    public void Path_OneOrTwoCharacters_IsRejectedCleanly(string path)
    {
        Assert.Throws<ArgumentException>(() => ForwardedPathProtocol.Encode([path]));
        Assert.False(ForwardedPathProtocol.TryDecode(Raw(path), Exists, out _));
    }

    [Theory]
    [InlineData(@"\\")]
    [InlineData("//")]
    public void Path_TwoSeparators_NeverCrashesTheShapeCheck(string path)
    {
        // .NET calls "\\" fully qualified, so acceptance is decided by the existence check; what matters is a clean verdict.
        Assert.IsNotType<IndexOutOfRangeException>(Record.Exception(() => ForwardedPathProtocol.Encode([path])));
        Assert.IsNotType<IndexOutOfRangeException>(Record.Exception(() => ForwardedPathProtocol.TryDecode(Raw(path), Exists, out _)));
    }

    [Theory]
    [InlineData(@"C:\a.jpg")]
    [InlineData(@"C:\a\b.jpg")]
    [InlineData(@"\\server\share\a.jpg")]
    [InlineData("//server/share/a.jpg")]
    [InlineData(@"D:\x?.jpg.bak")]
    public void Path_OrdinaryFullyQualifiedPath_IsAccepted(string path)
    {
        var bytes = ForwardedPathProtocol.Encode([path]);

        Assert.True(ForwardedPathProtocol.TryDecode(bytes, Exists, out var decoded));
        Assert.Equal([path], decoded);
    }

    [Fact]
    public void TryDecode_PathThatDoesNotExist_IsRejected()
    {
        var bytes = ForwardedPathProtocol.Encode([@"C:\a.jpg"]);

        Assert.False(ForwardedPathProtocol.TryDecode(bytes, _ => false, out _));
    }
}