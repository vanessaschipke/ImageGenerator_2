namespace SlideGenerator.Core.Services;

/// <summary>
/// Liefert die Identitaet des aktuellen Users.
/// Wird pro HTTP-Request aus dem Easy-Auth-Header gespeist.
/// Im lokalen Entwicklungsmodus (ohne Easy Auth) wird "default" zurueckgegeben.
/// </summary>
public interface IUserContext
{
    /// <summary>
    /// Stabile, fuer Blob-Pfade sichere User-Kennung (z.B. die Azure-AD Object-ID).
    /// Nie leer.
    /// </summary>
    string UserId { get; }

    /// <summary>
    /// Anzeige-Name fuer die UI (z.B. "Max Mustermann" oder "max@rohde-schwarz.com").
    /// Kann null sein, wenn nicht verfuegbar.
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// True, wenn ein echter Easy-Auth-User erkannt wurde
    /// (false im lokalen Dev-Modus mit Fallback-User).
    /// </summary>
    bool IsAuthenticated { get; }
}
