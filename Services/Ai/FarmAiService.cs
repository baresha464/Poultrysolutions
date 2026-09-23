using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services.Reports;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Role = Anthropic.Models.Messages.Role;   // the app also has a Models.Role (user roles)

namespace AmrPoultryFarmWeb.Services.Ai;

/// <summary>A photo the farmer uploaded, already downscaled in the browser.</summary>
public record BirdPhoto(byte[] Bytes, string MediaType);

public record ChatTurn(bool FromUser, string Text);

public record PossibleCondition(string Name, string Likelihood, string Evidence);

/// <summary>Structured result of a photo health check (shape enforced by structured outputs).</summary>
public class HealthAssessment
{
    public string PhotoQuality { get; set; } = "";
    public string PhotoQualityNote { get; set; } = "";
    public string Urgency { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Observations { get; set; } = new();
    public List<PossibleCondition> PossibleConditions { get; set; } = new();
    public List<string> ImmediateActions { get; set; } = new();
    public List<string> ChecksToConfirm { get; set; } = new();
    public bool CallVet { get; set; }
    public string VetNote { get; set; } = "";
    public bool NotifiableDiseaseSuspected { get; set; }
}

/// <summary>
/// Claude-powered helpers for the batch screen: a photo health check (vision + structured output)
/// and a conversational farm assistant (streaming). Both are grounded in the batch's own records
/// — the model sees the same numbers the farmer sees, so its advice fits this flock, not a generic one.
///
/// Not a diagnosis tool: prompts require differential "possible conditions", tell the model to
/// escalate to a vet, and never to prescribe antibiotic doses.
/// </summary>
public class FarmAiService
{
    private const string Model = "claude-opus-5";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ReportService reports;
    private readonly AuthService auth;
    private readonly TenantFeatureService features;
    private AnthropicClient? client;

    public FarmAiService(ReportService reports, AuthService auth, TenantFeatureService features)
    {
        this.reports = reports;
        this.auth = auth;
        this.features = features;
    }

    /// <summary>Whether the signed-in client has the AI Assistant switched on, and with a key.
    /// The feature and each client's own Anthropic key are managed by the Super Admin.</summary>
    public Task<FeatureStatus> GetStatusAsync() => features.GetStatusAsync(auth.CurrentTenantId);

    // One client per circuit, built from this tenant's own key (usage is billed to that key).
    private async Task<AnthropicClient> ClientAsync()
    {
        if (client is not null) return client;
        var tenantId = auth.CurrentTenantId ?? throw new InvalidOperationException("Not signed in.");
        var apiKey = await features.GetAiKeyAsync(tenantId)
                     ?? throw new InvalidOperationException("The AI Assistant is not enabled for your farm.");
        // Refusal fallback: if a request is declined by a safety classifier, it is retried on
        // Claude Opus 4.8 instead of failing the farmer's request outright.
        return client = new AnthropicClient
        {
            ApiKey = apiKey,
            Handlers = [new BetaRefusalFallbackHandler { Fallbacks = [new(Anthropic.Models.Messages.Model.ClaudeOpus4_8)] }],
        };
    }

    // ---------------- Photo health check ----------------

