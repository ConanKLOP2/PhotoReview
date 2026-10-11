using System.Collections.Concurrent;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Default implementation of <see cref="IImageDecoderFactory"/>.
/// Resolves the requested decoder backend and wraps non-WPF backends in <see cref="FallbackImageDecoder"/> with WPF fallback.
/// <para>WP-06: this assembly has no WPF decoder. The <see cref="DecoderBackend.Wpf"/> slot is filled by an explicit provider, else by
/// <c>wpfDecoderFactory</c> (<c>PhotoReview.Imaging.Wpf.WpfBitmapImageDecoder</c> in the WPF app), else it is mapped to the
/// <see cref="DecoderBackend.WicDirect"/> provider (the Win32 build, NE-8): a persisted "Wpf" setting keeps working and is never
/// rewritten, it is only served by WIC Direct. Having none of the three is a composition error.</para>
/// </summary>
public sealed class ImageDecoderFactory : IImageDecoderFactory
{
    private readonly Dictionary<DecoderBackend, Func<IImageDecoder>> _registry;
    // IMG-08: decoders are stateless/shareable, so build each backend's (primary + Wpf fallback +
    // FallbackImageDecoder) once instead of on every Create call.
    private readonly ConcurrentDictionary<DecoderBackend, Lazy<IImageDecoder>> _created = new();
    private readonly ILog _log;
    private readonly ReviewMetrics? _metrics;
    private readonly Func<DecoderBackend, IImageDecoder, IImageDecoder>? _decoderDecorator;
    // True when the Wpf slot is served by the WicDirect provider (no WPF decoder in this build): a WicDirect request then needs
    // no second, identical fallback attempt.
    private readonly bool _wpfMappedToWicDirect;

    public ImageDecoderFactory(
        IEnumerable<(DecoderBackend Backend, Func<IImageDecoder> Factory)> providers,
        ILog? log = null,
        ReviewMetrics? metrics = null,
        Func<DecoderBackend, IImageDecoder, IImageDecoder>? decoderDecorator = null,
        Func<IImageDecoder>? wpfDecoderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var dict = new Dictionary<DecoderBackend, Func<IImageDecoder>>();
        foreach (var (backend, factory) in providers)
        {
            dict[backend] = factory;
        }

        // The Wpf slot is always filled (it is the fallback of every other backend, INV-12).
        if (!dict.ContainsKey(DecoderBackend.Wpf))
        {
            if (wpfDecoderFactory is not null)
            {
                dict[DecoderBackend.Wpf] = wpfDecoderFactory;
            }
            else if (dict.TryGetValue(DecoderBackend.WicDirect, out var wicDirect))
            {
                dict[DecoderBackend.Wpf] = wicDirect;
                _wpfMappedToWicDirect = true;
            }
            else
            {
                throw new ArgumentException(
                    "No decoder can serve the Wpf backend: register a Wpf provider, pass wpfDecoderFactory, or register WicDirect.",
                    nameof(providers));
            }
        }

        _registry = dict;
        _log = log ?? NullLog.Instance;
        _metrics = metrics;
        _decoderDecorator = decoderDecorator;
    }

    public bool IsRegistered(DecoderBackend backend) => _registry.ContainsKey(backend);

    // Lazy (ExecutionAndPublication): GetOrAdd alone may run the value factory on several threads and keep one result, which
    // would build (and decorate) a decoder twice; the Lazy makes exactly one thread build it.
    public IImageDecoder Create(DecoderBackend backend) =>
        _created.GetOrAdd(backend, b => new Lazy<IImageDecoder>(() => BuildDecoder(b), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private IImageDecoder BuildDecoder(DecoderBackend backend)
    {
        if (backend == DecoderBackend.Wpf)
        {
            return Decorate(backend, _registry[DecoderBackend.Wpf]());
        }

        if (!_registry.TryGetValue(backend, out var primaryFactory))
        {
            _log.Warn($"Decoder backend {backend} is not registered, falling back to Wpf.");
            return Decorate(backend, _registry[DecoderBackend.Wpf]());
        }

        if (_wpfMappedToWicDirect && backend == DecoderBackend.WicDirect)
        {
            // Wpf and WicDirect are the same decoder here: wrapping it in a fallback to itself would only decode a failure twice.
            return Decorate(backend, primaryFactory());
        }

        var primary = primaryFactory();
        var fallback = _registry[DecoderBackend.Wpf]();
        return Decorate(backend, new FallbackImageDecoder(primary, backend, fallback, _log, _metrics));
    }

    private IImageDecoder Decorate(DecoderBackend backend, IImageDecoder decoder) =>
        _decoderDecorator?.Invoke(backend, decoder) ?? decoder;
}
