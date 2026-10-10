namespace PhotoReview.Core.Abstractions;

// C-04 (NO-WPF-EXEC-PLAN mục 5, đóng băng ở WP-01): dispatcher UI có mức ưu tiên.
// Thực thi: Win32UiDispatcher (WP-14), DispatcherUiScheduler của WPF (WP-07).

/// <summary>Ánh xạ WPF theo tên: Send->Send, Normal->Normal, Render->Render, Background->Background (DispatcherPriority).</summary>
public enum UiPriority
{
    Send = 0,
    Normal = 1,
    Render = 2,
    Background = 3,
}

public interface IUiDispatcher : IUiScheduler
{
    bool CheckAccess();

    /// <summary><see cref="IUiScheduler.Post"/> = <see cref="UiPriority.Normal"/>.</summary>
    void Post(Action action, UiPriority priority);

    /// <summary><see cref="IUiScheduler.InvokeAsync"/> = <see cref="UiPriority.Normal"/>.</summary>
    Task InvokeAsync(Action action, UiPriority priority);

    /// <summary><see cref="IUiScheduler.YieldAsync"/> = <see cref="UiPriority.Background"/>.</summary>
    ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default);
}