    public async Task<HealthAssessment> AssessPhotosAsync(int batchId, IReadOnlyList<BirdPhoto> photos, string farmerNotes, string language = AppLanguages.English, CancellationToken ct = default)
    {
        var client = await ClientAsync();
        var context = await BuildBatchContextAsync(batchId) ?? throw new InvalidOperationException("Batch not found.");

        var content = new List<ContentBlockParam>();
        foreach (var photo in photos)
        {
            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource { Data = Convert.ToBase64String(photo.Bytes), MediaType = photo.MediaType },
            });
        }
        content.Add(new TextBlockParam
        {
            Text = $"""
                Farm records for this flock:
                {context}

                What the farmer says they are seeing:
                {(string.IsNullOrWhiteSpace(farmerNotes) ? "(no notes given)" : farmerNotes.Trim())}

                Assess the {photos.Count} photo(s) above together with these records.
                """,
        });

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.High, Format = new JsonOutputFormat { Schema = AssessmentSchema() } },
            System = PhotoSystemPrompt + LanguageInstruction(language),
            Messages = [new() { Role = Role.User, Content = content }],
        }, ct);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException("The assistant could not assess these photos. Please consult your vet.");

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return JsonSerializer.Deserialize<HealthAssessment>(json, Json)
               ?? throw new InvalidOperationException("The assistant returned an empty assessment.");
    }

    private const string PhotoSystemPrompt = """
        You are a poultry health assistant for commercial broiler farmers in India, looking at photos
        a farmer took in the shed (live birds, droppings, litter, or post-mortem findings) alongside
        the flock's own records.

        How to assess:
        - Describe only what is actually visible. If the photos are blurry, too dark, too far away or
          don't show the relevant part (e.g. droppings, comb, eyes, legs, internal organs), say so in
          photoQuality/photoQualityNote and tell the farmer exactly what photo to take next.
        - Combine the photos with the records: age, mortality trend, weights vs standard, temperatures,
          vaccinations and remarks. Point out when the records support or contradict a condition.
        - Give a short differential of possible conditions (at most 4), each with a likelihood of
          high/medium/low and the specific evidence behind it. Never state a single definitive diagnosis —
          only a vet with a post-mortem or lab test can confirm.
        - Urgency: routine (normal/minor), monitor (watch 24-48 h), act_today (take action and call the vet
          today), emergency (rapid deaths, suspected highly contagious disease).
        - Immediate actions must be practical farm management steps (ventilation, temperature, litter,
          water, isolating sick birds, biosecurity, electrolytes). Do not prescribe antibiotics or drug
          doses — say to get a prescription from the vet instead.
        - If signs could fit a notifiable disease (highly pathogenic avian influenza, virulent Newcastle
          disease), set notifiableDiseaseSuspected, make urgency emergency, and tell the farmer to stop
          bird movement and report to the local veterinary officer / animal husbandry department immediately.
        - Write in plain, simple words a farmer can follow (English unless told otherwise below). Keep each list item to one sentence.
        """;

    private static Dictionary<string, JsonElement> AssessmentSchema()
    {
        static object Str() => new { type = "string" };
        static object StrList() => new { type = "array", items = new { type = "string" } };
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "photoQuality", "photoQualityNote", "urgency", "summary", "observations", "possibleConditions", "immediateActions", "checksToConfirm", "callVet", "vetNote", "notifiableDiseaseSuspected" },
            properties = new Dictionary<string, object>
            {
                ["photoQuality"] = new { type = "string", @enum = new[] { "good", "limited", "unusable" } },
                ["photoQualityNote"] = Str(),
                ["urgency"] = new { type = "string", @enum = new[] { "routine", "monitor", "act_today", "emergency" } },
                ["summary"] = Str(),
                ["observations"] = StrList(),
                ["possibleConditions"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "name", "likelihood", "evidence" },
                        properties = new Dictionary<string, object>
                        {
                            ["name"] = Str(),
                            ["likelihood"] = new { type = "string", @enum = new[] { "high", "medium", "low" } },
                            ["evidence"] = Str(),
                        },
                    },
                },
                ["immediateActions"] = StrList(),
                ["checksToConfirm"] = StrList(),
                ["callVet"] = new { type = "boolean" },
                ["vetNote"] = Str(),
                ["notifiableDiseaseSuspected"] = new { type = "boolean" },
            },
        };
        var element = JsonSerializer.SerializeToElement(schema);
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    // ---------------- Farm assistant chat ----------------

    /// <summary>Streams the assistant's reply, token by token, for a conversation about one batch.</summary>
    public async IAsyncEnumerable<string> ChatAsync(int batchId, IReadOnlyList<ChatTurn> conversation, string language = AppLanguages.English,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = await ClientAsync();
        var context = await BuildBatchContextAsync(batchId) ?? throw new InvalidOperationException("Batch not found.");

        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.Medium },
            // Batch records are stable for the whole conversation — cache them so follow-up
            // questions don't re-pay for the full context.
            System = new List<TextBlockParam>
            {
                new() { Text = ChatSystemPrompt + LanguageInstruction(language) + "\n\nFarm records for the batch being discussed:\n" + context, CacheControl = new CacheControlEphemeral() },
            },
            Messages = conversation.Select(t => new MessageParam { Role = t.FromUser ? Role.User : Role.Assistant, Content = t.Text }).ToList(),
        };

        await foreach (var ev in client.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
        }
    }

    private const string ChatSystemPrompt = """
        You are the farm assistant inside a broiler farm management app used by contract (integrator)
        farmers in India. You help the farmer understand their flock and decide what to do today.

        - Answer from the farm records below whenever the question is about this batch; quote the actual
          numbers (day, weight vs standard, FCR, mortality, temperature). If the records don't contain
          something, say so instead of guessing.
        - Give practical, specific shed-level advice (brooding, ventilation, feeder/drinker management,
          litter, lighting, biosecurity, lifting timing). Chicks and feed come from the integrator; the
          farmer is paid a growing charge per kg with incentives for good FCR and deductions for high mortality.
        - For health questions, list possible causes rather than a diagnosis, never give antibiotic or drug
          doses, and tell the farmer to involve their vet when birds are sick or dying above normal. For signs
          of bird flu or virulent Newcastle disease, tell them to report to the veterinary officer immediately.
        - Keep answers short and scannable: a one-line answer first, then a few "- " bullet points.
          Plain text only, no tables or headings. Use simple words (English unless told otherwise below).
        """;

    /// <summary>Reply-language rule appended to both system prompts. The records stay in English;
    /// the model translates when it answers. Fixed per user, so it doesn't break prompt caching.</summary>
    private static string LanguageInstruction(string language) => AppLanguages.Normalize(language) == AppLanguages.Telugu
        ? """


            Language: the farmer reads Telugu. Write every sentence you return in simple, everyday Telugu
            (తెలుగు script) as spoken by poultry farmers in Andhra Pradesh and Telangana — not formal or
            bookish Telugu. Keep numbers in English digits, keep units (kg, g, °C, %) and technical
            abbreviations (FCR, EEF, ND, IBD) as they are, and put the common English name in brackets
            the first time you name a disease, e.g. "గంబోరో (IBD)". Structured field values that are
            fixed choices (like urgency or likelihood) stay exactly as specified in English.
            """
        : "";

    // ---------------- Grounding context ----------------

    private async Task<string?> BuildBatchContextAsync(int batchId)
    {
        var data = await reports.LoadBatchAsync(batchId);
        if (data is null) return null;

        var b = data.Batch;
        var p = data.Perf;
        var s = BatchAnalytics.Summarize(data);
        int age = Math.Max(1, p.AgeDays);
        var sb = new StringBuilder();

        sb.AppendLine($"Batch {b.BatchCode}, {b.House?.Name} (capacity {b.House?.CapacityBirds:N0}), integrator {b.Integrator?.Name}, breed {b.Breed}.");
        sb.AppendLine($"Placed {b.PlacementDate:dd MMM yyyy} ({s.Season} season), {b.ChicksPlaced:N0} chicks. Status: {(b.Status == BatchStatus.Closed ? $"closed {b.ClosedDate:dd MMM yyyy}" : $"active, day {age}")}. Today is {DateTime.Today:dd MMM yyyy}.");
        sb.AppendLine($"Live birds {p.LiveBirds:N0}, lifted {p.BirdsLifted:N0}, dead {p.TotalMortality:N0}, culls {p.TotalCulls:N0}, livability {p.LivabilityPct:0.0}%, first-week mortality {s.FirstWeekMortalityPct:0.00}%.");
        sb.AppendLine($"Avg weight {p.AvgBodyWeightKg * 1000:0} g vs standard {FlockAdvisor.TargetWeightGmAt(age):0} g for day {age}; 7-day weight {(s.Day7WeightGm is { } d7 ? $"{d7:0} g" : "not recorded")}.");
        sb.AppendLine($"FCR {p.Fcr:0.000} vs standard {FlockAdvisor.TargetFcrAt(age):0.00}; EEF {p.Eef:0}; feed received {p.FeedReceivedKg:N0} kg, consumed {p.FeedConsumedKg:N0} kg, stock {p.FeedStockKg:N0} kg.");

        sb.AppendLine("Last 10 days (date: dead+culls, feed kg, weight g, min-max °C, humidity %, remarks):");
        foreach (var r in data.Daily.OrderByDescending(r => r.Date).Take(10).OrderBy(r => r.Date))
            sb.AppendLine($"  {r.Date:dd MMM} (day {BatchAnalytics.AgeOn(b, r.Date)}): {r.Mortality}+{r.Culls}, {r.FeedConsumedKg:0} kg, {r.AvgBodyWeightGm:0} g, {r.MinTempC:0.#}-{r.MaxTempC:0.#} °C, {r.HumidityPct:0}%{(string.IsNullOrWhiteSpace(r.Remarks) ? "" : $", \"{r.Remarks}\"")}");

        var weekly = BatchAnalytics.Weekly(data);
        if (weekly.Count > 0)
        {
            sb.AppendLine("Weekly (week: deaths, cum. mortality %, weight g vs std, cum. FCR vs std, avg °C vs target):");
            foreach (var w in weekly)
                sb.AppendLine($"  wk{w.Week}: {w.Mortality + w.Culls}, {w.CumMortalityPct:0.00}%, {w.AvgWeightGm:0} vs {w.StdWeightGm:0}, {w.CumFcr:0.000} vs {w.StdFcr:0.00}, {w.AvgTempC:0.0} vs {w.TargetTempC:0}");
        }

        sb.AppendLine(data.Health.Count == 0 ? "Health log: nothing recorded." : "Health log:");
        foreach (var h in data.Health.OrderBy(h => h.Date))
            sb.AppendLine($"  day {BatchAnalytics.AgeOn(b, h.Date)}: {h.Type} - {h.Name} ({h.Dose}, {h.Route}){(string.IsNullOrWhiteSpace(h.Remarks) ? "" : $" - {h.Remarks}")}");

        var advisor = FlockAdvisor.Analyze(b, p, data.Daily, data.Health);
        sb.AppendLine($"App's flock score: {advisor.OverallScore:0}/100 (" + string.Join(", ", advisor.Components.Select(c => $"{c.Label} {c.Score:0}")) + ").");
        return sb.ToString();
    }
}
