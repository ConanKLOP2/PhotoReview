using System.Text;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Settings;

/// <summary>
/// FEAT-EXTERNAL-EDITORS: one declared external image editor (Lightroom, Topaz, DxO, ...). The context menu lists them, one entry
/// each; <see cref="AppSettings.DefaultExternalEditorId"/> names the one the "default editor" shortcut sends the photo to.
/// </summary>
public sealed class ExternalEditor
{
    /// <summary>The argument template used when <see cref="Arguments"/> is blank: the photo path, quoted.</summary>
    public const string DefaultArguments = "\"{file}\"";

    /// <summary>Stable identifier (a GUID in "N" format); lets the default survive a rename or a reorder.</summary>
    public string Id { get; set; } = NewId();

    /// <summary>Menu text. Blank = the executable's file name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full path of the editor's executable.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Command-line template; <c>{file}</c> is replaced by the photo's full path. Default <see cref="DefaultArguments"/>
    /// (<c>"{file}"</c>). See <see cref="ExternalEditors.BuildArguments"/> for the exact rules.
    /// </summary>
    public string Arguments { get; set; } = DefaultArguments;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public ExternalEditor Clone() => new() { Id = Id, Name = Name, Path = Path, Arguments = Arguments };

    /// <summary>The text the menu and the lists show: the name, or the executable's file name when the name is blank.</summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : ExternalEditors.NameFromPath(Path);
}

/// <summary>Pure rules of the declared-editors list: migration, repair, default lookup, launch arguments, RAW pairing.</summary>
public static class ExternalEditors
{
    /// <summary>Hard cap on the list; a hand-edited file with thousands of entries would otherwise build a giant menu.</summary>
    public const int MaxEditors = 32;

    private const int MaxArgumentsLength = 4000;
    private const string FileToken = "{file}";

