namespace SlideGenerator.Core.Models;

/// <summary>
/// Persistenter Historien-Eintrag (wird in history.json in Azure Blob Storage serialisiert).
/// </summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string UserPrompt { get; set; } = string.Empty;
    public string ComposedPrompt { get; set; } = string.Empty;
    public string? RevisedPrompt { get; set; }
    public string Size { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public bool HadInputImage { get; set; }

    /// <summary>Relativer URL-Pfad fuer das Bild, z.B. "generated/abc123.png". Wird vom /generated-Endpunkt aus Blob Storage geliefert.</summary>
    public string ImageRelativePath { get; set; } = string.Empty;
}
