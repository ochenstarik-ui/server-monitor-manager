using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitorManager_Desktop;

public sealed partial class SettingsPage : Page
{
    private readonly MainPage _host;

    internal SettingsPage(MainPage host)
    {
        _host = host;
        InitializeComponent();
        Loaded += (_, _) => UpdateStatus();
    }

    private void ConnectControlButton_Click(object sender, RoutedEventArgs e)
    {
        _host.ConnectControlHubFromPage();
        UpdateStatus();
    }

    private void ShowSshKeyButton_Click(object sender, RoutedEventArgs e)
        => _host.ShowSshKeyFromPage();

    private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        => _host.ExportDiagnosticsFromPage();

    private void UpdateStatus()
        => ControlStatusText.Text = _host.IsControlConfigured
            ? "Operator-сертификат установлен и защищён Windows DPAPI."
            : "Control Hub ещё не подключён. Создайте одноразовый device code на Hub.";
}
