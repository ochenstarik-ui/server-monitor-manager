using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitorManager_Desktop;

public sealed partial class LinksPage : Page
{
    private readonly MainPage _host;

    internal LinksPage(MainPage host)
    {
        _host = host;
        InitializeComponent();
    }

    public ObservableCollection<MeshNodeViewModel> Nodes => _host.MeshNodes;

    public ObservableCollection<MeshLinkViewModel> Links => _host.MeshLinks;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await _host.RefreshLinksFromPageAsync();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        => await ChangeLinkAsync(enable: true);

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        => await ChangeLinkAsync(enable: false);

    private void ReenrollButton_Click(object sender, RoutedEventArgs e)
        => _host.ReenrollNodeFromPage(SourceNodeBox.SelectedItem as MeshNodeViewModel);

    private async Task ChangeLinkAsync(bool enable)
    {
        var protocol = (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tcp";
        await _host.ChangeLinkFromPageAsync(
            SourceNodeBox.SelectedItem as MeshNodeViewModel,
            TargetNodeBox.SelectedItem as MeshNodeViewModel,
            LinksList.SelectedItem as MeshLinkViewModel,
            protocol,
            double.IsNaN(PortBox.Value) ? 0 : checked((int)PortBox.Value),
            double.IsNaN(TtlBox.Value) ? 0 : checked((int)TtlBox.Value),
            enable);
    }
}
