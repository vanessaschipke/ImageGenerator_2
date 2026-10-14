using System.Text.Json;
using Azure;
using Microsoft.Extensions.Logging;
using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Azure Blob Storage-basierter UserSettingsService.
/// Pro User strikt getrennt (Pfad: settings/{userId}/settings.json).
/// Thread-safe via SemaphoreSlim.
/// </summary>
public sealed class UserSettingsService : IUserSettingsService
{
    private readonly IBlobStorageClientFactory _blobFactory;
    private readonly IUserContext _userContext;
    private readonly ILogger<UserSettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public UserSettingsService(IBlobStorageClientFactory blobFactory, IUserContext userContext, ILogger<UserSettingsService> logger)
    {
        _blobFactory = blobFactory;
        _userContext = userContext;
        _logger = logger;
    }

    private string SettingsBlobName => $"settings/{_userContext.UserId}/settings.json";

    public async Task<UserSettings> GetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var container = await _blobFactory.GetContainerClientAsync(ct);
            var blobClient = container.GetBlobClient(SettingsBlobName);

            if (!await blobClient.ExistsAsync(ct))
                return new UserSettings();

            var response = await blobClient.DownloadContentAsync(ct);
            using var stream = response.Value.Content.ToStream();
            var settings = await JsonSerializer.DeserializeAsync<UserSettings>(stream, JsonOpts, ct);
            return settings ?? new UserSettings();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new UserSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "settings.json (User={User}) konnte nicht gelesen werden - liefere leere Settings", _userContext.UserId);
            return new UserSettings();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken ct = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        await _lock.WaitAsync(ct);
        try
        {
            var container = await _blobFactory.GetContainerClientAsync(ct);
            var blobClient = container.GetBlobClient(SettingsBlobName);

            using var ms = new MemoryStream();
            await JsonSerializer.SerializeAsync(ms, settings, JsonOpts, ct);
            ms.Position = 0;
            await blobClient.UploadAsync(ms, overwrite: true, ct);
            _logger.LogInformation("UserSettings gespeichert: {Blob}", SettingsBlobName);
        }
        finally
        {
            _lock.Release();
        }
    }
}
