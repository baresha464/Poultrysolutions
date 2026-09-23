namespace AmrPoultryFarmWeb;

/// <summary>
/// The platform's own identity (BroilIQ), shown on the login screen, in the side menu for every
/// client, in the Super Admin area and as the browser icon. A client's own name/logo/colours
/// (Settings → Branding) appear in the top strip of their screens, never in place of this.
/// </summary>
public static class AppBrand
{
    public const string Name = "BroilIQ";
    public const string Tagline = "Poultry Intelligence";

    /// <summary>Full logo with the BroilIQ wordmark — for large placements (login).</summary>
    public const string LogoUrl = "images/broiliq-logo.svg";

    /// <summary>Icon-only mark — for small placements (menu, favicon, loading screen).</summary>
    public const string MarkUrl = "images/broiliq-mark.svg";
}
