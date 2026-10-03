
namespace PhotoReview.TestSupport;

/// <summary>Thrown in strict mode when the RAW sample corpus (or native LibRaw) a test needs is unavailable.</summary>
public sealed class RawCorpusRequiredException(string message) : InvalidOperationException(message);

/// <summary>
/// Access to the gitignored real-camera RAW corpus (tests/Fixtures/raw-corpus, fetched by tools/fetch-raw-samples.ps1).
/// Default behaviour: a missing corpus, sample or native LibRaw makes the caller skip by early return (returns false / null).
/// With PHOTOREVIEW_RAW_CORPUS_STRICT=1 (or "true") the same situations throw <see cref="RawCorpusRequiredException"/>
/// so a CI job that is supposed to have the corpus cannot silently pass.
/// </summary>
public static class RawCorpus
{
    public const string StrictEnvironmentVariable = "PHOTOREVIEW_RAW_CORPUS_STRICT";

    public static string Directory { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    public static bool IsStrict => ParseStrict(Environment.GetEnvironmentVariable(StrictEnvironmentVariable));

    public static bool ParseStrict(string? value) =>
        value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the corpus directory exists. Missing: false (caller returns), or throws in strict mode.</summary>
    public static bool RequireDirectory() => RequireDirectory(Directory, IsStrict);

    public static bool RequireDirectory(string directory, bool strict)
    {
        if (System.IO.Directory.Exists(directory)) return true;
        if (strict) throw new RawCorpusRequiredException($"RAW corpus is required ({StrictEnvironmentVariable}) but the directory is missing: {directory}");
        return false;
    }

    /// <summary>Full path of a corpus sample, or null (caller returns) when absent; throws in strict mode.</summary>
    public static string? TryGetFile(string fileName) => TryGetFile(Directory, fileName, IsStrict);

    public static string? TryGetFile(string directory, string fileName, bool strict)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path)) return path;
        if (strict) throw new RawCorpusRequiredException($"RAW corpus sample is required ({StrictEnvironmentVariable}) but missing: {path}");
        return null;
    }

    /// <summary>First corpus file matching the search pattern whose name contains the given text; null / throws as <see cref="TryGetFile(string)"/>.</summary>
    public static string? TryGetFirst(string searchPattern, string nameContains) => TryGetFirst(Directory, searchPattern, nameContains, IsStrict);

    public static string? TryGetFirst(string directory, string searchPattern, string nameContains, bool strict)
    {
        var found = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(directory, searchPattern)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => Path.GetFileName(f).Contains(nameContains, StringComparison.Ordinal))
            : null;
        if (found is not null) return found;
        if (strict) throw new RawCorpusRequiredException($"RAW corpus sample matching '{searchPattern}' containing '{nameContains}' is required ({StrictEnvironmentVariable}) but missing in: {directory}");
        return null;
    }

    /// <summary>Call with the result of the LibRaw availability probe. False (caller returns) when unavailable; throws in strict mode.</summary>
    public static bool RequireNative(bool available, string? reason = null) => RequireNative(available, reason, IsStrict);

    public static bool RequireNative(bool available, string? reason, bool strict)
    {
        if (available) return true;
        if (strict) throw new RawCorpusRequiredException($"Native LibRaw is required ({StrictEnvironmentVariable}) but unavailable: {reason}");
        return false;
    }

    /// <summary>Corpus files with one of the extensions, ordered. Missing directory: empty, or throws in strict mode.</summary>
    public static IReadOnlyList<string> Files(params string[] extensions)
    {
        if (!RequireDirectory()) return [];
        var files = System.IO.Directory.GetFiles(Directory)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RequireNonEmpty(files.Length, string.Join("/", extensions));
        return files;
    }

    /// <summary>True when <paramref name="count"/> is positive. Zero: false (caller returns), or throws in strict mode.</summary>
    public static bool RequireNonEmpty(int count, string what) => RequireNonEmpty(count, what, IsStrict);

    public static bool RequireNonEmpty(int count, string what, bool strict)
    {
        if (count > 0) return true;
        if (strict) throw new RawCorpusRequiredException($"RAW corpus is required ({StrictEnvironmentVariable}) but contains no {what} samples.");
        return false;
    }
}
