using Azure;
using SlideGenerator.Blazor;
using SlideGenerator.Blazor.Localization;
using SlideGenerator.Blazor.Services;
using SlideGenerator.Core.Config;
using SlideGenerator.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureImageConfig>(
    builder.Configuration.GetSection(AzureImageConfig.SectionName));

builder.Services.Configure<AzureStorageBlobConfig>(
    builder.Configuration.GetSection(AzureStorageBlobConfig.SectionName));

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddHealthChecks();

// HttpContext fuer Easy-Auth-Header-Zugriff (UserContext)
builder.Services.AddHttpContextAccessor();

// ---- Pro-User-Speicher (Azure Blob Storage) ----
// Daten werden pro User unter blobs/{kategorie}/{userId}/... abgelegt.
// Die UserId stammt aus dem Easy-Auth-Header (X-MS-CLIENT-PRINCIPAL-ID),
// lokal greift ein Fallback ("local-dev").
builder.Services.AddScoped<IUserContext, EasyAuthUserContext>();

// Blob-Container-Factory: einmal pro App (Singleton), keinerlei User-State.
builder.Services.AddSingleton<IBlobStorageClientFactory, BlobStorageClientFactory>();

// Brand-Referenzbild (Corporate Design) - einmal laden & cachen.
builder.Services.AddSingleton<IBrandReferenceService, BrandReferenceService>();

// Pro-User-Services: Scoped, damit IUserContext pro Request neu aufgeloest wird.
builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
builder.Services.AddScoped<IImageHistoryService, ImageHistoryService>();
builder.Services.AddScoped<IAzureImageService, AzureImageService>();
builder.Services.AddScoped<ISlideLayoutService, SlideLayoutService>();
builder.Services.AddSingleton<IPptxExportService, PptxExportService>();

// UI-Sprache (Default Englisch, Umschalter EN/DE) - Scoped pro Blazor-Circuit.
builder.Services.AddScoped<LanguageService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Bilder aus Azure Blob Storage unter /generated/{fileName} streamen.
// Pfad ist pro User isoliert: User A kann nicht die Bilder von User B abrufen,
// selbst wenn er einen Blob-Namen erraet (anderer Pfad-Praefix).
app.MapGet("/generated/{fileName}", async (
    string fileName,
    IBlobStorageClientFactory factory,
    IUserContext userContext,
    CancellationToken ct) =>
{
    // Pfad-Traversal verhindern: nur reiner Dateiname, kein "../"
    if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
        return Results.BadRequest();

    var container = await factory.GetContainerClientAsync(ct);
    var blobClient = container.GetBlobClient($"images/{userContext.UserId}/{fileName}");
    try
    {
        var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
        return Results.Stream(response.Value.Content, response.Value.Details.ContentType ?? "image/png");
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
        return Results.NotFound();
    }
});

// Eine bearbeitbare Folie. gpt-5 liest das Bild, die Datei enthaelt keine zweite Folie.
app.MapGet("/export/pptx/{id}", async (
    string id,
    IImageHistoryService history,
    ISlideLayoutService layouts,
    IPptxExportService pptx,
    CancellationToken ct) =>
{
    if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c)))
        return Results.BadRequest();

    var entry = await history.FindAsync(id, ct);
    if (entry is null)
        return Results.NotFound();

    var imageBytes = await history.ReadImageAsync(entry, ct);
    if (imageBytes is null || imageBytes.Length == 0)
        return Results.NotFound();

    try
    {
        var layout = await layouts.ExtractAsync(imageBytes, ct);
        var pptxBytes = pptx.BuildPptx(imageBytes, layout, entry.UserPrompt);
        return Results.File(
            pptxBytes,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            $"slide-{id}.pptx");
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
    {
        return Results.Problem(detail: ex.Message, statusCode: 502, title: "PPTX-Export fehlgeschlagen");
    }
});

app.UseRouting();
app.UseAntiforgery();

app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
