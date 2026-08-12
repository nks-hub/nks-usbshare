using System.Windows;
using NksUsbShare.Core.Configuration;

namespace NksUsbShare.Desktop;

public partial class SettingsDialog : Window
{
    private readonly NodeSettings _settings;

    /// <summary>True when a change was made that only takes effect once the listener restarts.</summary>
    public bool NeedsRestart { get; private set; }

    public SettingsDialog(NodeSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        NameBox.Text = settings.HostName;
        BindBox.Text = settings.LinkBindAddress;
        PortBox.Text = settings.LinkPort.ToString();
        RequireCertificateBox.IsChecked = settings.RequireClientCertificate;

        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "This computer needs a name; it is what other computers will show.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "The port must be a number between 1 and 65535.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }

        var address = BindBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            MessageBox.Show(this, $"'{address}' is not an address this computer can listen on.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            BindBox.Focus();
            return;
        }

        NeedsRestart = address != _settings.LinkBindAddress
                       || port != _settings.LinkPort
                       || RequireCertificateBox.IsChecked == true != _settings.RequireClientCertificate;

        _settings.HostName = name;
        _settings.LinkBindAddress = address;
        _settings.LinkPort = port;
        _settings.RequireClientCertificate = RequireCertificateBox.IsChecked == true;
        _settings.Save();

        DialogResult = true;
    }
}
