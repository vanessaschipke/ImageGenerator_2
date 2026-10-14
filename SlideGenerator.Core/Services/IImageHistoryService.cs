using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

public interface IImageHistoryService
{
    /// <summary>Speichert das Bild in Azure Blob Storage und legt einen History-Eintrag an.</summary>
    Task<HistoryEntry> SaveAsync(ImageGenerationResult result, ImageGenerationRequest request, CancellationToken ct = default);

    /// <summary>Liefert alle Eintraege, neueste zuerst.</summary>
    Task<IReadOnlyList<HistoryEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Sucht einen Eintrag des aktuellen Users anhand der Id.</summary>
    Task<HistoryEntry?> FindAsync(string id, CancellationToken ct = default);

    /// <summary>Laedt die PNG-Bytes eines History-Eintrags aus dem Blob Storage.</summary>
    Task<byte[]?> ReadImageAsync(HistoryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Aktualisiert den User-Prompt eines bestehenden History-Eintrags. Das Bild wird nicht neu generiert.
    /// </summary>
    Task UpdateUserPromptAsync(string id, string newUserPrompt, CancellationToken ct = default);

    /// <summary>Loescht einen Eintrag samt Bilddatei.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
