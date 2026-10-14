namespace SlideGenerator.Core.Models;

/// <summary>
/// Ergebnis einer Bildgenerierung.
/// </summary>
public sealed class ImageGenerationResult
{
    /// <summary>Eindeutige ID, identisch mit Dateiname (ohne Extension).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Generiertes Bild als rohe PNG-Bytes.</summary>
    public byte[] ImageBytes { get; set; } = Array.Empty<byte>();

    /// <summary>MIME-Type, typischerweise "image/png".</summary>
    public string ContentType { get; set; } = "image/png";

    /// <summary>Zeitpunkt der Erstellung (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Effektiv verwendeter Prompt (Master + User).</summary>
    public string ComposedPrompt { get; set; } = string.Empty;

    /// <summary>Optionaler Revised-Prompt von Azure (gpt-image-2 liefert das manchmal).</summary>
    public string? RevisedPrompt { get; set; }
}
