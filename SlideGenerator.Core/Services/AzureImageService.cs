using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlideGenerator.Core.Config;
using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Ruft das Azure-OpenAI Image-Deployment (gpt-image-2) auf.
///
/// Zwei Pfade:
///   1. Generations  (kein Input-Bild) -> POST /openai/deployments/{name}/images/generations
///                                        application/json
///   2. Edits        (1 Input-Bild)    -> POST /openai/deployments/{name}/images/edits
///                                        multipart/form-data
///
/// Authentifizierung:
///   * Wenn ApiKey gesetzt -> Header "api-key"
///   * sonst -> DefaultAzureCredential (Managed Identity / dev-Login)
/// </summary>
public sealed class AzureImageService : IAzureImageService
{
    private const string AadScope = "https://cognitiveservices.azure.com/.default";

    private readonly AzureImageConfig _defaults;
    private readonly IUserSettingsService _userSettings;
    private readonly IBrandReferenceService _brandReference;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureImageService> _logger;

    public AzureImageService(
        IOptions<AzureImageConfig> config,
        IUserSettingsService userSettings,
        IBrandReferenceService brandReference,
        IHttpClientFactory httpClientFactory,
        ILogger<AzureImageService> logger)
    {
        _defaults = config.Value ?? throw new ArgumentNullException(nameof(config));
        _userSettings = userSettings;
        _brandReference = brandReference;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Effektive Konfiguration: User-Settings ueberschreiben Defaults aus appsettings.
    /// </summary>
    private async Task<AzureImageConfig> ResolveConfigAsync(CancellationToken ct)
    {
        var user = await _userSettings.GetAsync(ct);
        return new AzureImageConfig
        {
            Endpoint       = !string.IsNullOrWhiteSpace(user.Endpoint)       ? user.Endpoint!       : _defaults.Endpoint,
            ApiKey         = !string.IsNullOrWhiteSpace(user.ApiKey)         ? user.ApiKey          : _defaults.ApiKey,
            DeploymentName = !string.IsNullOrWhiteSpace(user.DeploymentName) ? user.DeploymentName! : _defaults.DeploymentName,
            ApiVersion     = !string.IsNullOrWhiteSpace(user.ApiVersion)     ? user.ApiVersion!     : _defaults.ApiVersion,
            DefaultSize    = _defaults.DefaultSize,
            DefaultQuality = _defaults.DefaultQuality,
            MasterPrompt   = _defaults.MasterPrompt
        };
    }

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var cfg = await ResolveConfigAsync(ct);

        if (string.IsNullOrWhiteSpace(cfg.Endpoint))
            throw new InvalidOperationException("Azure-OpenAI Endpoint ist nicht konfiguriert. Bitte unter 'Einstellungen' eintragen.");
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException("Azure-OpenAI API-Key ist nicht konfiguriert. Bitte unter 'Einstellungen' eintragen.");

        // Brand-Referenzbild nur laden, wenn per Haekchen aktiviert.
        byte[]? brandReference = null;
        if (request.UseBrandReference)
        {
            brandReference = await _brandReference.GetReferenceImageAsync(ct);
            if (brandReference is null)
                _logger.LogWarning("Brand-Referenz angefordert, aber kein Referenzbild im Blob Storage vorhanden.");
        }

        var hasUserImage = request.InputImage is { Length: > 0 };
        var hasBrandImage = brandReference is { Length: > 0 };
        // Edits-Endpoint sobald mindestens ein Referenzbild (User oder Brand) vorliegt.
        var useEdits = hasUserImage || hasBrandImage;

        var endpoint = useEdits
            ? $"{cfg.Endpoint.TrimEnd('/')}/openai/deployments/{cfg.DeploymentName}/images/edits?api-version={cfg.ApiVersion}"
            : $"{cfg.Endpoint.TrimEnd('/')}/openai/deployments/{cfg.DeploymentName}/images/generations?api-version={cfg.ApiVersion}";

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        SetAuthHeader(http, cfg);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (useEdits)
        {
            requestMessage.Content = BuildMultipartContent(request, brandReference);
        }
        else
        {
            requestMessage.Content = BuildJsonContent(request);
        }

        _logger.LogInformation("Sende Image-Request an Azure ({Mode}, Size={Size}, Quality={Quality}, UserImg={UserImg}, BrandRef={BrandRef})",
            useEdits ? "edits" : "generations", request.Size, request.Quality, hasUserImage, hasBrandImage);

        using var response = await http.SendAsync(requestMessage, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Azure Image API Fehler {Status}: {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException($"Azure Image API Fehler {(int)response.StatusCode}: {responseBody}");
        }

        return ParseResponse(responseBody, request);
    }

    private static void SetAuthHeader(HttpClient http, AzureImageConfig cfg)
    {
        http.DefaultRequestHeaders.Remove("api-key");
        http.DefaultRequestHeaders.Add("api-key", cfg.ApiKey!);
    }

    private StringContent BuildJsonContent(ImageGenerationRequest request)
    {
        var payload = new
        {
            prompt = request.ComposedPrompt,
            size = request.Size,
            quality = request.Quality,
            n = 1,
            output_format = "png"
        };

        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private MultipartFormDataContent BuildMultipartContent(ImageGenerationRequest request, byte[]? brandReference)
    {
        var multipart = new MultipartFormDataContent();

        // gpt-image-2 Edits akzeptiert mehrere Referenzbilder als "image[]".
        // Reihenfolge: zuerst das User-Bild (Hauptmotiv), dann die Brand-Referenz (Stilvorlage).
        var hasUserImage = request.InputImage is { Length: > 0 };

        if (hasUserImage)
        {
            var contentType = string.IsNullOrWhiteSpace(request.InputImageContentType)
                ? "image/png"
                : request.InputImageContentType!;
            var fileName = string.IsNullOrWhiteSpace(request.InputImageFileName)
                ? "input.png"
                : request.InputImageFileName!;

            var imageContent = new ByteArrayContent(request.InputImage!);
            imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            multipart.Add(imageContent, "image[]", fileName);
        }

        if (brandReference is { Length: > 0 })
        {
            var brandContent = new ByteArrayContent(brandReference);
            brandContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            multipart.Add(brandContent, "image[]", "brand-reference.png");
        }

        multipart.Add(new StringContent(request.ComposedPrompt), "prompt");
        multipart.Add(new StringContent(request.Size), "size");
        multipart.Add(new StringContent(request.Quality), "quality");
        multipart.Add(new StringContent("1"), "n");
        multipart.Add(new StringContent("png"), "output_format");

        return multipart;
    }

    private ImageGenerationResult ParseResponse(string body, ImageGenerationRequest request)
    {
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            throw new InvalidOperationException("Azure-Antwort enthielt kein Bild im 'data'-Array.");

        var first = data[0];
        byte[] bytes;

        if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
        {
            bytes = Convert.FromBase64String(b64.GetString()!);
        }
        else if (first.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
        {
            using var http = _httpClientFactory.CreateClient();
            bytes = http.GetByteArrayAsync(url.GetString()!).GetAwaiter().GetResult();
        }
        else
        {
            throw new InvalidOperationException("Antwort enthaelt weder 'b64_json' noch 'url'.");
        }

        string? revised = null;
        if (first.TryGetProperty("revised_prompt", out var rp) && rp.ValueKind == JsonValueKind.String)
            revised = rp.GetString();

        return new ImageGenerationResult
        {
            ImageBytes = bytes,
            ContentType = "image/png",
            ComposedPrompt = request.ComposedPrompt,
            RevisedPrompt = revised
        };
    }
}
