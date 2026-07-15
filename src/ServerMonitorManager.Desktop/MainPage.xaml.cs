using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ServerMonitorManager_Desktop;

public sealed partial class MainPage : Page
{
    private readonly ServerStorage _storage = new();
    private readonly SshMonitorService _ssh = new();
    private bool _loaded;

    public MainPage()
    {
        InitializeComponent();
        ManualLinkToggle.Toggled += ManualLinkToggle_Toggled;
        Loaded += MainPage_Loaded;
        MainNavigation.SelectedItem = MainNavigation.MenuItems[0];
    }

    public ObservableCollection<ServerViewModel> Servers { get; } = [];

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        foreach (var profile in await _storage.LoadAsync())
        {
            Servers.Add(new ServerViewModel(profile));
        }
        UpdateEmptyState();

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
        var validationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children = { nameBox, hostBox, portBox, userBox, validationText }
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

        var profile = new ServerProfileData(
            Guid.NewGuid().ToString("N"),
            nameBox.Text.Trim(),
            hostBox.Text.Trim(),
            checked((int)portBox.Value),
            userBox.Text.Trim());
        var server = new ServerViewModel(profile);
        Servers.Add(server);
        await SaveProfilesAsync();
        UpdateEmptyState();
        await RefreshServerAsync(server);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        if (Servers.Count == 0)
        {
            ShowInfo("Серверы не добавлены", "Сначала создайте SSH-ключ и установите его на сервере.", InfoBarSeverity.Informational);
            return;
        }

        await Task.WhenAll(Servers.Select(RefreshServerAsync));
        var online = Servers.Count(server => server.IsOnline);
        var warnings = Servers.Count - online;
        AvailabilityValueText.Text = $"{online} / {Servers.Count}";
        AvailabilityDetailText.Text = warnings == 0 ? "Все серверы доступны" : $"Недоступно: {warnings}";
        AverageLoadValueText.Text = online == 0
            ? "—"
            : $"{Servers.Where(server => server.IsOnline).Average(server => server.CpuPercent):F0}%";
        WarningValueText.Text = warnings.ToString(CultureInfo.InvariantCulture);
        WarningDetailText.Text = warnings == 0 ? "Нет предупреждений" : "Проверьте SSH и ключ";
        HeaderStatusText.Text = $"SSH monitoring · {Servers.Count} сервер(а) · обновлено {DateTime.Now:HH:mm:ss}";
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
            server.LatencyText = $"{metrics.Latency.TotalMilliseconds:F0} ms";
            server.Status = $"Онлайн · uptime {FormatUptime(metrics.Uptime)}";
            server.IsOnline = true;
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

    private void ManualLinkToggle_Toggled(object sender, RoutedEventArgs e)
        => ApplyLinkState(ManualLinkToggle.IsOn);

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        => ManualLinkToggle.IsOn = false;

    private void ApplyLinkState(bool isActive)
    {
        LinkStatusText.Text = isActive
            ? "Защищённая связь активна"
            : "Связь отключена вручную";
        LinkStatusText.Opacity = isActive ? 1 : 0.68;
        DisconnectButton.IsEnabled = isActive;
        TerminalButton.IsEnabled = isActive;
        ShowInfo(
            isActive ? "Связь включена" : "Hermes отключён от Home Lab",
            isActive
                ? "Маршрут доступен только для SSH-порта 20202."
                : "Желаемое состояние сохранено: Disabled. Оба агента должны подтвердить удаление peer и маршрута.",
            isActive ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowInfo(
                "Настройки",
                "Профили серверов хранятся локально; приватный SSH-ключ не покидает этот ПК.",
                InfoBarSeverity.Informational);
        }
    }
}
