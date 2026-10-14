using Azure;
using Microsoft.Extensions.Logging;
using SlideGenerator.Core.Services;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Laedt das R&amp;S Brand-Referenzbild einmalig aus dem Blob Storage
/// (<c>branding/brand-reference.png</c>) und cached es prozessweit.
/// Wird von <see cref="AzureImageService"/> genutzt, um bei aktiviertem Haekchen
/// das Corporate-Design-Referenzbild an den Edits-Endpoint mitzugeben.
/// </summary>
public interface IBrandReferenceService
{
    /// <summary>
    /// Liefert die PNG-Bytes des Brand-Referenzbildes oder <c>null</c>,
    /// wenn kein Referenzbild im Blob Storage vorhanden ist.
    /// </summary>
    Task<byte[]?> GetReferenceImageAsync(CancellationToken ct = default);
}

public sealed class BrandReferenceService : IBrandReferenceService
{
    /// <summary>Blob-Pfad des Referenzbildes im konfigurierten Container.</summary>
    public const string BrandReferenceBlobName = "branding/brand-reference.png";

    private readonly IBlobStorageClientFactory _factory;
    private readonly ILogger<BrandReferenceService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private byte[]? _cached;
    private bool _loaded;

    public BrandReferenceService(IBlobStorageClientFactory factory, ILogger<BrandReferenceService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<byte[]?> GetReferenceImageAsync(CancellationToken ct = default)
    {
        if (_loaded) return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return _cached;

            var container = await _factory.GetContainerClientAsync(ct);
            var blob = container.GetBlobClient(BrandReferenceBlobName);

            try
            {
                var response = await blob.DownloadContentAsync(ct);
                _cached = response.Value.Content.ToArray();
                _logger.LogInformation("Brand-Referenzbild geladen ({Bytes} Bytes) aus '{Blob}'.",
                    _cached.Length, BrandReferenceBlobName);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _cached = null;
                _logger.LogWarning("Kein Brand-Referenzbild unter '{Blob}' gefunden.", BrandReferenceBlobName);
            }

            _loaded = true;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
