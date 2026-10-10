using System.Text.Json.Serialization;

namespace PhotoReview.App.Windowing;

// Lược đồ JSON của window-placement.json (N-3: định dạng file KHÔNG đổi). Tên trường và thứ tự khai báo ở đây là định
// dạng trên đĩa: Length, Flags, ShowCommand, MinPosition{X,Y}, MaxPosition{X,Y}, NormalPosition{Left,Top,Right,Bottom}.
// Giống hệt lớp WindowPlacement (WPF) mà bản cũ tuần tự hoá bằng reflection; ở đây source-gen (AOT-ready).

internal struct PlacementPointJson
{
    public int X;
    public int Y;
}

internal struct PlacementRectJson
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal sealed class WindowPlacementJson
{
    public int Length;
    public int Flags;
    public int ShowCommand = 3; // SW_SHOWMAXIMIZED: mặc định của bản cũ khi file thiếu trường
    public PlacementPointJson MinPosition;
    public PlacementPointJson MaxPosition;
    public PlacementRectJson NormalPosition;
}

[JsonSourceGenerationOptions(IncludeFields = true, WriteIndented = true)]
[JsonSerializable(typeof(WindowPlacementJson))]
internal sealed partial class WindowPlacementJsonContext : JsonSerializerContext;
