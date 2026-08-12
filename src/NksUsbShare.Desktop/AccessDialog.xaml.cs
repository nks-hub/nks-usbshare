using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using NksUsbShare.Core.Configuration;
using NksUsbShare.Core.Security;

namespace NksUsbShare.Desktop;

/// <summary>A known client, with whether this share lets it in.</summary>
public sealed class ClientAccessRow : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Fingerprint { get; init; }
    public required string LastSeen { get; init; }

    private bool _allowed;
    public bool Allowed
    {
        get => _allowed;
        set
        {
            if (_allowed == value) return;
            _allowed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Allowed)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Chooses which computers a shared device is offered to. An empty list means anyone who knows
/// the pairing token, which is the sensible default for a single-user setup; naming computers
/// narrows it to those keys.
/// </summary>
public partial class AccessDialog : Window
{
    private readonly DeviceShare _share;
    private readonly ObservableCollection<ClientAccessRow> _clients = [];

    public AccessDialog(DeviceShare share, IReadOnlyList<KnownClient> knownClients, string deviceName)
    {
        InitializeComponent();
        _share = share;

        DeviceLabel.Text = deviceName;
        ClientList.ItemsSource = _clients;

        foreach (var client in knownClients)
        {
            _clients.Add(new ClientAccessRow
            {
                Name = client.Name.Length > 0 ? client.Name : "unnamed",
                Fingerprint = client.Fingerprint,
                LastSeen = client.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Allowed = share.AllowedClients.Any(a => NodeIdentity.FingerprintsMatch(a, client.Fingerprint)),
            });
        }

        var restricted = share.AllowedClients.Count > 0;
        AnyoneOption.IsChecked = !restricted;
        ChosenOption.IsChecked = restricted;

        if (_clients.Count == 0)
        {
            EmptyNote.Visibility = Visibility.Visible;
            ChosenOption.IsEnabled = false;
        }

        UpdateListState();
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e) => UpdateListState();

    private void UpdateListState()
    {
        // The list only means anything when the share is restricted.
        if (ClientList is not null) ClientList.IsEnabled = ChosenOption.IsChecked == true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (ChosenOption.IsChecked == true)
        {
            var chosen = _clients.Where(c => c.Allowed).Select(c => c.Fingerprint).ToList();
            if (chosen.Count == 0)
            {
                MessageBox.Show(this,
                    "No computer is ticked, so nobody could use the device.\n\n" +
                    "Tick at least one, or choose the first option to let any paired computer in.",
                    "Who can use this device", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _share.AllowedClients = chosen;
        }
        else
        {
            _share.AllowedClients = [];
        }

        DialogResult = true;
    }
}
