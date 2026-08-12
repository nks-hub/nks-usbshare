using System.Windows;
using NksUsbShare.Core.Configuration;

namespace NksUsbShare.Desktop;

public partial class AddHostDialog : Window
{
    public string Address { get; private set; } = "";
    public int Port { get; private set; } = DefaultPorts.Link;
    public string Token { get; private set; } = "";

    public AddHostDialog()
    {
        InitializeComponent();
        PortBox.Text = DefaultPorts.Link.ToString();
        Loaded += (_, _) => AddressBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        var token = TokenBox.Text.Trim();

        if (address.Length == 0)
        {
            MessageBox.Show(this, "Enter the address of the other computer.", "Add computer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            AddressBox.Focus();
            return;
        }

        if (token.Length == 0)
        {
            MessageBox.Show(this, "Enter the pairing token shown by the other computer.", "Add computer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TokenBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "The port must be a number between 1 and 65535.", "Add computer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }

        Address = address;
        Port = port;
        Token = token;
        DialogResult = true;
    }
}
