using PhotoReview.App.Presenters;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a: HỢP ĐỒNG của một trang Settings Win32 (mỗi trang một file trong <c>Dialogs/SettingsUi/Pages</c>). Khung
/// (<see cref="SettingsDialog"/>) lo điều hướng, cuộn, Save/Apply/Cancel/Defaults/Reset/Import/Export, bố cục, DPI, ngôn ngữ; trang chỉ
/// (1) KHAI BÁO điều khiển bằng <see cref="SettingsPageBuilder"/>, (2) chép giá trị giữa điều khiển và <see cref="SettingsFormState"/>,
/// (3) xử lý lệnh/ghi chú phụ thuộc của riêng nó. Mọi quy tắc (kiểm hợp lệ, ánh xạ AppSettings) nằm ở <c>SettingsPresenter</c> +
/// <c>SettingsFieldMap</c> (không đổi). Thứ tự mục của combobox LÀ hợp đồng với <see cref="SettingsFormState"/> (chỉ số), nên
/// trang phải khai báo đúng thứ tự mục của bản WPF.
/// </summary>
internal interface ISettingsPageView
{
    /// <summary>Khoá trang, khớp <see cref="SettingsPresenter.Pages"/> (General, Display, Mouse, Performance, Shortcuts, Files, Diagnostics).</summary>
    string Key { get; }

    /// <summary>Tiêu đề đã dịch (đổi khi đổi ngôn ngữ).</summary>
    string Title { get; }

    /// <summary>Gọi MỘT lần trước khi cửa sổ tạo: khai báo điều khiển + bố cục. Thứ tự khai báo = thứ tự Tab.</summary>
    void Declare(SettingsPageBuilder page);

    /// <summary>Gọi một lần sau khi điều khiển đã tạo (trước <see cref="Load"/>): giữ <paramref name="context"/>, nạp danh sách động.</summary>
    void Attach(SettingsPageContext context);

    /// <summary>Form -&gt; điều khiển (mọi thuộc tính của trang). Gọi lúc mở, Defaults, Import, Reset trang.</summary>
    void Load(SettingsFormState state);

    /// <summary>Điều khiển -&gt; form (CHỈ các thuộc tính của trang; phần còn lại giữ giá trị đã nạp).</summary>
    void Collect(SettingsFormState state);

    /// <summary>WM_COMMAND của điều khiển trang (<paramref name="notification"/> = BN_CLICKED/CBN_SELCHANGE/EN_CHANGE...). true = đã xử lý.</summary>
    bool OnCommand(int id, int notification);

    /// <summary>WM_HSCROLL của thanh trượt <paramref name="id"/> của trang.</summary>
    void OnScroll(int id);

    /// <summary>Đã đổi ngôn ngữ: đặt lại các chữ do mã đặt (gợi ý, giá trị, trạng thái). Chữ khai báo bằng builder tự theo.</summary>
    void OnLanguageChanged();

    /// <summary>Điều khiển cần focus khi Save bị từ chối ở trường này (null = trang không có trường đó).</summary>
    int? FocusTarget(SettingsFocusField field);

    /// <summary>
    /// Lệnh "Open in external editor" từ ngoài (<c>SettingsTarget.ExternalEditor</c>): trang Files đưa focus vào ô đường dẫn trình
    /// soạn thảo và trả true; các trang khác giữ mặc định (false). Khung gọi một lần, sau khi cửa sổ đã bố cục xong.
    /// </summary>
    bool FocusExternalEditor() => false;

    /// <summary>Cửa sổ đóng.</summary>
    void OnClosed();
}

/// <summary>Cài đặt mặc định (không làm gì) cho <see cref="ISettingsPageView"/>: trang chỉ ghi đè phần cần.</summary>
internal abstract class SettingsPageBase : ISettingsPageView
{
    /// <summary>Ngữ cảnh chạy (có sau <see cref="Attach"/>).</summary>
    protected SettingsPageContext Context { get; private set; } = null!;

    public abstract string Key { get; }

    public virtual string Title => SettingsPresenter.PageTitle(Key);

    public abstract void Declare(SettingsPageBuilder page);

    public virtual void Attach(SettingsPageContext context) => Context = context;

    public abstract void Load(SettingsFormState state);

    public abstract void Collect(SettingsFormState state);

    public virtual bool OnCommand(int id, int notification) => false;

    public virtual void OnScroll(int id)
    {
    }

    public virtual void OnLanguageChanged()
    {
    }

    public virtual int? FocusTarget(SettingsFocusField field) => null;

    public virtual bool FocusExternalEditor() => false;

    public virtual void OnClosed()
    {
    }
}
