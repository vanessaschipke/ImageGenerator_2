using Azure.Storage.Blobs;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Factory zum Erzeugen von BlobContainerClient-Instanzen.
/// Stellt sicher, dass der Container existiert.
/// </summary>
public interface IBlobStorageClientFactory
{
    /// <summary>Liefert den BlobContainerClient fuer den konfigurierten Container.</summary>
    Task<BlobContainerClient> GetContainerClientAsync(CancellationToken ct = default);
}
