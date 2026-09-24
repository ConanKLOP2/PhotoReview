using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Trạng thái của một thao tác trong nhật ký journal.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<JournalState>))]
public enum JournalState
{
    Prepared = 0,
    Committed = 1,
    Failed = 2,
    /// <summary>Người dùng đã xoá mục khỏi danh sách Recovery; không có thao tác file nào.</summary>
    Dismissed = 3
}
