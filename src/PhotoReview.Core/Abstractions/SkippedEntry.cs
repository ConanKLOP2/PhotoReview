namespace PhotoReview.Core.Abstractions;

/// <summary>
/// A file or directory that a folder scan could not read (access denied, locked, I/O error) and
/// skipped instead of failing the whole load (ADR 0007 section 3). <see cref="Reason"/> is a
/// short technical reason (exception type / message), not localized UI text.
/// </summary>
public sealed record SkippedEntry(string Path, string Reason);
