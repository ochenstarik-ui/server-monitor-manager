using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitorManager_Desktop;

public sealed partial class ServersPage : Page
{
    private readonly MainPage _host;

    internal ServersPage(MainPage host)
    {
        _host = host;
        InitializeComponent();
        Servers.CollectionChanged += (_, _) => UpdateEmptyState();
        Loaded += (_, _) => UpdateEmptyState();
    }

    public ObservableCollection<ServerViewModel> Servers => _host.Servers;

    private ServerViewModel? SelectedServer => ServerList.SelectedItem as ServerViewModel;

    private void AddButton_Click(object sender, RoutedEventArgs e) => _host.AddServerFromPage();

    private void EditButton_Click(object sender, RoutedEventArgs e) => _host.EditServerFromPage(SelectedServer);

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => _host.DeleteServerFromPage(SelectedServer);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await _host.RefreshServersFromPageAsync();

    private void UpdateEmptyState() => EmptyServersInfo.IsOpen = Servers.Count == 0;
}
