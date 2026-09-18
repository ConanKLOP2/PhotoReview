using System;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Services;

/// <summary>
/// Bridges UI/view state to the singleton <see cref="PhotoReview.Imaging.Caching.PreviewImageService"/>
/// until full MVVM ViewModel binding is introduced in Wave W4.
/// </summary>
public sealed class PreviewStateContext
{
    public Func<bool> IsOriginalLoadingMode { get; set; } = () => false;
    public Func<int> TargetDecodeWidth { get; set; } = () => 0;
    public Func<DecoderBackend> CurrentBackend { get; set; } = () => DecoderBackend.Wpf;
}
