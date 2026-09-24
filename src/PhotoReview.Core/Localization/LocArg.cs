namespace PhotoReview.Core.Localization;

/// <summary>One named value for a <c>{name}</c> placeholder in a translation template.</summary>
public readonly record struct LocArg(string Name, object? Value);