    /// <summary>"Photoshop.exe" -> "Photoshop".</summary>
    public static string NameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path.Trim());
            return string.IsNullOrWhiteSpace(name) ? path.Trim() : name;
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    /// <summary>Id of the implicit editor made from a legacy <see cref="AppSettings.ExternalEditorPath"/> set without a list.</summary>
    public const string LegacyEditorId = "legacy";

    /// <summary>
    /// The editors the app offers right now: the list, or - when only the legacy single path is set (code or a test that never
    /// went through <see cref="Reconcile"/>) - one implicit editor for it. Never null.
    /// </summary>
    public static IReadOnlyList<ExternalEditor> Effective(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ExternalEditors is { Count: > 0 } list) return list;
        var legacy = (settings.ExternalEditorPath ?? string.Empty).Trim();
        return legacy.Length == 0
            ? []
            : [new ExternalEditor { Id = LegacyEditorId, Name = NameFromPath(legacy), Path = legacy }];
    }

    /// <summary>The default among <see cref="Effective"/>: the id named by <see cref="AppSettings.DefaultExternalEditorId"/>, else the first.</summary>
    public static ExternalEditor? EffectiveDefault(AppSettings settings)
    {
        var editors = Effective(settings);
        if (editors.Count == 0) return null;
        return editors.FirstOrDefault(e => string.Equals(e.Id, settings.DefaultExternalEditorId, StringComparison.Ordinal)) ?? editors[0];
    }

    /// <summary>The default editor of <paramref name="settings"/>: the one <see cref="AppSettings.DefaultExternalEditorId"/> names, else the first.</summary>
    public static ExternalEditor? Default(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = settings.ExternalEditors;
        if (list is null || list.Count == 0) return null;
        return list.FirstOrDefault(e => string.Equals(e.Id, settings.DefaultExternalEditorId, StringComparison.Ordinal)) ?? list[0];
    }

    /// <summary>Editor with this id, or null.</summary>
    public static ExternalEditor? Find(AppSettings settings, string? id) =>
        id is null ? null : settings.ExternalEditors?.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Makes the list and the legacy single path agree. (1) A list that is empty while the legacy <see cref="AppSettings.ExternalEditorPath"/>
    /// is set (an old config, or the WPF window which still edits only that field) gets one entry named after the executable, which
    /// becomes the default. (2) Blank/duplicate entries are dropped, ids made unique, the default id made valid (else the first
    /// editor, else empty). (3) <see cref="AppSettings.ExternalEditorPath"/> mirrors the default editor's path (empty when none) so
    /// the frozen WPF build keeps working. Returns true when an existing list had to be repaired (the silent legacy migration and
    /// mirroring do not count).
    /// </summary>
    public static bool Reconcile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var repaired = false;
        var source = settings.ExternalEditors ??= [];
        var clean = new List<ExternalEditor>(source.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var editor in source)
        {
            if (editor is null || string.IsNullOrWhiteSpace(editor.Path) || clean.Count >= MaxEditors)
            {
                repaired = true;
                continue;
            }
            if (string.IsNullOrWhiteSpace(editor.Id) || !ids.Add(editor.Id))
            {
                // A blank or repeated id: a fresh one (the default id then falls back to the first editor if it pointed here).
                string fresh;
                do fresh = ExternalEditor.NewId(); while (!ids.Add(fresh));
                editor.Id = fresh;
                repaired = true;
            }
            var arguments = string.IsNullOrWhiteSpace(editor.Arguments) ? ExternalEditor.DefaultArguments : editor.Arguments;
            if (arguments.Length > MaxArgumentsLength)
            {
                arguments = ExternalEditor.DefaultArguments;
                repaired = true;
            }
            editor.Path = editor.Path.Trim();
            editor.Name = (editor.Name ?? string.Empty).Trim();
            editor.Arguments = arguments;
            clean.Add(editor);
        }

        var legacy = (settings.ExternalEditorPath ?? string.Empty).Trim();
        if (clean.Count == 0 && legacy.Length > 0)
        {
            var created = new ExternalEditor { Name = NameFromPath(legacy), Path = legacy };
            clean.Add(created);
            settings.DefaultExternalEditorId = created.Id;
        }

        if (!SameReferences(source, clean))
        {
            source.Clear();
            source.AddRange(clean);
        }
        if (clean.Count == 0)
        {
            if (!string.IsNullOrEmpty(settings.DefaultExternalEditorId)) repaired = true;
            settings.DefaultExternalEditorId = string.Empty;
        }
        else if (!clean.Any(e => string.Equals(e.Id, settings.DefaultExternalEditorId, StringComparison.Ordinal)))
        {
            if (!string.IsNullOrEmpty(settings.DefaultExternalEditorId)) repaired = true;
            settings.DefaultExternalEditorId = clean[0].Id;
        }

        settings.ExternalEditorPath = Default(settings)?.Path ?? string.Empty;
        return repaired;
    }

    private static bool SameReferences(List<ExternalEditor> a, List<ExternalEditor> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>
    /// WPF-window hand-off: the frozen WPF settings window edits only <see cref="AppSettings.ExternalEditorPath"/>. When that field
    /// differs from the default editor's path, the default editor takes the new path (a cleared field removes the default editor;
    /// a path with no editor creates one). A no-op for the Win32 dialog, which keeps the field equal to the default's path.
    /// </summary>
    public static void ApplyLegacyPath(AppSettings settings, string? legacyPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = (legacyPath ?? string.Empty).Trim();
        var list = settings.ExternalEditors ??= [];
        var current = Default(settings);
        if (string.Equals(current?.Path ?? string.Empty, path, StringComparison.Ordinal)) return;
        if (path.Length == 0)
        {
            if (current is not null) list.Remove(current);
        }
        else if (current is not null)
        {
            if (string.Equals(current.Name, NameFromPath(current.Path), StringComparison.Ordinal)) current.Name = NameFromPath(path);
            current.Path = path;
        }
        else
        {
            var created = new ExternalEditor { Name = NameFromPath(path), Path = path };
            list.Add(created);
            settings.DefaultExternalEditorId = created.Id;
        }
        Reconcile(settings);
    }

    /// <summary>
    /// The command line for <paramref name="template"/> with <c>{file}</c> replaced by <paramref name="filePath"/>. One left-to-right
    /// pass: the inserted path is never scanned again (a file literally named <c>{file}.jpg</c> cannot expand twice), and every
    /// <c>{file}</c> in the template is replaced. A token inside double quotes receives the bare path (so the default
    /// <c>"{file}"</c> yields one quoted argument); a bare token receives the path quoted. A template without <c>{file}</c> gets
    /// the quoted path appended. Windows file names cannot hold <c>"</c>, but one is backslash-escaped anyway.
    /// </summary>
    public static string BuildArguments(string? template, string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var text = string.IsNullOrWhiteSpace(template) ? ExternalEditor.DefaultArguments : template.Trim();
        var escaped = EscapeForQuotes(filePath);
        var result = new StringBuilder(text.Length + filePath.Length + 8);
        var inQuotes = false;
        var replaced = false;
        for (var i = 0; i < text.Length;)
        {
            if (string.CompareOrdinal(text, i, FileToken, 0, FileToken.Length) == 0)
            {
                result.Append(inQuotes ? escaped : "\"" + escaped + "\"");
                i += FileToken.Length;
                replaced = true;
                continue;
            }
            var c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            result.Append(c);
            i++;
        }
        if (!replaced) result.Append(result.Length > 0 ? " " : string.Empty).Append('"').Append(escaped).Append('"');
        return result.ToString();
    }

    private static string EscapeForQuotes(string path)
    {
        var sb = new StringBuilder(path.Length + 2);
        var backslashes = 0;
        foreach (var c in path)
        {
            if (c == '\\')
            {
                backslashes++;
                sb.Append(c);
                continue;
            }
            if (c == '"') sb.Append('\\', backslashes + 1).Append('"'); // 2n+1 backslashes then the quote
            else sb.Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes); // trailing backslashes are doubled so they cannot swallow the closing quote
        return sb.ToString();
    }

    /// <summary>
    /// The file an editor should receive. With <paramref name="sendRawOriginal"/> and a photo that has a RAW companion, the RAW
    /// (the capture group's RAW when the catalog paired them, else a same-folder, same-name file with a known RAW extension);
    /// otherwise <paramref name="path"/> itself.
    /// </summary>
    public static string ResolveFileToSend(string path, string? pairedRawPath, bool sendRawOriginal, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!sendRawOriginal) return path;
        if (PhotoReview.Core.Catalog.ImageFileTypes.RawExtensions.Contains(System.IO.Path.GetExtension(path))) return path;
        if (!string.IsNullOrWhiteSpace(pairedRawPath) && fileExists(pairedRawPath)) return pairedRawPath;
        var folder = System.IO.Path.GetDirectoryName(path);
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem)) return path;
        foreach (var extension in PhotoReview.Core.Catalog.ImageFileTypes.RawExtensions.Order(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = System.IO.Path.Combine(folder, stem + extension);
            if (fileExists(candidate)) return candidate;
        }
        return path;
    }
}
