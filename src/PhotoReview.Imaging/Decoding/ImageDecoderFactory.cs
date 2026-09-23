using System;
using System.Collections.Generic;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Default implementation of <see cref="IImageDecoderFactory"/>.
/// Resolves the requested decoder backend and wraps non-WPF backends in <see cref="FallbackImageDecoder"/> with WPF fallback.
/// </summary>
public sealed class ImageDecoderFactory : IImageDecoderFactory
{
    private readonly Dictionary<DecoderBackend, Func<IImageDecoder>> _registry;
    private readonly ILog _log;
    private readonly ReviewMetrics? _metrics;

    public ImageDecoderFactory(
        IEnumerable<(DecoderBackend Backend, Func<IImageDecoder> Factory)> providers,
        ILog? log = null,
        ReviewMetrics? metrics = null)
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

    public IImageDecoder Create(DecoderBackend backend)
    {
        if (backend == DecoderBackend.Wpf)
        {
            return _registry[DecoderBackend.Wpf]();
        }

        if (!_registry.TryGetValue(backend, out var primaryFactory))
        {
            _log.Warn($"Decoder backend {backend} is not registered, falling back to Wpf.");
            return _registry[DecoderBackend.Wpf]();
        }

        var primary = primaryFactory();
        var fallback = _registry[DecoderBackend.Wpf]();
        return new FallbackImageDecoder(primary, backend, fallback, DecoderBackend.Wpf, _log, _metrics);
    }
}
