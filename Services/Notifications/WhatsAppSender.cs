using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AmrPoultryFarmWeb.Services.Notifications;

/// <summary>Configuration section "WhatsApp" (appsettings / user-secrets / env vars WhatsApp__...).
/// Only platform-wide operational settings live here. Whether a client has WhatsApp alerts, and
/// which WhatsApp Business number/token it sends from, is set per client by the Super Admin
/// (TenantFeatureService).</summary>
public class WhatsAppOptions
{
    /// <summary>"Meta" (default): send through each client's own WhatsApp Business credentials.
    /// "Log": platform-wide test mode — nothing leaves the server, messages are recorded as Simulated
    /// (use on dev/staging copies of a real database).</summary>
    public string Provider { get; set; } = "Meta";

    /// <summary>Master switch for the background alert worker.</summary>
    public bool AlertsEnabled { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 15;

    /// <summary>Farm-local time zone used for "today", summary hour and missing-entry checks.</summary>
    public string TimeZone { get; set; } = "Asia/Kolkata";

    public MetaOptions Meta { get; set; } = new();
    public TemplateOptions Templates { get; set; } = new();

    public class MetaOptions
    {
        public string ApiVersion { get; set; } = "v23.0";
    }

    /// <summary>Names of the message templates approved in WhatsApp Manager (see docs/whatsapp-templates.md).
    /// The same name is created once per language (en, te).</summary>
    public class TemplateOptions
    {
        public string Alert { get; set; } = "broiliq_alert";
        public string DailySummary { get; set; } = "broiliq_daily_summary";
    }

    public TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { TimeZone, "Asia/Kolkata", "India Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try the next id */ }
        }
        return TimeZoneInfo.Local;
    }

    public bool ForceTestMode => string.Equals(Provider, "Log", StringComparison.OrdinalIgnoreCase);
}

public record WhatsAppSendResult(bool Success, bool Simulated, string? MessageId, string? Error);

/// <summary>Sends a pre-approved WhatsApp template. Business-initiated messages (alerts, summaries)
/// must use templates — free text is only allowed inside a 24-hour window after the user writes in.</summary>
public interface IWhatsAppSender
{
    /// <summary>Sends from the client's own WhatsApp Business number. With no credentials (or in
    /// platform test mode) the message is only logged and reported as Simulated.</summary>
    Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppCredentials? credentials, string toNumber, string templateName,
        string languageCode, IReadOnlyList<string> bodyParameters, CancellationToken ct = default);
}

public static class WhatsAppText
{
    /// <summary>Template variables may not contain newlines/tabs or more than 4 consecutive spaces,
    /// and are capped in length — clean every value before it goes out.</summary>
    public static string CleanParameter(string value)
    {
        var v = Regex.Replace(value ?? "", @"[\r\n\t]+", " · ");
        v = Regex.Replace(v, @" {2,}", " ").Trim();
        if (v.Length == 0) v = "-";
        return v.Length > 900 ? v[..897] + "..." : v;
    }

    /// <summary>Normalises a typed phone number to WhatsApp's digits-only international format.
    /// A bare 10-digit number is treated as Indian (+91). Returns null if it can't be valid.</summary>
    public static string? NormalizeNumber(string? input)
    {
        var digits = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        if (digits.Length == 10) digits = "91" + digits;
        return digits.Length is >= 11 and <= 15 ? digits : null;
    }

    public static string Mask(string number) =>
        number.Length <= 4 ? number : new string('•', number.Length - 4) + number[^4..];
}

/// <summary>WhatsApp Business Cloud API (Meta): POST /{version}/{phone-number-id}/messages, sent with
/// the client's own phone-number id and token. Without credentials, or in platform test mode, it only
/// logs what would have been sent.</summary>
public class WhatsAppSender : IWhatsAppSender
{
    private readonly IHttpClientFactory httpFactory;
    private readonly WhatsAppOptions options;
    private readonly ILogger<WhatsAppSender> log;

    public WhatsAppSender(IHttpClientFactory httpFactory, IOptions<WhatsAppOptions> options, ILogger<WhatsAppSender> log)
    {
        this.httpFactory = httpFactory;
        this.options = options.Value;
        this.log = log;
    }

    public async Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppCredentials? credentials, string toNumber, string templateName,
        string languageCode, IReadOnlyList<string> bodyParameters, CancellationToken ct = default)
    {
        if (credentials is null || options.ForceTestMode)
        {
            log.LogInformation("[WhatsApp test mode] to {To} template {Template}/{Lang}: {Params}",
                WhatsAppText.Mask(toNumber), templateName, languageCode, string.Join(" | ", bodyParameters));
            return new WhatsAppSendResult(true, true, null, null);
        }

        var http = httpFactory.CreateClient(nameof(WhatsAppSender));
        var url = $"https://graph.facebook.com/{options.Meta.ApiVersion}/{Uri.EscapeDataString(credentials.PhoneNumberId)}/messages";
        var payload = new
        {
            messaging_product = "whatsapp",
            to = toNumber,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = languageCode },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = bodyParameters.Select(p => new { type = "text", text = WhatsAppText.CleanParameter(p) }).ToArray(),
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

            if (response.IsSuccessStatusCode
                && json.RootElement.TryGetProperty("messages", out var messages)
                && messages.GetArrayLength() > 0)
            {
                return new WhatsAppSendResult(true, false, messages[0].GetProperty("id").GetString(), null);
            }

            var error = json.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                ? m.GetString()
                : $"HTTP {(int)response.StatusCode}";
            log.LogWarning("WhatsApp send to {To} failed: {Error}", WhatsAppText.Mask(toNumber), error);
            return new WhatsAppSendResult(false, false, null, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "WhatsApp send to {To} failed", WhatsAppText.Mask(toNumber));
            return new WhatsAppSendResult(false, false, null, ex.Message);
        }
    }
}
