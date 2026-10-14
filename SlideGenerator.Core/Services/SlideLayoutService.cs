using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlideGenerator.Core.Config;
using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Schickt das Folienbild an gpt-5 (Version 2025-08-07) und verlangt ein festes JSON-Layout.
/// Endpoint und API-Key sind dieselben wie für die Bildgenerierung. Der Deployment-Name ist getrennt.
/// </summary>
public sealed class SlideLayoutService : ISlideLayoutService
{
    private const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["backgroundColor", "elements"],
          "properties": {
            "backgroundColor": { "type": "string" },
            "elements": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "x", "y", "w", "h", "text", "fontSize", "color", "fill", "bold", "align", "vAlign"],
                "properties": {
                  "kind": { "type": "string", "enum": ["text"] },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "w": { "type": "number" },
                  "h": { "type": "number" },
                  "text": { "type": "string" },
                  "fontSize": { "type": "number" },
                  "color": { "type": "string" },
                  "fill": { "type": "string" },
                  "bold": { "type": "boolean" },
                  "align": { "type": "string", "enum": ["left", "center", "right"] },
                  "vAlign": { "type": "string", "enum": ["top", "middle", "bottom"] }
                }
              }
            }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AzureImageConfig _defaults;
    private readonly IUserSettingsService _userSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SlideLayoutService> _logger;

    public SlideLayoutService(
        IOptions<AzureImageConfig> config,
        IUserSettingsService userSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<SlideLayoutService> logger)
    {
        _defaults = config.Value ?? throw new ArgumentNullException(nameof(config));
        _userSettings = userSettings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SlideLayout> ExtractAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            throw new ArgumentException("imageBytes ist leer", nameof(imageBytes));

        var user = await _userSettings.GetAsync(ct);
        var endpoint = First(user.Endpoint, _defaults.Endpoint);
        var apiKey = First(user.ApiKey, _defaults.ApiKey);
        var deployment = First(user.ChatDeploymentName, _defaults.ChatDeploymentName);

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Azure-OpenAI Endpoint ist nicht konfiguriert.");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Azure-OpenAI API-Key ist nicht konfiguriert.");
        if (string.IsNullOrWhiteSpace(deployment))
            throw new InvalidOperationException("Chat-Deployment ist nicht konfiguriert. Unter Einstellungen gpt-5 (Version 2025-08-07) eintragen.");

        var regions = SlideTextFit.Measure(imageBytes);
        if (regions.Count == 0)
            return new SlideLayout();

        var instruction = SlideTextFit.BuildInstruction(regions);
        var visionBytes = PngCropper.ShrinkForVision(imageBytes, 1536);
        var mime = visionBytes.Length > 2 && visionBytes[0] == 0xFF && visionBytes[1] == 0xD8
            ? "image/jpeg"
            : "image/png";
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(visionBytes)}";

        using var schema = JsonDocument.Parse(Schema);
        var payload = new
        {
            model = deployment,
            messages = new object[]
            {
                new { role = "system", content = "You extract editable slide layouts. Reply only through the required JSON schema." },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = instruction },
                        new { type = "image_url", image_url = new { url = dataUrl, detail = "high" } }
                    }
                }
            },
            max_completion_tokens = 16000,
            reasoning_effort = "low",
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "slide_layout",
                    strict = true,
                    schema = schema.RootElement
                }
            }
        };

        var url = ChatCompletionsUrl(endpoint);
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Add("api-key", apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        _logger.LogInformation("Lese Folienlayout mit Deployment {Deployment}", deployment);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("GPT-5 Layout-Fehler {Status}: {Body}", response.StatusCode, body);
            throw new HttpRequestException($"GPT-5 Layout-Fehler {(int)response.StatusCode}: {body}");
        }

        var layout = ParseLayout(body);
        _logger.LogInformation("Folienlayout gelesen: {Count} Objekte", layout.Elements.Count);
        return layout;
    }

    private static SlideLayout ParseLayout(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        if (message.TryGetProperty("refusal", out var refusal)
            && refusal.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(refusal.GetString()))
        {
            throw new InvalidOperationException("GPT-5 hat die Anfrage abgelehnt: " + refusal.GetString());
        }

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("GPT-5 hat kein Layout geliefert.");

        var json = content.GetString();
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("GPT-5 hat ein leeres Layout geliefert.");

        SlideLayout? layout;
        try
        {
            layout = JsonSerializer.Deserialize<SlideLayout>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Layout konnte nicht gelesen werden: " + ex.Message);
        }
        if (layout is null)
            throw new InvalidOperationException("Layout konnte nicht gelesen werden.");
        layout.Elements ??= new List<SlideElement>();
        if (layout.Elements.Count == 0)
            throw new InvalidOperationException("GPT-5 hat keine Folienobjekte geliefert.");
        return layout;
    }

    private static string? First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;

    /// <summary>
    /// Azure-OpenAI-v1-Route, wie vom OpenAI-SDK mit base_url .../openai/v1 aufgerufen.
    /// </summary>
    private static string ChatCompletionsUrl(string endpoint)
    {
        var root = endpoint.Trim().TrimEnd('/');
        var marker = root.IndexOf("/openai/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            root = root[..marker];
        return root + "/openai/v1/chat/completions";
    }
}
