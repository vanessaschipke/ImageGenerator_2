using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlideGenerator.Core.Config;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Factory fuer BlobContainerClient. Verwendet DefaultAzureCredential (Managed Identity / lokale Dev-Logins)
/// und erstellt den Container automatisch, falls er noch nicht existiert.
/// Thread-safe via SemaphoreSlim.
/// </summary>
public sealed class BlobStorageClientFactory : IBlobStorageClientFactory
{
    private readonly AzureStorageBlobConfig _config;
    private readonly ILogger<BlobStorageClientFactory> _logger;
    private readonly BlobServiceClient _serviceClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private BlobContainerClient? _containerClient;

    public BlobStorageClientFactory(IOptions<AzureStorageBlobConfig> options, ILogger<BlobStorageClientFactory> logger)
    {
        _config = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_config.Endpoint))
            throw new InvalidOperationException("AzureStorageBlob:Endpoint ist nicht konfiguriert.");
        if (string.IsNullOrWhiteSpace(_config.ContainerName))
            throw new InvalidOperationException("AzureStorageBlob:ContainerName ist nicht konfiguriert.");

        _serviceClient = new BlobServiceClient(new Uri(_config.Endpoint), new DefaultAzureCredential());
    }

    public async Task<BlobContainerClient> GetContainerClientAsync(CancellationToken ct = default)
    {
        if (_containerClient is not null)
            return _containerClient;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_containerClient is not null)
                return _containerClient;

            var client = _serviceClient.GetBlobContainerClient(_config.ContainerName);
            await client.CreateIfNotExistsAsync(cancellationToken: ct);
            _logger.LogInformation("Blob-Container '{Container}' bereit.", _config.ContainerName);
            _containerClient = client;
            return _containerClient;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
