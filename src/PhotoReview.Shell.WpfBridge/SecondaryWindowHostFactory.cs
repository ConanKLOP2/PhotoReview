namespace PhotoReview.Shell.WpfBridge;

public static class SecondaryWindowHostFactory
{
    /// <summary>
    /// Tạo host cửa sổ phụ WPF (WP-19b). Không nạp WPF: <c>Application</c>/Dispatcher WPF chỉ được tạo khi mở cửa sổ phụ đầu
    /// tiên. Gọi trên UI thread của shell; instance gắn với thread đó. Instance cũng implement
    /// <see cref="ISecondaryWindowMessagePump"/> và <see cref="IDisposable"/>.
    /// </summary>
    public static ISecondaryWindowHost Create(IServiceProvider services) => new WpfSecondaryWindowHost(services);
}
