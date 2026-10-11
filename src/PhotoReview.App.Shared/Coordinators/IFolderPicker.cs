namespace PhotoReview.App.Coordinators;

/// <summary>
/// "Move to… / Copy to…": the modal folder picker, owned by the main window. Abstracted so the controller flow
/// (picked, cancelled, state changed while the dialog pumped messages) is testable without a real dialog.
/// </summary>
public interface IFolderPicker
{
    /// <summary>
    /// Shows the picker starting at <paramref name="initialFolder"/> (ignored when null or missing) and returns the chosen
    /// folder, or null when the user cancelled. Runs a nested message loop: callers must re-check their state afterwards.
    /// </summary>
    string? PickFolder(string title, string? initialFolder);
}
