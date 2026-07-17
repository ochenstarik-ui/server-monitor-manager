using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitorManager_Desktop;

public sealed partial class SessionsPage : Page
{
    private readonly MainPage _host;

    internal SessionsPage(MainPage host)
    {
        _host = host;
        InitializeComponent();
    }

    public ObservableCollection<ServerViewModel> Servers => _host.Servers;

    private void OpenTerminalButton_Click(object sender, RoutedEventArgs e)
        => _host.OpenTerminalFromPage(SessionTargetsList.SelectedItem as ServerViewModel);
}
