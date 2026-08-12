using System.Reflection;

namespace NksUsbShare.App.Web;

/// <summary>
/// The interface files, compiled into the assembly so a published binary needs nothing beside it.
/// </summary>
public static class Assets
{
    private static readonly Assembly Owner = typeof(Assets).Assembly;

    public static string IndexHtml { get; } = Read("index.html");
    public static string Css { get; } = Read("app.css");
    public static string JavaScript { get; } = Read("app.js");

    /// <summary>A USB connector drawn as a mark, in the same amber the panel uses for outgoing devices.</summary>
    public const string FaviconSvg =
        """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
          <rect width="32" height="32" rx="6" fill="#16151a"/>
          <circle cx="16" cy="25" r="3" fill="#ffb000"/>
          <path d="M16 22V7" stroke="#ffb000" stroke-width="2.4" stroke-linecap="round"/>
          <path d="M16 7l-3.2 4h6.4z" fill="#ffb000"/>
          <path d="M16 16l-5 -3v3.6" stroke="#ffb000" stroke-width="2.2" stroke-linecap="round" fill="none"/>
          <rect x="9" y="15.5" width="4" height="4" rx="1" fill="#ffb000"/>
          <path d="M16 12.5l5 -3v4" stroke="#4dd0e1" stroke-width="2.2" stroke-linecap="round" fill="none"/>
          <circle cx="21" cy="9.5" r="2.2" fill="#4dd0e1"/>
        </svg>
        """;

    private static string Read(string name)
    {
        var resourceName = $"NksUsbShare.App.Web.assets.{name}";
        using var stream = Owner.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Missing embedded resource '{resourceName}'. Available: " +
                               string.Join(", ", Owner.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
