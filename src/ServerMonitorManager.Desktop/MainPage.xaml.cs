using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace ServerMonitorManager_Desktop;

public sealed partial class MainPage : Page
{
    private readonly ServerStorage _storage = new();
    private readonly SshMonitorService _ssh = new();
    private readonly MetricsHistoryStorage _historyStorage = new();
    private readonly List<MetricSampleData> _history = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _loaded;

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
    }

    public ObservableCollection<ServerViewModel> Servers { get; } = [];
    public ObservableCollection<MeshNodeViewModel> MeshNodes { get; } = [];
    public ObservableCollection<MeshLinkViewModel> MeshLinks { get; } = [];

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _refreshTimer.Start();
        _history.AddRange(await _historyStorage.LoadAsync());
        foreach (var profile in await _storage.LoadAsync())
        {
            Servers.Add(new ServerViewModel(profile));
        }
        UpdateEmptyState();
        if (Servers.Count > 0)
        {
            ServerList.SelectedIndex = 0;
            RenderHistory();
        }

        if (Servers.Count > 0)
        {
            await RefreshAllAsync();
        }
    }

    private async void SshKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var publicKey = await _ssh.EnsureKeyPairAsync();
            var keyBox = new TextBox
            {
                Text = publicKey,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 520
            };
            AutomationProperties.SetName(keyBox, "Публичный SSH-ключ мониторинга");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "SSH-ключ мониторинга",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Скопируйте этот публичный ключ и вставьте его в установочный скрипт на каждом сервере. Приватный ключ остаётся только на этом ПК.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        keyBox
                    }
                },
                PrimaryButtonText = "Копировать",
                CloseButtonText = "Закрыть",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(publicKey);
                Clipboard.SetContent(package);
                ShowInfo("SSH-ключ скопирован", "Вставьте его в ochenstarik-server-monitor-manager.sh на каждом сервере.", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            ShowInfo("Не удалось создать SSH-ключ", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _ssh.EnsureKeyPairAsync();
        }
        catch (Exception exception)
        {
            ShowInfo("Не удалось подготовить SSH-ключ", exception.Message, InfoBarSeverity.Error);
            return;
        }

        var nameBox = new TextBox { Header = "Название", PlaceholderText = "Home Lab" };
        var hostBox = new TextBox { Header = "IP или домен", PlaceholderText = "192.0.2.10" };
        var portBox = new NumberBox
        {
            Header = "SSH-порт",
            Value = 22,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var userBox = new TextBox { Header = "Пользователь", Text = "ochenstarik-monitor" };
        var hubBox = new CheckBox
        {
            Content = "Это главный Mesh Hub"
        };
        AutomationProperties.SetName(hubBox, "Использовать сервер как главный Mesh Hub");
        var validationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children = { nameBox, hostBox, portBox, userBox, hubBox, validationText }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Добавить сервер",
            Content = content,
            PrimaryButtonText = "Добавить и проверить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)
                || string.IsNullOrWhiteSpace(hostBox.Text)
                || string.IsNullOrWhiteSpace(userBox.Text)
                || double.IsNaN(portBox.Value))
            {
                validationText.Text = "Заполните название, адрес, порт и пользователя.";
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (hubBox.IsChecked == true && Servers.Any(server => server.IsHub))
        {
            ShowInfo("Hub уже выбран", "Измените существующий Hub или снимите эту отметку.", InfoBarSeverity.Warning);
            return;
        }

        var profile = new ServerProfileData(
            Guid.NewGuid().ToString("N"),
            nameBox.Text.Trim(),
            hostBox.Text.Trim(),
            checked((int)portBox.Value),
            userBox.Text.Trim(),
            hubBox.IsChecked == true);
        var server = new ServerViewModel(profile);
        Servers.Add(server);
        await SaveProfilesAsync();
        UpdateEmptyState();
        await RefreshServerAsync(server);
    }

    private async void EditServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerViewModel selected)
        {
            ShowInfo("Сервер не выбран", "Выберите сервер в списке для изменения.", InfoBarSeverity.Warning);
            return;
        }

        var nameBox = new TextBox { Header = "Название", Text = selected.Profile.Name };
        var hostBox = new TextBox { Header = "IP или домен", Text = selected.Profile.Host };
        var portBox = new NumberBox
        {
            Header = "SSH-порт",
            Value = selected.Profile.Port,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var userBox = new TextBox { Header = "Пользователь", Text = selected.Profile.User };
        var hubBox = new CheckBox { Content = "Это главный Mesh Hub", IsChecked = selected.IsHub };
        AutomationProperties.SetName(hubBox, "Использовать сервер как главный Mesh Hub");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Изменить сервер",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 420,
                Children = { nameBox, hostBox, portBox, userBox, hubBox }
            },
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(nameBox.Text)
            || string.IsNullOrWhiteSpace(hostBox.Text)
            || string.IsNullOrWhiteSpace(userBox.Text)
            || double.IsNaN(portBox.Value))
        {
            ShowInfo("Данные не сохранены", "Название, адрес, порт и пользователь обязательны.", InfoBarSeverity.Warning);
            return;
        }
        if (hubBox.IsChecked == true && Servers.Any(server => server.IsHub && server != selected))
        {
            ShowInfo("Hub уже выбран", "В конфигурации может быть только один главный Mesh Hub.", InfoBarSeverity.Warning);
            return;
        }

        var index = Servers.IndexOf(selected);
        var updated = new ServerViewModel(new ServerProfileData(
            selected.Profile.Id,
            nameBox.Text.Trim(),
            hostBox.Text.Trim(),
            checked((int)portBox.Value),
            userBox.Text.Trim(),
            hubBox.IsChecked == true));
        Servers[index] = updated;
        await SaveProfilesAsync();
        await RefreshServerAsync(updated);
        ShowInfo("Сервер изменён", updated.Name, InfoBarSeverity.Success);
    }

    private async void DeleteServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerViewModel selected)
        {
            ShowInfo("Сервер не выбран", "Выберите сервер в списке для удаления.", InfoBarSeverity.Warning);
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Удалить {selected.Name}?",
            Content = "Удаляется только локальный профиль. Серверная часть и WireGuard Node останутся установленными.",
            PrimaryButtonText = "Удалить профиль",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        Servers.Remove(selected);
        _history.RemoveAll(sample => sample.ServerId == selected.Profile.Id);
        await SaveProfilesAsync();
        await _historyStorage.SaveAsync(_history);
        UpdateEmptyState();
        ShowInfo("Профиль удалён", selected.Name, InfoBarSeverity.Success);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }
        try
        {
            if (Servers.Count == 0)
            {
                ShowInfo("Серверы не добавлены", "Сначала создайте SSH-ключ и установите его на сервере.", InfoBarSeverity.Informational);
                return;
            }

            await Task.WhenAll(Servers.Select(RefreshServerAsync));
            await _historyStorage.SaveAsync(_history);
            var online = Servers.Count(server => server.IsOnline);
            var warnings = Servers.Count(server => !server.IsOnline || server.HasWarning);
            AvailabilityValueText.Text = $"{online} / {Servers.Count}";
            AvailabilityDetailText.Text = warnings == 0 ? "Все серверы доступны" : $"Недоступно: {warnings}";
            AverageLoadValueText.Text = online == 0
                ? "—"
                : $"{Servers.Where(server => server.IsOnline).Average(server => server.CpuPercent):F0}%";
            WarningValueText.Text = warnings.ToString(CultureInfo.InvariantCulture);
            WarningDetailText.Text = warnings == 0 ? "Нет предупреждений" : "Проверьте доступность и ресурсы";
            HeaderStatusText.Text = $"SSH monitoring · {Servers.Count} сервер(а) · обновлено {DateTime.Now:HH:mm:ss}";
            if (Servers.Any(server => server.IsHub))
            {
                await RefreshMeshAsync(showSuccess: false);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshServerAsync(ServerViewModel server)
    {
        server.Status = "Подключение…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var metrics = await _ssh.QueryAsync(server.Profile, timeout.Token);
            server.CpuPercent = metrics.CpuPercent;
            server.CpuText = $"{metrics.CpuPercent:F0}% load";
            server.MemoryText = $"{FormatSize(metrics.MemoryUsedKb)} / {FormatSize(metrics.MemoryTotalKb)} RAM";
            server.DiskText = $"{FormatSize(metrics.DiskUsedKb)} / {FormatSize(metrics.DiskTotalKb)} disk";
            var memoryPercent = Percent(metrics.MemoryUsedKb, metrics.MemoryTotalKb);
            var diskPercent = Percent(metrics.DiskUsedKb, metrics.DiskTotalKb);
            var inodePercent = Percent(metrics.InodesUsed, metrics.InodesTotal);
            server.HasWarning = memoryPercent >= 90 || diskPercent >= 90 || inodePercent >= 90 || metrics.SshState != "active";
            server.HealthText = $"swap {FormatSize(metrics.SwapUsedKb)}/{FormatSize(metrics.SwapTotalKb)} · inode {inodePercent:F0}% · net ↓{FormatBytes(metrics.NetworkRxBytes)} ↑{FormatBytes(metrics.NetworkTxBytes)} · SSH {metrics.SshState}";
            server.LatencyText = $"{metrics.Latency.TotalMilliseconds:F0} ms";
            server.Status = $"Онлайн · uptime {FormatUptime(metrics.Uptime)}";
            server.IsOnline = true;
            _history.Add(new MetricSampleData(
                server.Profile.Id,
                DateTimeOffset.Now,
                metrics.CpuPercent,
                memoryPercent,
                diskPercent));
            var overflow = _history
                .Where(sample => sample.ServerId == server.Profile.Id)
                .OrderByDescending(sample => sample.Timestamp)
                .Skip(240)
                .ToList();
            foreach (var sample in overflow)
            {
                _history.Remove(sample);
            }
            if (ServerList.SelectedItem == server)
            {
                RenderHistory();
            }
        }
        catch (Exception exception)
        {
            server.Status = CompactError(exception);
            server.CpuText = "—";
            server.CpuPercent = 0;
            server.MemoryText = "—";
            server.DiskText = "—";
            server.LatencyText = "offline";
            server.IsOnline = false;
            server.HasWarning = true;
            server.HealthText = "Нет данных о службах и сети";
        }
    }

    private async Task SaveProfilesAsync()
        => await _storage.SaveAsync(Servers.Select(server => server.Profile));

    private void UpdateEmptyState()
    {
        EmptyServersText.Visibility = Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderStatusText.Text = Servers.Count == 0
            ? "SSH monitoring · серверы ещё не добавлены"
            : $"SSH monitoring · {Servers.Count} сервер(а)";
        if (Servers.Count == 0)
        {
            AvailabilityValueText.Text = "0";
            AvailabilityDetailText.Text = "Добавьте серверы";
            AverageLoadValueText.Text = "—";
            WarningValueText.Text = "0";
            WarningDetailText.Text = "Нет данных";
        }
    }

    private static string FormatSize(long kilobytes)
        => kilobytes >= 1024 * 1024
            ? $"{kilobytes / 1024d / 1024d:F1} GB"
            : $"{kilobytes / 1024d:F0} MB";

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1
            ? $"{uptime.TotalDays:F0} дн."
            : $"{uptime.TotalHours:F0} ч.";

    private static double Percent(long used, long total)
        => total <= 0 ? 0 : Math.Clamp(used * 100d / total, 0, 100);

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:F1} GB"
            : $"{bytes / 1024d / 1024d:F1} MB";

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RenderHistory();

    private void HistoryChart_SizeChanged(object sender, SizeChangedEventArgs e)
        => RenderHistory();

    private void RenderHistory()
    {
        if (ServerList.SelectedItem is not ServerViewModel server)
        {
            HistoryCaptionText.Text = "Выберите сервер";
            CpuHistoryLine.Points = new PointCollection();
            MemoryHistoryLine.Points = new PointCollection();
            DiskHistoryLine.Points = new PointCollection();
            return;
        }

        var samples = _history
            .Where(sample => sample.ServerId == server.Profile.Id)
            .OrderBy(sample => sample.Timestamp)
            .ToList();
        HistoryCaptionText.Text = samples.Count == 0
            ? $"{server.Name} · данные появятся после обновления"
            : $"{server.Name} · {samples.Count} точек · {samples[0].Timestamp:dd.MM HH:mm} — {samples[^1].Timestamp:dd.MM HH:mm}";
        CpuHistoryLine.Points = BuildHistoryPoints(samples.Select(sample => sample.CpuPercent).ToList());
        MemoryHistoryLine.Points = BuildHistoryPoints(samples.Select(sample => sample.MemoryPercent).ToList());
        DiskHistoryLine.Points = BuildHistoryPoints(samples.Select(sample => sample.DiskPercent).ToList());
    }

    private PointCollection BuildHistoryPoints(IReadOnlyList<double> values)
    {
        var points = new PointCollection();
        if (values.Count == 0 || HistoryChart.ActualWidth <= 0 || HistoryChart.ActualHeight <= 0)
        {
            return points;
        }
        var step = values.Count == 1 ? 0 : HistoryChart.ActualWidth / (values.Count - 1);
        for (var index = 0; index < values.Count; index++)
        {
            var x = values.Count == 1 ? HistoryChart.ActualWidth : index * step;
            var y = HistoryChart.ActualHeight * (1 - Math.Clamp(values[index], 0, 100) / 100d);
            points.Add(new Point(x, y));
        }
        return points;
    }

    private static string CompactError(Exception exception)
    {
        var firstLine = exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "Ошибка подключения" : firstLine;
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        LinkActionInfo.Title = title;
        LinkActionInfo.Message = message;
        LinkActionInfo.Severity = severity;
        LinkActionInfo.IsOpen = true;
    }

    private ServerViewModel? FindHub()
        => Servers.FirstOrDefault(server => server.IsHub);

    private async Task RefreshMeshAsync(bool showSuccess = true)
    {
        var hub = FindHub();
        if (hub is null)
        {
            ShowInfo("Mesh Hub не выбран", "Добавьте главный сервер или отметьте его как Mesh Hub.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var nodesOutput = await _ssh.RunRestrictedCommandAsync(hub.Profile, "mesh nodes", timeout.Token);
            var linksOutput = await _ssh.RunRestrictedCommandAsync(hub.Profile, "mesh links", timeout.Token);
            MeshNodes.Clear();
            foreach (var line in nodesOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("NODE=", StringComparison.Ordinal))
                {
                    continue;
                }
                var fields = line[5..].Split('|');
                if (fields.Length == 4 && int.TryParse(fields[3], out var age))
                {
                    MeshNodes.Add(new MeshNodeViewModel(fields[0], fields[1], fields[2], age));
                }
            }

            MeshLinks.Clear();
            foreach (var line in linksOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("LINK=", StringComparison.Ordinal))
                {
                    continue;
                }
                var fields = line[5..].Split('|');
                if (fields.Length >= 8
                    && int.TryParse(fields[4], CultureInfo.InvariantCulture, out var port)
                    && long.TryParse(fields[5], CultureInfo.InvariantCulture, out var expiresUnix)
                    && long.TryParse(fields[7], CultureInfo.InvariantCulture, out var version))
                {
                    MeshLinks.Add(new MeshLinkViewModel(
                        fields[0],
                        fields[1],
                        fields[2],
                        fields[3],
                        port,
                        expiresUnix,
                        fields[6],
                        version));
                }
            }

            ActiveLinksValueText.Text = MeshLinks.Count.ToString(CultureInfo.InvariantCulture);
            MeshStatusText.Text = $"{MeshNodes.Count} узлов · {MeshLinks.Count} активных связей";
            if (showSuccess)
            {
                ShowInfo("Mesh обновлён", MeshStatusText.Text, InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            MeshStatusText.Text = "Hub недоступен";
            ShowInfo("Не удалось получить Mesh-состояние", CompactError(exception), InfoBarSeverity.Error);
        }
    }

    private async void RefreshMeshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshMeshAsync();

    private async void ConnectLinkButton_Click(object sender, RoutedEventArgs e)
        => await ChangeLinkAsync(enable: true);

    private async void DisconnectLinkButton_Click(object sender, RoutedEventArgs e)
        => await ChangeLinkAsync(enable: false);

    private async Task ChangeLinkAsync(bool enable)
    {
        var hub = FindHub();
        if (hub is null)
        {
            ShowInfo("Mesh Hub не выбран", "Сначала добавьте главный сервер с отметкой Mesh Hub.", InfoBarSeverity.Warning);
            return;
        }
        MeshNodeViewModel? source = null;
        MeshNodeViewModel? target = null;
        string protocol;
        int port;
        int ttlMinutes;

        if (enable)
        {
            if (SourceNodeBox.SelectedItem is not MeshNodeViewModel selectedSource
                || TargetNodeBox.SelectedItem is not MeshNodeViewModel selectedTarget)
            {
                ShowInfo("Выберите серверы", "Укажите источник и сервер назначения.", InfoBarSeverity.Warning);
                return;
            }
            source = selectedSource;
            target = selectedTarget;
            protocol = (LinkProtocolBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tcp";
            if (double.IsNaN(LinkPortBox.Value)
                || double.IsNaN(LinkTtlBox.Value)
                || LinkPortBox.Value is < 1 or > 65535
                || LinkTtlBox.Value is < 0 or > 525600)
            {
                ShowInfo("Некорректная политика", "Проверьте порт и TTL.", InfoBarSeverity.Warning);
                return;
            }
            port = checked((int)LinkPortBox.Value);
            ttlMinutes = checked((int)LinkTtlBox.Value);
        }
        else
        {
            if (MeshLinksList.SelectedItem is not MeshLinkViewModel selectedLink)
            {
                ShowInfo("Выберите связь", "Для отключения выберите правило в списке.", InfoBarSeverity.Warning);
                return;
            }
            source = MeshNodes.FirstOrDefault(node => node.Name == selectedLink.Source);
            target = MeshNodes.FirstOrDefault(node => node.Name == selectedLink.Target);
            if (source is null || target is null)
            {
                ShowInfo("Узел не найден", "Обновите список Mesh и повторите попытку.", InfoBarSeverity.Warning);
                return;
            }
            protocol = selectedLink.Protocol;
            port = selectedLink.Port;
            ttlMinutes = 0;
        }

        if (source.Name == target.Name)
        {
            ShowInfo("Некорректная связь", "Источник и назначение должны отличаться.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var action = enable ? "connect" : "disconnect";
            var policyArguments = enable
                ? $"{protocol} {port} {ttlMinutes}"
                : $"{protocol} {port}";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var commandOutput = await _ssh.RunRestrictedCommandAsync(
                hub.Profile,
                $"mesh {action} {source.Name} {target.Name} {policyArguments}",
                timeout.Token);
            var confirmation = commandOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("LINK_STATE=", StringComparison.Ordinal));
            if (confirmation is null)
            {
                throw new InvalidOperationException("Hub не подтвердил фактическое состояние Link.");
            }
            await RefreshMeshAsync(showSuccess: false);
            ShowInfo(
                enable ? "Hub подтвердил Active" : "Hub подтвердил Disabled",
                $"{source.Name} → {target.Name} · {protocol.ToUpperInvariant()}/{port} · {confirmation[11..]}",
                enable ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowInfo("Не удалось изменить связь", CompactError(exception), InfoBarSeverity.Error);
        }
    }

    private void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowPlaceholder(
                "Настройки",
                "Профили серверов хранятся локально. Настройки ключей, интервалов обновления и подтверждений будут добавляться здесь.");
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "overview";
        NavigationPlaceholder.Visibility = Visibility.Collapsed;
        OverviewSummary.Visibility = Visibility.Visible;
        WorkspaceScroll.Visibility = Visibility.Visible;
        ServerWorkspace.Visibility = tag is "overview" or "servers" ? Visibility.Visible : Visibility.Collapsed;
        LinkInspector.Visibility = tag is "overview" or "links" ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = tag == "overview" && ActualWidth >= 1080
            ? new GridLength(360)
            : new GridLength(0);

        if (tag == "links")
        {
            Grid.SetRow(LinkInspector, 0);
            Grid.SetColumn(LinkInspector, 0);
            Grid.SetColumnSpan(LinkInspector, 2);
        }
        else
        {
            Grid.SetColumnSpan(LinkInspector, 1);
            if (ActualWidth >= 1080)
            {
                Grid.SetRow(LinkInspector, 0);
                Grid.SetColumn(LinkInspector, 1);
            }
            else
            {
                Grid.SetRow(LinkInspector, 1);
                Grid.SetColumn(LinkInspector, 0);
            }
        }

        if (tag == "sessions")
        {
            ShowPlaceholder(
                "SSH-сессии",
                "Интерактивные терминалы будут использовать отдельную identity и не получат ключ мониторинга или права Mesh Hub.");
        }
    }

    private void ShowPlaceholder(string title, string description)
    {
        OverviewSummary.Visibility = Visibility.Collapsed;
        WorkspaceScroll.Visibility = Visibility.Collapsed;
        PlaceholderTitle.Text = title;
        PlaceholderDescription.Text = description;
        NavigationPlaceholder.Visibility = Visibility.Visible;
    }
}
