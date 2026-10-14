using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Baut aus einem Folienlayout eine PowerPoint-Datei mit genau einer Folie.
/// Text und Flächen sind Objekte. Fotos, Icons, Logos und Diagramme sind Ausschnitte des Originals.
/// </summary>
public interface IPptxExportService
{
    /// <summary>
    /// Baut die .pptx im Speicher. Der Prompt landet als Sprechernotiz, nicht als zweite Folie.
    /// </summary>
    byte[] BuildPptx(byte[] imageBytes, SlideLayout layout, string? userPrompt = null);
}
