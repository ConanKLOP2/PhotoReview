using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>Trang "Diagnostics" (WPF: <c>DiagnosticsPage</c>): ghi log chẩn đoán + mở vị trí file log. Thuộc tính: LoggingEnabled.</summary>
internal sealed class DiagnosticsSettingsPage : SettingsPageBase
{
    private int _logging;
    private int _openLog;

    public override string Key => "Diagnostics";

    /// <summary>Id điều khiển (test).</summary>
    internal (int Logging, int OpenLog) Ids => (_logging, _openLog);

    public override void Declare(SettingsPageBuilder page)
    {
        page.Group(() => Tr.SettingsNavDiagnostics, g =>
        {
            _logging = g.Check(() => Tr.SettingsLoggingLabel);
            _openLog = g.Buttons(false, () => Tr.SettingsOpenLogLocation)[0];
        });
    }

    public override void Load(SettingsFormState state) => Context.SetChecked(_logging, state.LoggingEnabled);

    public override void Collect(SettingsFormState state) => state.LoggingEnabled = Context.IsChecked(_logging);

    public override bool OnCommand(int id, int notification)
    {
        if (id != _openLog || notification != DialogConstants.BnClicked)
        {
            return false;
        }

        Context.RunExplorer(SettingsPresenter.PrepareLogLocation());
        return true;
    }
}
