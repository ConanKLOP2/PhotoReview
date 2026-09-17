using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class ReviewAction
{
    public string Name { get; set; } = "Loại 2";
    public string Shortcut { get; set; } = "Enter";
    public FileOperationType Operation { get; set; } = FileOperationType.Move;
    public string Destination { get; set; } = "Loai-2";
    public bool Confirm { get; set; }

    public static List<ReviewAction> Defaults() =>
    [
        new() { Name = "Loại 2", Shortcut = "Enter", Operation = FileOperationType.Move, Destination = "Loai-2" },
        new() { Name = "Loại 3", Shortcut = "F3", Operation = FileOperationType.Move, Destination = "Loai-3" },
        new() { Name = "Loại 4", Shortcut = "F4", Operation = FileOperationType.Move, Destination = "Loai-4" },
        new() { Name = "Backup", Shortcut = "F5", Operation = FileOperationType.Copy, Destination = "Backup" }
    ];
}
