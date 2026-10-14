using System.Text.Json;
using Azure;
using Microsoft.Extensions.Logging;
using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Speichert generierte Bilder als Azure Blob unter images/{userId}/{id}.png
/// und pflegt eine history.json an history/{userId}/history.json.
/// Pro User strikt getrennt (Multi-Tenant-Datenisolation).
/// Im Web werden Bilder ueber einen Blob-Streaming-Endpunkt unter /generated ausgeliefert.
/// Thread-safe via SemaphoreSlim.
/// </summary>
public sealed class ImageHistoryService : IImageHistoryService
{
    private readonly IBlobStorageClientFactory _blobFactory;
    private readonly IUserContext _userContext;
    private readonly ILogger<ImageHistoryService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public ImageHistoryService(IBlobStorageClientFactory blobFactory, IUserContext userContext, ILogger<ImageHistoryService> logger)
    {
        _blobFactory = blobFactory;
        _userContext = userContext;
        _logger = logger;
    }

    private string ImagesPrefix    => $"images/{_userContext.UserId}";
    private string HistoryBlobName => $"history/{_userContext.UserId}/history.json";

    public async Task<HistoryEntry> SaveAsync(ImageGenerationResult result, ImageGenerationRequest request, CancellationToken ct = default)
    {
        var fileName = $"{result.Id}.png";
        var blobName = $"{ImagesPrefix}/{fileName}";

        var container = await _blobFactory.GetContainerClientAsync(ct);
        var blobClient = container.GetBlobClient(blobName);
        using (var ms = new MemoryStream(result.ImageBytes, writable: false))
        {
            await blobClient.UploadAsync(ms, overwrite: true, ct);
        }

        var entry = new HistoryEntry
        {
            Id = result.Id,
            CreatedAtUtc = result.CreatedAtUtc,
            UserPrompt = request.UserPrompt,
            ComposedPrompt = result.ComposedPrompt,
            RevisedPrompt = result.RevisedPrompt,
            Size = request.Size,
            Quality = request.Quality,
            HadInputImage = request.InputImage is { Length: > 0 },
            ImageRelativePath = $"generated/{fileName}"
        };

        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadInternalAsync(ct);
            all.Insert(0, entry);
            await SaveInternalAsync(all, ct);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("History-Eintrag gespeichert: User={User} Id={Id} -> {Path}",
            _userContext.UserId, entry.Id, entry.ImageRelativePath);
        return entry;
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadInternalAsync(ct);
            return all;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HistoryEntry?> FindAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadInternalAsync(ct);
            return all.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<byte[]?> ReadImageAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(entry.ImageRelativePath);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            return null;

        var container = await _blobFactory.GetContainerClientAsync(ct);
        var blobClient = container.GetBlobClient($"{ImagesPrefix}/{fileName}");
        try
        {
            var response = await blobClient.DownloadContentAsync(ct);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadInternalAsync(ct);
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;

            all.Remove(entry);
            await SaveInternalAsync(all, ct);

            var container = await _blobFactory.GetContainerClientAsync(ct);
            var blobName = $"{ImagesPrefix}/{Path.GetFileName(entry.ImageRelativePath)}";
            var blobClient = container.GetBlobClient(blobName);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateUserPromptAsync(string id, string newUserPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        newUserPrompt ??= string.Empty;

        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadInternalAsync(ct);
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;

            if (string.Equals(entry.UserPrompt, newUserPrompt, StringComparison.Ordinal))
                return;

            entry.UserPrompt = newUserPrompt;
            await SaveInternalAsync(all, ct);

            _logger.LogInformation("History-Eintrag Prompt aktualisiert: User={User} Id={Id}",
                _userContext.UserId, entry.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<HistoryEntry>> LoadInternalAsync(CancellationToken ct)
    {
        try
        {
            var container = await _blobFactory.GetContainerClientAsync(ct);
            var blobClient = container.GetBlobClient(HistoryBlobName);

            if (!await blobClient.ExistsAsync(ct))
                return new List<HistoryEntry>();

            var response = await blobClient.DownloadContentAsync(ct);
            using var stream = response.Value.Content.ToStream();
            var list = await JsonSerializer.DeserializeAsync<List<HistoryEntry>>(stream, JsonOpts, ct);
            return list ?? new List<HistoryEntry>();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new List<HistoryEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "history.json (User={User}) konnte nicht gelesen werden - starte mit leerer Liste", _userContext.UserId);
            return new List<HistoryEntry>();
        }
    }

    private async Task SaveInternalAsync(List<HistoryEntry> all, CancellationToken ct)
    {
        var container = await _blobFactory.GetContainerClientAsync(ct);
        var blobClient = container.GetBlobClient(HistoryBlobName);

        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, all, JsonOpts, ct);
        ms.Position = 0;
        await blobClient.UploadAsync(ms, overwrite: true, ct);
    }
}
