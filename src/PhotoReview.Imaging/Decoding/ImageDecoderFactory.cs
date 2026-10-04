using System.Collections.Concurrent;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Default implementation of <see cref="IImageDecoderFactory"/>.
/// Resolves the requested decoder backend and wraps non-WPF backends in <see cref="FallbackImageDecoder"/> with WPF fallback.
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

    public ImageDecoderFactory(
        IEnumerable<(DecoderBackend Backend, Func<IImageDecoder> Factory)> providers,
        ILog? log = null,
        ReviewMetrics? metrics = null,
        Func<DecoderBackend, IImageDecoder, IImageDecoder>? decoderDecorator = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var dict = new Dictionary<DecoderBackend, Func<IImageDecoder>>();
        foreach (var (backend, factory) in providers)
        {
            dict[backend] = factory;
        }

        // Ensure default Wpf backend is always available
        if (!dict.ContainsKey(DecoderBackend.Wpf))
        {
            dict[DecoderBackend.Wpf] = () => new WpfBitmapImageDecoder();
        }

        _registry = dict;
        _log = log ?? NullLog.Instance;
        _metrics = metrics;
        _decoderDecorator = decoderDecorator;
    }

    public ImageDecoderFactory(ILog? log = null, ReviewMetrics? metrics = null)
        : this(CreateDefaultProviders(), log, metrics)
    {
    }

    private static List<(DecoderBackend, Func<IImageDecoder>)> CreateDefaultProviders()
    {
        return
        [
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder()),
            (DecoderBackend.WicDirect, () => new WicDirectDecoder())
        ];
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

        var primary = primaryFactory();
        var fallback = _registry[DecoderBackend.Wpf]();
        return Decorate(backend, new FallbackImageDecoder(primary, backend, fallback, _log, _metrics));
    }

    private IImageDecoder Decorate(DecoderBackend backend, IImageDecoder decoder) =>
        _decoderDecorator?.Invoke(backend, decoder) ?? decoder;
}
