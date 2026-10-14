using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Pflegt die pro-User-Einstellungen (Azure-OpenAI Endpoint, Key, ...).
/// Persistiert in Azure Blob Storage unter settings/{userId}/settings.json.
/// </summary>
public interface IUserSettingsService
{
    /// <summary>Liefert die aktuell gespeicherten Settings (oder leeres Objekt, falls noch nichts gespeichert).</summary>
    Task<UserSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Speichert die uebergebenen Settings.</summary>
    Task SaveAsync(UserSettings settings, CancellationToken ct = default);
}
