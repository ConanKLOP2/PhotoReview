namespace PhotoReview.Core.Model;

/// <summary>
/// Gán một hoặc nhiều tên alias (chuỗi tương đương) cho một giá trị enum khi đọc từ JSON.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class JsonAliasAttribute(params string[] aliases) : Attribute
{
    public string[] Aliases { get; } = aliases ?? [];
}
