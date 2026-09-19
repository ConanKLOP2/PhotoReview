using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Loại thao tác file được thực hiện trong quá trình review.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<FileOperationType>))]
public enum FileOperationType
{
    Move = 0,
    Copy = 1,

    [JsonAlias("Delete")]
    Recycle = 2
}
