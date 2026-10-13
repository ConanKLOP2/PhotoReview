namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// Chủ sở hữu cổng cửa sổ phụ trong DI: <see cref="Dispose"/> (container hoặc <c>ShellStartup</c> trước khi huỷ cửa sổ chủ) đóng
/// mọi hộp thoại phụ còn mở, chỉ khi cổng đã được dựng (không dựng chỉ để dọn). Gọi trên UI thread. WP-35 GĐ5: cổng luôn là
/// <see cref="Native.SecondaryWindowRouter"/> trên hộp thoại Win32 gốc; không còn cầu WPF nạp lười.
/// </summary>
internal sealed class SecondaryWindowPortHost(Lazy<ISecondaryWindowPort?> port) : IDisposable
{
    public Lazy<ISecondaryWindowPort?> Port { get; } = port;

    public void Dispose()
    {
        if (Port.IsValueCreated && Port.Value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
