namespace SlideGenerator.Core.Models;

/// <summary>
/// Eingabe fuer eine Bildgenerierung (Text + optional 1 Eingangsbild).
/// </summary>
public sealed class ImageGenerationRequest
{
    /// <summary>Vom Nutzer eingegebener individueller Prompt.</summary>
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>Master-Prompt (Corporate Design) - wird vorangestellt.</summary>
    public string MasterPrompt { get; set; } = string.Empty;

    /// <summary>Optional: Eingabebild als rohe Bytes (max. 1 Bild).</summary>
    public byte[]? InputImage { get; set; }

    /// <summary>MIME-Type des Eingabebildes (z.B. "image/png").</summary>
    public string? InputImageContentType { get; set; }

    /// <summary>Original-Dateiname des Eingabebildes (fuer Multipart).</summary>
    public string? InputImageFileName { get; set; }

    /// <summary>
    /// Wenn true, wird zusaetzlich das R&amp;S Brand-Referenzbild (aus dem Blob Storage)
    /// als weiteres Referenzbild an den Edits-Endpoint gesendet, damit die Ausgabe dem
    /// Corporate Design (Farben, Bildsprache) visuell folgt. Opt-in per UI-Haekchen.
    /// </summary>
    public bool UseBrandReference { get; set; }

    /// <summary>Bildgroesse, z.B. "1536x1024".</summary>
    public string Size { get; set; } = "1536x1024";

    /// <summary>Qualitaet: low | medium | high | auto.</summary>
    public string Quality { get; set; } = "high";

    /// <summary>Liefert den effektiven, an Azure gesendeten Prompt (Master + User).</summary>
    public string ComposedPrompt =>
        string.IsNullOrWhiteSpace(MasterPrompt)
            ? UserPrompt
            : $"{MasterPrompt}\n\nAufgabe: {UserPrompt}";
}
