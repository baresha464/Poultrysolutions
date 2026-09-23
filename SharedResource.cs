using System.Globalization;

namespace AmrPoultryFarmWeb;

/// <summary>
/// Marker type for the app's single translation table: <c>Resources/SharedResource.te.resx</c>.
/// Keys are the English text itself, so English needs no resource file (a missing key falls back
/// to the key) and an untranslated string still shows up readable instead of as a code.
/// Inject <c>IStringLocalizer&lt;SharedResource&gt;</c> (available as <c>L</c> in every component).
/// </summary>
public sealed class SharedResource
{
}

/// <summary>The languages a user can pick. Number/date formatting always stays en-IN (₹1,20,000);
/// only the UI language changes, so numeric input parsing never depends on the language choice.</summary>
public static class AppLanguages
{
    public const string English = "en";
    public const string Telugu = "te";

    public static readonly CultureInfo FormatCulture = CultureInfo.GetCultureInfo("en-IN");

    public static readonly CultureInfo[] UiCultures =
    {
        CultureInfo.GetCultureInfo("en-IN"),
        CultureInfo.GetCultureInfo("te-IN"),
    };

    public static string Normalize(string? code) => code == Telugu ? Telugu : English;

    public static CultureInfo UiCultureFor(string code) => Normalize(code) == Telugu ? UiCultures[1] : UiCultures[0];

    /// <summary>Two-letter code of the language the current request/circuit is using.</summary>
    public static string Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Telugu ? Telugu : English;
}
