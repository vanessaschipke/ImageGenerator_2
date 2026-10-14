using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

public interface IAzureImageService
{
    /// <summary>
    /// Generiert (oder editiert, wenn ein Eingabebild mitgeliefert wird) ein Bild
    /// per Azure-OpenAI gpt-image-2 und liefert die rohen PNG-Bytes zurueck.
    /// </summary>
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default);
}
