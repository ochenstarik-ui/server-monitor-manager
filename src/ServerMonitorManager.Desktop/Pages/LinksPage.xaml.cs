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
        RefreshFilter();
    }

    public ObservableCollection<MeshNodeViewModel> Nodes => _host.MeshNodes;

    public ObservableCollection<MeshLinkViewModel> DisplayedLinks { get; } = [];

    internal void RefreshFilter()
    {
        var selectedId = (LinksList.SelectedItem as MeshLinkViewModel)?.Id;
        var effective = _host.MeshLinks
            .GroupBy(link => new { link.Source, link.Target, link.Protocol, link.Port })
            .Select(group => group.MaxBy(link => link.Version)!)
            .ToArray();
        var source = ShowHistoryToggle.IsOn
            ? _host.MeshLinks
            : effective.Where(link => link.DesiredState == "Active" || link.HasDrift);
        DisplayedLinks.Clear();
        foreach (var link in source)
        {
            DisplayedLinks.Add(link);
        }
        LinksCountText.Text = $"Показано политик: {DisplayedLinks.Count} · фактически Active: {DisplayedLinks.Count(link => link.ActualState == "Active")} · с расхождением: {DisplayedLinks.Count(link => link.HasDrift)}";
        if (selectedId is not null)
        {
            LinksList.SelectedItem = DisplayedLinks.FirstOrDefault(link => link.Id == selectedId);
        }
    }

    internal void SetFirewallUnavailable(bool unavailable)
        => FirewallUnavailableInfo.IsOpen = unavailable;

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await _host.RefreshLinksFromPageAsync();

    private void ShowHistoryToggle_Toggled(object sender, RoutedEventArgs e)
        => RefreshFilter();

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
