using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Localization;

/// <summary>
/// "Export strings to translate" (I18N L08): builds <c>&lt;code&gt;.todo.json</c> for a translator. It holds the
/// language's <c>_meta</c>, every English key: untranslated ones first (value = English text, also listed in
/// <c>_missing</c>), then the translated ones with their current text; and a <c>_notes</c> object with the translator
/// context from <c>en.notes.json</c>. The dotted file name
/// keeps <see cref="LanguageLoader"/> from loading the half-done file as a catalog.
/// </summary>
public static class TranslationExport
{
    public const string FileSuffix = ".todo.json";

    /// <summary>Appended to the previous export when Export replaces it (never matches the loader's <c>*.json</c> scan).</summary>
    public const string BackupSuffix = ".bak";

    public const string NotesFileName = "en.notes.json";

    /// <summary>Array of the keys whose value is still English (ignored by the loader, like every <c>_</c> key).</summary>
    public const string MissingKey = "_missing";

    /// <summary>Guidance written into every export (catalog key <c>export.help</c>, in the current UI language).</summary>
    public static string HelpText => Tr.ExportHelp;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // Keep non-ASCII text readable for translators (the file is only read by people and PhotoReview).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the export for <paramref name="code"/> (already resolved, never <c>auto</c>) into
    /// <paramref name="userDir"/> and returns the file path. Existing translations are read from both folders.
    /// </summary>
    public static string Export(string code, string shippedDir, string userDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(shippedDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDir);
        if (!LanguageCatalog.IsValidCode(code)) throw new ArgumentException($"Invalid language code '{code}'.", nameof(code));

        var catalogs = ReadCatalogs(shippedDir, code).Concat(ReadCatalogs(userDir, code)).ToList();
        var notes = ReadNotes(Path.Combine(shippedDir, NotesFileName));
        var json = BuildJson(BuiltInCatalog.English, code, catalogs, notes);

        Directory.CreateDirectory(userDir);
        var path = Path.Combine(userDir, code.ToLowerInvariant() + FileSuffix);
        // The export is what a translator edits by hand: a second Export must not silently destroy that work.
        // Each export that would replace different content keeps its own backup (.bak, .bak2, ...): a second Export
        // must not overwrite the only copy of the first with the generated file. An identical file needs none.
        if (File.Exists(path) && !string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
            File.Copy(path, FreeBackupPath(path), overwrite: false);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string FreeBackupPath(string path)
    {
        var candidate = path + BackupSuffix;
        for (var n = 2; File.Exists(candidate); n++) candidate = path + BackupSuffix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>
    /// The export text. <paramref name="translations"/> are the catalogs of <paramref name="code"/> found so far
    /// (shipped first, user last; the last one also supplies <c>_meta</c>). English is complete by definition.
    /// </summary>
    public static string BuildJson(LanguageCatalog english, string code, IReadOnlyList<LanguageCatalog> translations,
        IReadOnlyDictionary<string, string> notes)
    {
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(notes);

        var isEnglish = string.Equals(code, Localizer.EnglishCode, StringComparison.OrdinalIgnoreCase);
        var meta = translations.Count > 0 ? translations[^1] : isEnglish ? english : null;
        var plural = meta?.Plural ?? PluralRule.OneOther;

        // Current text per English key: English itself is complete; otherwise shipped then user, the last file wins
        // (the loader's order). Keys English does not know are dropped: the loader would ignore them with a warning.
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<LanguageCatalog> sources = isEnglish ? translations.Prepend(english) : translations;
        foreach (var catalog in sources)
        {
            foreach (var (key, text) in catalog.Entries)
            {
                if (english.Entries.ContainsKey(key)) current[key] = text;
            }
        }

        // A language without plural forms never shows key.one, so an untranslated one is not work to do.
        var missing = english.Entries
            .Where(p => !current.ContainsKey(p.Key) && !(plural == PluralRule.None && IsUnusedOneForm(p.Key, english)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var done = current.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        // Untranslated keys first (English text), then every translated key with its current text, so the translator
        // sees the work to do at the top and can still correct any existing translation.
        var entries = missing.Concat(done).ToList();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(LanguageCatalog.MetaKey);
            writer.WriteString("code", meta?.Code ?? code.ToLowerInvariant());
            writer.WriteString("name", meta?.Name ?? code);
            writer.WriteString("nativeName", meta?.NativeName ?? code);
            writer.WriteString("plural", plural == PluralRule.None ? "none" : "one-other");
            writer.WriteStartArray("authors");
            foreach (var author in meta?.Authors ?? []) writer.WriteStringValue(author);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("_help", HelpText);
            writer.WriteStartArray(MissingKey);
            foreach (var (key, _) in missing) writer.WriteStringValue(key);
            writer.WriteEndArray();
            writer.WriteStartObject("_notes");
            foreach (var (key, _) in entries)
            {
                if (notes.TryGetValue(key, out var note)) writer.WriteString(key, note);
            }
            writer.WriteEndObject();
            foreach (var (key, text) in entries) writer.WriteString(key, text);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static bool IsUnusedOneForm(string key, LanguageCatalog english) =>
        key.EndsWith(".one", StringComparison.Ordinal)
        && english.Entries.ContainsKey(string.Concat(key.AsSpan(0, key.Length - ".one".Length), ".other"));

    /// <summary>Reads <c>en.notes.json</c> (key → translator note); a missing or broken file gives no notes.</summary>
    public static IReadOnlyDictionary<string, string> ReadNotes(string path)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > LanguageCatalog.MaxFileBytes) return notes;
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return notes;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!property.Name.StartsWith('_') && property.Value.ValueKind == JsonValueKind.String)
                    notes[property.Name] = property.Value.GetString()!;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Notes are a translator aid only; the export works without them.
        }
        return notes;
    }

    private static List<LanguageCatalog> ReadCatalogs(string dir, string code)
    {
        var found = new List<LanguageCatalog>();
        if (!Directory.Exists(dir)) return found;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            // Same rule as LanguageLoader: dotted names (en.notes.json, *.todo.json) are not catalogs.
            if (Path.GetFileNameWithoutExtension(file).Contains('.', StringComparison.Ordinal)) continue;
            try
            {
                if (new FileInfo(file).Length > LanguageCatalog.MaxFileBytes) continue;
                if (LanguageCatalog.TryParse(File.ReadAllText(file), file, out var catalog, new List<string>())
                    && string.Equals(catalog.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(catalog);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable files are skipped, exactly as the loader skips them.
            }
        }
        return found;
    }
}
