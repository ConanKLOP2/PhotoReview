using Microsoft.Win32;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// <see cref="IShellIntegration"/> over <c>HKEY_CURRENT_USER\Software\Classes</c> (per user, no administrator rights). Writes only
/// <c>Directory\shell\PhotoReview</c> and <c>Directory\Background\shell\PhotoReview</c> below the classes root; the root is a
/// constructor argument so tests use a private key instead of the user's real one.
/// </summary>
public sealed class WindowsShellIntegration : IShellIntegration
{
    /// <summary>The real per-user classes root, relative to HKCU.</summary>
    public const string DefaultClassesKey = @"Software\Classes";

    private const string FolderVerbPath = @"Directory\shell\" + ShellMenuCommand.VerbKeyName;
    private const string BackgroundVerbPath = @"Directory\Background\shell\" + ShellMenuCommand.VerbKeyName;

    private readonly string _classesKey;

    public WindowsShellIntegration(string classesKey = DefaultClassesKey)
    {
        if (string.IsNullOrWhiteSpace(classesKey)) throw new ArgumentException("A registry key path is required.", nameof(classesKey));
        _classesKey = classesKey.TrimEnd('\\');
    }

    public ShellMenuState GetState(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return ShellMenuCommand.Evaluate(ReadCommand(FolderVerbPath), ReadCommand(BackgroundVerbPath), executablePath);
    }

    public void Register(string executablePath)
    {
        if (!ShellMenuCommand.IsValidExecutablePath(executablePath))
            throw new ArgumentException("The executable path must be rooted and must not contain a quote.", nameof(executablePath));
        try
        {
            WriteVerb(FolderVerbPath, executablePath, ShellMenuCommand.ForFolder(executablePath));
            WriteVerb(BackgroundVerbPath, executablePath, ShellMenuCommand.ForBackground(executablePath));
        }
        catch
        {
            // Never leave half of the pair behind: a retry then starts from a clean state.
            TryUnregister();
            throw;
        }
    }

    public void Unregister()
    {
        using var root = Registry.CurrentUser.OpenSubKey(_classesKey, writable: true);
        if (root is null) return;
        root.DeleteSubKeyTree(FolderVerbPath, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(BackgroundVerbPath, throwOnMissingSubKey: false);
    }

    private void TryUnregister()
    {
        try { Unregister(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* best effort */ }
    }

    private string? ReadCommand(string verbPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_classesKey + "\\" + verbPath + "\\command");
        return key?.GetValue(null) as string;
    }

    private void WriteVerb(string verbPath, string executablePath, string command)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(_classesKey + "\\" + verbPath, writable: true);
        verb.SetValue(null, ShellMenuCommand.MenuText, RegistryValueKind.String);
        verb.SetValue("Icon", ShellMenuCommand.Icon(executablePath), RegistryValueKind.String);
        using var commandKey = verb.CreateSubKey("command", writable: true);
        commandKey.SetValue(null, command, RegistryValueKind.String);
    }
}
