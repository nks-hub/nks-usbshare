using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NksUsbShare.Core;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Diagnostics;
using NksUsbShare.Core.Security;
using NksUsbShare.Core.Usb;

namespace NksUsbShare.Desktop;

public partial class MainWindow : Window
{
    private readonly NodeService _node;
    private readonly ObservableCollection<LocalDeviceRow> _localDevices = [];
    private readonly ObservableCollection<HostRow> _hosts = [];
    private readonly ObservableCollection<RemoteDeviceRow> _remoteDevices = [];
    private readonly ObservableCollection<ClientRow> _clients = [];
    private readonly ObservableCollection<string> _log = [];

    private HostRow? _browsedHost;

    public MainWindow()
    {
        InitializeComponent();

        var settings = NodeSettings.LoadOrCreate();
        var log = new ActivityLog();
        _node = new NodeService(settings, log);

        LocalDeviceList.ItemsSource = _localDevices;
        HostList.ItemsSource = _hosts;
        RemoteDeviceList.ItemsSource = _remoteDevices;
        ClientList.ItemsSource = _clients;
        LogList.ItemsSource = _log;

        log.Entered += entry => Dispatcher.BeginInvoke(() => AppendLog(entry));
        _node.StateChanged += () => Dispatcher.BeginInvoke(RefreshFromNode);

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => OnRefresh(this, new RoutedEventArgs())), Key.F5, ModifierKeys.None));

        Loaded += OnLoaded;
        Closing += (_, _) => _node.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            await _node.StartHostAsync(CancellationToken.None);
            await ReloadLocalDevicesAsync();
            await _node.AutoConnectAsync(CancellationToken.None);
        });

        RefreshFromNode();
        Report("Ready.");
    }

    // ---- data loading ----

    private async Task ReloadLocalDevicesAsync()
    {
        var devices = await _node.ListLocalDevicesWithSharesAsync(CancellationToken.None);
        var selected = (LocalDeviceList.SelectedItem as LocalDeviceRow)?.DeviceId;

        _localDevices.Clear();
        foreach (var (device, share) in devices)
        {
            var shared = share is { Enabled: true };
            _localDevices.Add(new LocalDeviceRow
            {
                DeviceId = device.DeviceId,
                Name = device.DisplayName,
                Manufacturer = device.Manufacturer,
                HardwareKey = device.HardwareKey,
                Kind = UsbDisplay.Kind(device),
                Speed = UsbDisplay.Speed(device.Speed),
                SerialNumber = device.SerialNumber,
                Status = device.ShareBlockedReason is not null ? "Unavailable" : shared ? "Shared" : "Not shared",
                StatusDetail = device.ShareBlockedReason,
                IsShared = shared,
                CanShare = device.ShareBlockedReason is null,
            });
        }

        if (selected is not null)
            LocalDeviceList.SelectedItem = _localDevices.FirstOrDefault(d => d.DeviceId == selected);

        OnLocalSelectionChanged(this, null!);
    }

    private void RefreshFromNode()
    {
        var connected = _node.ConnectedHosts.ToHashSet();

        var selectedHost = (HostList.SelectedItem as HostRow)?.Endpoint;
        _hosts.Clear();
        foreach (var host in _node.Settings.RemoteHosts)
        {
            _hosts.Add(new HostRow
            {
                Name = host.Name.Length > 0 ? host.Name : host.Address,
                Address = host.Address,
                Port = host.Port,
                Status = connected.Contains($"{host.Address}:{host.Port}") ? "Connected" : "Disconnected",
                Fingerprint = host.PinnedFingerprint ?? "not paired yet",
            });
        }
        if (selectedHost is not null)
            HostList.SelectedItem = _hosts.FirstOrDefault(h => h.Endpoint == selectedHost);

        _clients.Clear();
        foreach (var session in _node.Host.Sessions.Where(s => s.Authenticated))
        {
            _clients.Add(new ClientRow
            {
                Name = session.ClientName,
                Address = session.RemoteEndPoint?.ToString() ?? "",
                Platform = session.ClientPlatform,
                DeviceCount = session.ImportedDeviceIds.Count.ToString(),
                Since = session.ConnectedAt.ToLocalTime().ToString("HH:mm:ss"),
            });
        }

        UpdateImportedFlags();
        UpdateStatusBar();
    }

    /// <summary>Keeps the remote list's status column in step with what is actually imported.</summary>
    private void UpdateImportedFlags()
    {
        if (_remoteDevices.Count == 0) return;

        var imported = _node.Imports.ToDictionary(i => i.Key, i => i.Key);
        var refreshed = _remoteDevices.Select(row => new RemoteDeviceRow
        {
            DeviceId = row.DeviceId,
            Name = row.Name,
            Manufacturer = row.Manufacturer,
            HardwareKey = row.HardwareKey,
            Kind = row.Kind,
            Speed = row.Speed,
            HostAddress = row.HostAddress,
            HostPort = row.HostPort,
            ImportKey = imported.GetValueOrDefault($"{row.HostAddress}:{row.HostPort}|{row.DeviceId}"),
            Status = DescribeImportState(imported, $"{row.HostAddress}:{row.HostPort}|{row.DeviceId}"),
        }).ToList();

        var selected = (RemoteDeviceList.SelectedItem as RemoteDeviceRow)?.DeviceId;
        _remoteDevices.Clear();
        foreach (var row in refreshed) _remoteDevices.Add(row);

        if (selected is not null)
            RemoteDeviceList.SelectedItem = _remoteDevices.FirstOrDefault(d => d.DeviceId == selected);
    }

    /// <summary>
    /// "Attached" means the operating system itself has the device; "Imported" means only this
    /// application can reach it, which is what happens without a virtual host controller.
    /// </summary>
    private string DescribeImportState(Dictionary<string, string> imported, string key)
        => imported.ContainsKey(key)
            ? _node.IsAttachedToSystem(key) ? "Attached" : "Imported"
            : "Available";

    private void UpdateStatusBar()
    {
        var running = _node.Host.IsRunning;
        StatusState.Text = running ? "Sharing" : "Not sharing";
        StatusEndpoint.Text = running
            ? $"{_node.Settings.LinkBindAddress}:{_node.Host.Port}"
            : "listener stopped";

        var sharedCount = _localDevices.Count(d => d.IsShared);
        StatusCounts.Text = $"{sharedCount} shared · {_node.Imports.Count} in use from elsewhere · {_clients.Count} client(s)";

        StartSharingItem.IsEnabled = !running;
        StopSharingItem.IsEnabled = running;
        SharingToggle.Content = running ? "Stop sharing" : "Start sharing";

        TokenBox.Text = _node.Settings.PairingToken;
        FingerprintBox.Text = _node.Identity.Fingerprint;
        NodeNameBox.Text = _node.Settings.HostName;
        BackendBox.Text = _node.Backend.Name;
        EndpointBox.Text = $"{_node.Settings.LinkBindAddress}:{_node.Settings.LinkPort}" + (running ? "" : "   (stopped)");
        AddressesBox.Text = string.Join("   ", UsbDisplay.LocalAddresses().DefaultIfEmpty("no network interface"));
    }

    private void AppendLog(LogEntry entry)
    {
        _log.Add($"{entry.Timestamp:HH:mm:ss}  {entry.Level.ToString().ToLowerInvariant(),-7} {entry.Category,-8} {entry.Message}");
        while (_log.Count > 1000) _log.RemoveAt(0);
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    // ---- local devices ----

    private void OnLocalSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = LocalDeviceList.SelectedItem as LocalDeviceRow;
        ShareButton.IsEnabled = row is { CanShare: true, IsShared: false };
        UnshareButton.IsEnabled = row is { IsShared: true };
    }

    private void OnLocalDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalDeviceList.SelectedItem is not LocalDeviceRow row) return;

        if (!row.CanShare)
        {
            MessageBox.Show(this, row.StatusDetail ?? "This device cannot be shared.",
                "Device unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.IsShared) OnUnshareSelected(sender, e);
        else OnShareSelected(sender, e);
    }

    private async void OnShareSelected(object sender, RoutedEventArgs e)
    {
        if (LocalDeviceList.SelectedItem is not LocalDeviceRow row) return;

        await Guard(async () =>
        {
            await _node.ShareDeviceAsync(row.DeviceId, CancellationToken.None);
            await ReloadLocalDevicesAsync();
            Report($"{row.Name} is now offered to paired computers.");
        });
    }

    private async void OnUnshareSelected(object sender, RoutedEventArgs e)
    {
        if (LocalDeviceList.SelectedItem is not LocalDeviceRow row) return;

        await Guard(async () =>
        {
            _node.UnshareDevice(row.DeviceId);
            await ReloadLocalDevicesAsync();
            Report($"{row.Name} is no longer shared.");
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await Guard(async () =>
        {
            await ReloadLocalDevicesAsync();
            RefreshFromNode();
            Report("Refreshed.");
        });

    // ---- remote hosts ----

    private void OnHostSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = HostList.SelectedItem is HostRow;
        BrowseButton.IsEnabled = selected;
        RemoveHostButton.IsEnabled = selected;
    }

    private async void OnAddHost(object sender, RoutedEventArgs e)
    {
        var dialog = new AddHostDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await Guard(async () =>
        {
            var remote = _node.AddRemote(dialog.Address, dialog.Port, dialog.Token);
            remote.AutoConnect = true;
            // A freshly typed token means a fresh pairing, so a previous pin no longer applies.
            remote.PinnedFingerprint = null;
            _node.Settings.Save();

            await _node.ConnectAsync(remote, CancellationToken.None);
            RefreshFromNode();
            Report($"Paired with {remote.Name} at {dialog.Address}:{dialog.Port}.");
        });
    }

    private async void OnBrowseSelectedHost(object sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is not HostRow host) return;

        await Guard(async () =>
        {
            var remote = _node.Settings.RemoteHosts.FirstOrDefault(h =>
                h.Address.Equals(host.Address, StringComparison.OrdinalIgnoreCase) && h.Port == host.Port);
            if (remote is null) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var devices = await _node.ListRemoteDevicesAsync(remote, timeout.Token);
            var imported = _node.Imports.Select(i => i.Key).ToHashSet();

            _remoteDevices.Clear();
            foreach (var device in devices)
            {
                var key = $"{host.Address}:{host.Port}|{device.DeviceId}";
                _remoteDevices.Add(new RemoteDeviceRow
                {
                    DeviceId = device.DeviceId,
                    Name = device.DisplayName,
                    Manufacturer = device.Manufacturer,
                    HardwareKey = device.HardwareKey,
                    Kind = UsbDisplay.Kind(device),
                    Speed = UsbDisplay.Speed(device.Speed),
                    HostAddress = host.Address,
                    HostPort = host.Port,
                    ImportKey = imported.Contains(key) ? key : null,
                    Status = imported.Contains(key) ? (_node.IsAttachedToSystem(key) ? "Attached" : "Imported") : "Available",
                });
            }

            _browsedHost = host;
            RemoteHeading.Text = $"Devices offered by {host.Name}";
            RefreshFromNode();

            Report(devices.Count == 0
                ? $"{host.Name} is not offering any device to this computer."
                : $"{host.Name} offers {devices.Count} device(s).");
        });
    }

    private async void OnRemoveHost(object sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is not HostRow host) return;

        var confirmed = MessageBox.Show(this,
            $"Remove {host.Name} ({host.Endpoint})?\n\nAny device in use from it will be released.",
            "Remove computer", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.OK) return;

        await Guard(async () =>
        {
            await _node.DisconnectAsync(host.Address, host.Port);
            _node.RemoveRemote(host.Address, host.Port);

            if (_browsedHost?.Endpoint == host.Endpoint)
            {
                _remoteDevices.Clear();
                _browsedHost = null;
                RemoteHeading.Text = "Devices offered to this computer";
            }

            RefreshFromNode();
            Report($"Removed {host.Endpoint}.");
        });
    }

    // ---- remote devices ----

    private void OnRemoteSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = RemoteDeviceList.SelectedItem as RemoteDeviceRow;
        ImportButton.IsEnabled = row is { IsImported: false };
        ReleaseButton.IsEnabled = row is { IsImported: true };
    }

    private async void OnImportSelected(object sender, RoutedEventArgs e)
    {
        if (RemoteDeviceList.SelectedItem is not RemoteDeviceRow row || row.IsImported) return;

        await Guard(async () =>
        {
            var remote = _node.Settings.RemoteHosts.FirstOrDefault(h =>
                h.Address.Equals(row.HostAddress, StringComparison.OrdinalIgnoreCase) && h.Port == row.HostPort);
            if (remote is null) return;

            var import = await _node.ImportDeviceAsync(remote, row.DeviceId, CancellationToken.None);

            // Hand it to the operating system when this machine has a controller for that. When
            // it does not, the device is still usable through the library, so say so rather than
            // failing the whole operation.
            if (_node.SystemAttachAvailable)
            {
                await _node.AttachToSystemAsync(import, CancellationToken.None);
                Report($"{import.Device.Info.DisplayName} is attached and should appear as a local device.");
            }
            else
            {
                Report($"{import.Device.Info.DisplayName} is in use by this application. " +
                       "Install a virtual USB host controller to let the whole system see it.");
            }

            RefreshFromNode();
        });
    }

    private async void OnReleaseSelected(object sender, RoutedEventArgs e)
    {
        if (RemoteDeviceList.SelectedItem is not RemoteDeviceRow { ImportKey: { } key } row) return;

        await Guard(async () =>
        {
            await _node.ReleaseImportAsync(key);
            RefreshFromNode();
            Report($"{row.Name} released.");
        });
    }

    // ---- menu ----

    /// <summary>The header button, which does whichever of start or stop makes sense right now.</summary>
    private async void OnToggleSharing(object sender, RoutedEventArgs e)
    {
        if (_node.Host.IsRunning) OnStopSharing(sender, e);
        else OnStartSharing(sender, e);
        await Task.CompletedTask;
    }

    private async void OnStartSharing(object sender, RoutedEventArgs e)
        => await Guard(async () =>
        {
            await _node.StartHostAsync(CancellationToken.None);
            UpdateStatusBar();
            Report($"Listening on {_node.Settings.LinkBindAddress}:{_node.Host.Port}.");
        });

    private async void OnStopSharing(object sender, RoutedEventArgs e)
        => await Guard(async () =>
        {
            await _node.StopHostAsync();
            UpdateStatusBar();
            Report("Stopped. Other computers can no longer reach this one.");
        });

    private void OnRegenerateToken(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(this,
            "Generate a new pairing token?\n\nEvery computer paired with the current token will have to be paired again.",
            "Regenerate pairing token", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmed != MessageBoxResult.OK) return;

        _node.Settings.PairingToken = AccessToken.Generate();
        _node.Settings.Save();
        _node.Log.Warning("host", "The pairing token was regenerated; existing clients must pair again.");
        UpdateStatusBar();
        Report("New pairing token generated.");
    }

    private void OnCopyToken(object sender, RoutedEventArgs e) => Copy(_node.Settings.PairingToken, "Pairing token");
    private void OnCopyFingerprint(object sender, RoutedEventArgs e) => Copy(_node.Identity.Fingerprint, "Key fingerprint");
    private void OnCopyLog(object sender, RoutedEventArgs e) => Copy(string.Join(Environment.NewLine, _log), "Activity log");
    private void OnClearLog(object sender, RoutedEventArgs e) => _log.Clear();

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.GetDirectoryName(_node.Settings.SourcePath);
        if (folder is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Report(ex.Message, isError: true);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(NodeService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        MessageBox.Show(this,
            $"NKS UsbShare {version}\n\n" +
            $"Shares USB devices between computers over an encrypted link.\n\n" +
            $"USB backend: {_node.Backend.Name}\n" +
            $"Settings: {_node.Settings.SourcePath}",
            "About NKS UsbShare", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- plumbing ----

    private void Copy(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            Report($"{what} copied to the clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Report($"{what} could not be copied; another program is holding the clipboard.", isError: true);
        }
    }

    private void Report(string message, bool isError = false)
    {
        StatusMessage.Text = message;
        StatusMessage.Foreground = isError
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DimGray;
    }

    /// <summary>Turns a failure into a status line and a log entry rather than a crash dialog.</summary>
    private async Task Guard(Func<Task> action)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Report(ex.Message, isError: true);
            _node.Log.Error("app", ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}

/// <summary>Bare command wrapper so a keyboard shortcut can call a handler.</summary>
internal sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
