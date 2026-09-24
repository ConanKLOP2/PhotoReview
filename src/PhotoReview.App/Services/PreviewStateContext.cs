using System;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;

namespace PhotoReview.App.Services;

/// <summary>
/// Bridges UI/view state to the singleton <see cref="PhotoReview.Imaging.Caching.PreviewImageService"/>
/// until full MVVM ViewModel binding is introduced in Wave W4.
/// </summary>
public sealed class PreviewStateContext
{
    public Func<bool> IsOriginalLoadingMode { get; set; } = () => false;

    /// <summary>Preview decode box (device pixels); <see cref="DecodeBox.Unbounded"/> = full size.</summary>
    public Func<DecodeBox> TargetDecodeBox { get; set; } = () => DecodeBox.Unbounded;
    public Func<DecoderBackend> CurrentBackend { get; set; } = () => DecoderBackend.Wpf;
}
