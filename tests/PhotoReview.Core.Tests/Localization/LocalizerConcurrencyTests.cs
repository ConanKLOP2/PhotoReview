using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>The ambient localizer is swapped from the UI thread while worker threads render text.</summary>
[Collection("GlobalState")]
public sealed class LocalizerConcurrencyTests : IDisposable
{
    private readonly Localizer _previous = Localizer.Current;

    public void Dispose() => Localizer.SetCurrent(_previous);

    [Fact(DisplayName = "Reading Localizer.Current and formatting while another thread swaps languages never sees null or mixed text")]
    public async Task SwapWhileRendering_NeverTornOrNull()
    {
        var english = BuiltInCatalog.EnglishLocalizer;
        var pseudo = TranslatorModes.CreatePseudo(english);
        var showKeys = TranslatorModes.CreateShowKeys(english);
        var key = english.Keys.First(k => english.Get(k).Length > 3);
        var allowed = new HashSet<string>([english.Get(key), pseudo.Get(key), showKeys.Get(key)]);
        using var stop = new CancellationTokenSource();
        var bad = new List<string>();
        var swaps = 0;

        var swapper = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                Localizer.SetCurrent((i++ % 3) switch { 0 => english, 1 => pseudo, _ => showKeys });
                Interlocked.Increment(ref swaps);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var n = 0; n < 50_000; n++)
            {
                var text = Localizer.Current.Get(key);
                if (!allowed.Contains(text)) lock (bad) bad.Add(text);
            }
        })).ToArray();

        await Task.WhenAll(readers);
        await stop.CancelAsync();
        await swapper;

        Assert.Empty(bad);
        Assert.True(swaps > 0);
    }

    [Fact(DisplayName = "CurrentChanged fires once per SetCurrent, after the new localizer is visible")]
    public void CurrentChanged_SeesNewLocalizer()
    {
        var pseudo = TranslatorModes.CreatePseudo(BuiltInCatalog.EnglishLocalizer);
        Localizer? seen = null;
        var count = 0;
        void Handler(object? s, EventArgs e) { seen = Localizer.Current; count++; }
        Localizer.CurrentChanged += Handler;
        try
        {
            Localizer.SetCurrent(pseudo);
        }
        finally
        {
            Localizer.CurrentChanged -= Handler;
        }

        Assert.Same(pseudo, seen);
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "Unknown keys render as the key, extra arguments are ignored and missing ones are written back")]
    public void UnknownAndMismatchedArguments_DoNotThrow()
    {
        var english = BuiltInCatalog.EnglishLocalizer;

        Assert.Equal("no.such.key", english.Get("no.such.key"));
        Assert.Equal("no.such.key", english.Format("no.such.key", new LocArg("x", 1)));
        Assert.Equal("no.such.key.other", english.FormatPlural("no.such.key", 3));
        Assert.Equal(string.Empty, english.Get(string.Empty));
    }
}
