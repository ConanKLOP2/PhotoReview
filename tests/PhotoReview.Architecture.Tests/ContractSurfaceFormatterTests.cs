namespace PhotoReview.Architecture.Tests;

/// <summary>
/// Ghim cách <see cref="ContractSurface"/> viết chữ ký: nếu formatter bỏ sót nullability, tên phần tử tuple, giá trị mặc
/// định, in/out, static abstract hay init thì một thay đổi hợp đồng loại đó sẽ lọt qua <see cref="ContractSurfaceTests"/>.
/// </summary>
public sealed class ContractSurfaceFormatterTests
{
#pragma warning disable CA1069 // Bí danh cố ý: ghim thứ tự viết các bí danh enum (như KeyId Return = Enter).
    public enum SampleKind : byte { First = 1, Alias = 1, Second = 2 }
#pragma warning restore CA1069

    public interface ISample<TSelf> where TSelf : ISample<TSelf>
    {
        static abstract TSelf Create(int value);

        event EventHandler<EventArgs>? Changed;

        (double Horizontal, double Vertical) Offset { get; }

        (int Left, int Top)? Bounds { get; }

        string? Find(in SampleRecord key, out int index, CancellationToken cancellationToken = default);
    }

    public sealed record SampleRecord(string Name, IReadOnlyList<string?> Items, SampleKind Kind = SampleKind.Second, float Opacity = 1f)
    {
        public const string Marker = "m";

        public int Count { get; init; }

        internal int Hidden { get; set; }

        private int Secret { get; set; }

        public static SampleRecord? Parse(string? text) => throw new NotImplementedException();
    }

    [Fact(DisplayName = "L-CONTRACT: the surface formatter renders kinds, nullability, tuple names, defaults and modifiers")]
    [Trait("Category", "Architecture")]
    public void Formatter_RendersSignatureDetails()
    {
        const string ns = "PhotoReview.Architecture.Tests.ContractSurfaceFormatterTests";

        Assert.Equal(
        [
            $"[X] public enum {ns}+SampleKind : byte  (assembly PhotoReview.Architecture.Tests)",
            "  Alias = 1",
            "  First = 1",
            "  Second = 2",
        ], ContractSurface.RenderType("X", typeof(SampleKind)));

        Assert.Equal(
        [
            $"[X] public interface {ns}+ISample`1  (assembly PhotoReview.Architecture.Tests)",
            "  event public System.EventHandler<System.EventArgs>? Changed",
            $"  method public static abstract TSelf Create(int value)",
            $"  method public string? Find(in {ns}.SampleRecord key, out int index, System.Threading.CancellationToken cancellationToken = default)",
            "  property public (double Horizontal, double Vertical) Offset { get; }",
            "  property public (int Left, int Top)? Bounds { get; }",
        ], ContractSurface.RenderType("X", typeof(ISample<>)));

        Assert.Equal(
        [
            $"[X] public sealed record {ns}+SampleRecord : System.IEquatable<{ns}.SampleRecord>  (assembly PhotoReview.Architecture.Tests)",
            $"  ctor public (string Name, System.Collections.Generic.IReadOnlyList<string?> Items, {ns}.SampleKind Kind = SampleKind.Second, float Opacity = 1f)",
            "  field public const string Marker = \"m\"",
            $"  method public static {ns}.SampleRecord? Parse(string? text)",
            "  property internal int Hidden { get; set; }",
            $"  property public {ns}.SampleKind Kind {{ get; init; }}",
            "  property public System.Collections.Generic.IReadOnlyList<string?> Items { get; init; }",
            "  property public float Opacity { get; init; }",
            "  property public int Count { get; init; }",
            "  property public string Name { get; init; }",
        ], ContractSurface.RenderType("X", typeof(SampleRecord)));
    }
}
