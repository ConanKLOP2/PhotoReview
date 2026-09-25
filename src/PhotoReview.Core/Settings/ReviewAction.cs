using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class ReviewAction
{
    public string Name { get; set; } = Tr.ActionDefaultName(2);
    public string Shortcut { get; set; } = "Enter";
    public FileOperationType Operation { get; set; } = FileOperationType.Move;
    public string Destination { get; set; } = "Group-2";
    public bool Confirm { get; set; }

    public static List<ReviewAction> Defaults() =>
    [
        new() { Name = Tr.ActionDefaultName(2), Shortcut = "Enter", Operation = FileOperationType.Move, Destination = "Group-2" },
        new() { Name = Tr.ActionDefaultName(3), Shortcut = "F3", Operation = FileOperationType.Move, Destination = "Group-3" },
        new() { Name = Tr.ActionDefaultName(4), Shortcut = "F4", Operation = FileOperationType.Move, Destination = "Group-4" },
        new() { Name = Tr.ActionDefaultBackup, Shortcut = "F5", Operation = FileOperationType.Copy, Destination = "Backup" }
    ];
}
