namespace PhotoReview.Core.Localization;

/// <summary>
/// Attaches a localized UI sentence to an exception without changing its type (ADR 0006, decision Q-L3, same idea as
/// <c>JournalErrors</c>). Libraries (decoders, IO) keep throwing the exception type callers already filter on and keep
/// the English <see cref="Exception.Message"/> for logs; the UI shows <see cref="Describe"/>, which renders the sentence
/// in the language current at display time. Fallback and control-flow code must test the exception type, never the
/// message text. An exception without an attached sentence (a raw OS error, already localized by Windows) is shown as is.
/// </summary>
public static class UserFacingError
{
    private const string DataKey = "PhotoReview.UserFacingText";

    /// <summary>Attaches <paramref name="text"/> (evaluated when displayed, so it follows the current UI language) and returns <paramref name="exception"/>.</summary>
    public static TException Localized<TException>(TException exception, Func<string> text) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(text);
        exception.Data[DataKey] = text;
        return exception;
    }

    /// <summary>True when <paramref name="exception"/> carries a localized sentence.</summary>
    public static bool IsLocalized(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[DataKey] is Func<string>;
    }

    /// <summary>The localized sentence when attached, otherwise <see cref="Exception.Message"/> (OS text).</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[DataKey] is Func<string> text ? text() : exception.Message;
    }
}
