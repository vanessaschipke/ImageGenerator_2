namespace SlideGenerator.Core.Config;

/// <summary>
/// Konfiguration für Azure Blob Storage.
/// Wird aus appsettings.json Section "AzureStorageBlob" gelesen.
/// </summary>
public sealed class AzureStorageBlobConfig
{
    public const string SectionName = "AzureStorageBlob";

    /// <summary>
    /// Azure Blob Storage Endpoint, z.B. "https://mystorageaccount.blob.core.windows.net".
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Name des Blob-Containers, z.B. "slidegenerator-data".
    /// </summary>
    public string ContainerName { get; set; } = "slidegenerator-data";

    /// <summary>
    /// Optional: User-ID für Multi-Tenant-Support (Falls leer, wird "default" verwendet).
    /// </summary>
    public string? UserId { get; set; }
}
