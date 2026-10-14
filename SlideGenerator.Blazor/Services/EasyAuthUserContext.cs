using Microsoft.AspNetCore.Http;
using SlideGenerator.Core.Services;

namespace SlideGenerator.Blazor.Services;

/// <summary>
/// IUserContext-Implementierung, die den User aus den
/// "X-MS-CLIENT-PRINCIPAL-*"-Headern liest, die App Service Easy Auth
/// in jeden eingehenden Request injiziert.
///
/// Lokal (ohne Easy Auth) faellt der Service auf einen festen
/// Entwickler-Benutzer ("local-dev") zurueck, damit die App auch
/// auf dem Entwicklerrechner funktioniert.
/// </summary>
public sealed class EasyAuthUserContext : IUserContext
{
    // Header-Namen, die App Service Easy Auth setzt.
    // https://learn.microsoft.com/azure/app-service/configure-authentication-user-identities
    private const string HeaderPrincipalId   = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string HeaderPrincipalName = "X-MS-CLIENT-PRINCIPAL-NAME";

    private const string FallbackUserId      = "local-dev";

    private readonly IHttpContextAccessor _accessor;

    public EasyAuthUserContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public string UserId
    {
        get
        {
            var headers = _accessor.HttpContext?.Request.Headers;
            var raw = headers is null ? null : (string?)headers[HeaderPrincipalId];
            if (string.IsNullOrWhiteSpace(raw))
                return FallbackUserId;

            return SanitizeForBlobPath(raw);
        }
    }

    public string? DisplayName
    {
        get
        {
            var headers = _accessor.HttpContext?.Request.Headers;
            var raw = headers is null ? null : (string?)headers[HeaderPrincipalName];
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
    }

    public bool IsAuthenticated
    {
        get
        {
            var headers = _accessor.HttpContext?.Request.Headers;
            var raw = headers is null ? null : (string?)headers[HeaderPrincipalId];
            return !string.IsNullOrWhiteSpace(raw);
        }
    }

    /// <summary>
    /// Macht aus einer beliebigen User-Kennung einen Wert, der gefahrlos
    /// als Teil eines Blob-Pfads verwendet werden kann.
    /// Erlaubt: a-z, 0-9, '-'. Alles andere wird zu '-'.
    /// </summary>
    private static string SanitizeForBlobPath(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        var chars = lower.Select(c =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'
                ? c
                : '-').ToArray();
        var s = new string(chars).Trim('-');
        // Falls Sanitisierung alles wegfrisst -> Fallback
        return string.IsNullOrEmpty(s) ? FallbackUserId : s;
    }
}
