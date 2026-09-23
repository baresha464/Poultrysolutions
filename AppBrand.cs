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

    // ---- Public contact details (landing page) ----
    public const string PhoneDisplay = "+91 90307 05320";
    public const string PhoneLink = "tel:+919030705320";
    public const string Email = "info@broillq.com";
    public const string WhatsAppDisplay = "+91 90307 05320";
    public const string WhatsAppNumber = "919030705320";   // digits only, for wa.me links

    /// <summary>Opens a WhatsApp chat with the BroilIQ team, with a short opening message.</summary>
    public const string WhatsAppLink = "https://wa.me/919030705320?text=Hi%20BroilIQ%2C%20I%27d%20like%20to%20know%20more%20about%20the%20app%20for%20my%20farm.";
}
