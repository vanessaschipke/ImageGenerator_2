using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Liest ein generiertes Folienbild mit gpt-5 und liefert das bearbeitbare Layout.
/// </summary>
public interface ISlideLayoutService
{
    Task<SlideLayout> ExtractAsync(byte[] imageBytes, CancellationToken ct = default);
}
