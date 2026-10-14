using Microsoft.JSInterop;

namespace SlideGenerator.Blazor.Localization;

/// <summary>
/// Haelt die aktuelle UI-Sprache pro Blazor-Circuit (Scoped).
/// Default: Englisch. Wechsel via <see cref="SetLanguageAsync"/> -> persistiert in localStorage.
/// Komponenten koennen <see cref="OnChange"/> abonnieren und werden neu gerendert.
/// </summary>
public sealed class LanguageService
{
    public const string En = "en";
    public const string De = "de";
    private const string StorageKey = "slidegen.lang";

    private readonly IJSRuntime _js;

    public LanguageService(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>Aktuelle Sprache ("en" oder "de"). Default = "en".</summary>
    public string Current { get; private set; } = En;

    /// <summary>Event fuer Komponenten, die auf Sprachwechsel reagieren wollen.</summary>
    public event Action? OnChange;

    /// <summary>
    /// Laedt die zuletzt gewaehlte Sprache aus dem Browser (localStorage).
    /// In OnAfterRenderAsync(firstRender) aufrufen, damit JS-Interop verfuegbar ist.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(stored) && (stored == En || stored == De) && stored != Current)
            {
                Current = stored;
                OnChange?.Invoke();
            }
        }
        catch
        {
            // JS-Interop nicht verfuegbar (z. B. Prerender). Default bleibt En.
        }
    }

    public async Task SetLanguageAsync(string language)
    {
        if (language != En && language != De) return;
        if (language == Current) return;

        Current = language;
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, language);
        }
        catch
        {
            // Persistenz nicht moeglich -> Sprache gilt nur fuer diese Session.
        }
        OnChange?.Invoke();
    }

    /// <summary>Uebersetzt einen Key. Faellt bei fehlender Uebersetzung auf den Key selbst zurueck.</summary>
    public string T(string key)
    {
        var dict = Current == De ? UiTexts.De : UiTexts.En;
        return dict.TryGetValue(key, out var value) ? value : key;
    }
}
