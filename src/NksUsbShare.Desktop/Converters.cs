using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NksUsbShare.Desktop;

public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>
/// Colours a status column: green when a device is actively shared or imported, red when
/// something failed, and the ordinary text colour otherwise.
/// </summary>
public sealed class StateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Active = new(Color.FromRgb(0x0F, 0x7B, 0x0F));
    private static readonly SolidColorBrush Problem = new(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush Normal = new(Color.FromRgb(0x1B, 0x1B, 0x1B));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x76, 0x76, 0x76));

    static StateBrushConverter()
    {
        Active.Freeze();
        Problem.Freeze();
        Normal.Freeze();
        Muted.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Shared" or "Connected" or "Imported" => Active,
            "Unavailable" or "Blocked" => Problem,
            "Not shared" or "Disconnected" => Muted,
            _ => Normal,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
