namespace PhotoReview.Core.Abstractions;

/// <summary>Whether Explorer's folder right-click command "Browse with PhotoReview" is registered, and for which executable.</summary>
public enum ShellMenuState
{
    /// <summary>Neither the folder nor the folder-background command exists.</summary>
    NotRegistered,

    /// <summary>Both commands exist and launch exactly the executable asked about.</summary>
    Registered,

    /// <summary>A command exists but launches another executable (the app was moved) or only one of the two exists.</summary>
    RegisteredElsewhere,
}

/// <summary>
/// The per-user (HKCU, no administrator rights) Explorer integration: a "Browse with PhotoReview" command on folders and on the
/// empty background inside a folder. The registry sits behind this seam so Settings can be tested with a fake.
/// </summary>
public interface IShellIntegration
{
    /// <summary>Reads the registration and compares it with <paramref name="executablePath"/>.</summary>
    ShellMenuState GetState(string executablePath);

    /// <summary>Creates or overwrites both commands so they launch <paramref name="executablePath"/> (also repairs a moved exe).</summary>
    void Register(string executablePath);

    /// <summary>Removes both commands; nothing else is touched. No-op when not registered.</summary>
    void Unregister();
}

/// <summary>The exact registry values of the integration (pure, so the contract is testable without a registry).</summary>
public static class ShellMenuCommand
{
    /// <summary>The menu text Explorer shows.</summary>
    public const string MenuText = "Browse with PhotoReview";

    /// <summary>Key name below <c>Directory\shell</c> and <c>Directory\Background\shell</c>.</summary>
    public const string VerbKeyName = "PhotoReview";

    /// <summary>Command for a right-click ON a folder: Explorer substitutes the folder for %1.</summary>
    public static string ForFolder(string executablePath) => "\"" + executablePath + "\" \"%1\"";

    /// <summary>Command for a right-click on the empty background INSIDE a folder: Explorer substitutes that folder for %V.</summary>
    public static string ForBackground(string executablePath) => "\"" + executablePath + "\" \"%V\"";

    /// <summary>Menu icon: the executable's own first icon.</summary>
    public static string Icon(string executablePath) => "\"" + executablePath + "\",0";

    /// <summary>A path usable inside the quoted command: rooted, no quote or control character.</summary>
    public static bool IsValidExecutablePath(string? executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath) && Path.IsPathRooted(executablePath)
        && executablePath.IndexOfAny(['"', '\0', '\r', '\n']) < 0;

    /// <summary>Classifies what is stored (null = missing) against the executable the app is running from.</summary>
    public static ShellMenuState Evaluate(string? folderCommand, string? backgroundCommand, string executablePath)
    {
        if (folderCommand is null && backgroundCommand is null) return ShellMenuState.NotRegistered;
        return string.Equals(folderCommand, ForFolder(executablePath), StringComparison.OrdinalIgnoreCase)
            && string.Equals(backgroundCommand, ForBackground(executablePath), StringComparison.OrdinalIgnoreCase)
            ? ShellMenuState.Registered
            : ShellMenuState.RegisteredElsewhere;
    }
}
