# SlideGenerator

Blazor-Server-App (.NET 8) zum Generieren von Praesentations-Slides per
**Azure OpenAI gpt-image-2** (Foundry Bilder-Playground / Images API).

## Features

- Master-Prompt (Corporate Design) + individueller User-Prompt
- Optional 1 Eingabebild (image edit), max. 20 MB
- Default 1536x1024, Qualitaet `high` (waehlbar)
- Ergebnis-Bild downloadbar
- Lokale Historie / Archiv aller generierten Bilder (`wwwroot/generated/` + `App_Data/history.json`)
- R&S Corporate Design (Dunkelblau #003c7e, Hellblau #0099cc)

## Projekte

| Projekt | Inhalt |
|---|---|
| `SlideGenerator.Core`   | Models, `AzureImageService`, `ImageHistoryService`, `AzureImageConfig` |
| `SlideGenerator.Blazor` | Blazor Server UI: `Home.razor` (Generator) + `History.razor` |

## Konfiguration

`SlideGenerator.Blazor/appsettings.json`:

```json
"AzureImage": {
  "Endpoint": "https://<dein-resource>.openai.azure.com",
  "ApiKey": "",                  // leer = Managed Identity
  "DeploymentName": "gpt-image-2",
  "ApiVersion": "2025-04-01-preview",
  "DefaultSize": "1536x1024",
  "DefaultQuality": "high",
  "MasterPrompt": "..."
}
```

Den **API-Key** entweder in `appsettings.Development.json` (lokal, gitignored) oder
als Environment-Variable setzen:

```powershell
$env:AzureImage__ApiKey = "DEIN_KEY"
```

## Starten

```powershell
cd SlideGenerator
dotnet build
dotnet run --project SlideGenerator.Blazor
```

Browser oeffnet sich auf `https://localhost:7180`.

## Authentifizierung

- **API-Key** (default, einfach) -> `AzureImage:ApiKey` setzen.
- **Managed Identity / Entra ID** -> `ApiKey` leer lassen.
  `DefaultAzureCredential` nutzt dann automatisch:
    - Environment-Variablen
    - VS / VS Code / Azure CLI Login
    - Managed Identity (in Azure)
  Die Identitaet braucht die Rolle **"Cognitive Services User"** auf der OpenAI-Resource.

## API-Endpunkte (Azure-OpenAI gpt-image-2)

| Modus | Endpoint | Body |
|---|---|---|
| Nur Text | `POST /openai/deployments/{name}/images/generations` | JSON: `prompt`, `size`, `quality`, `n`, `output_format` |
| Text + Bild | `POST /openai/deployments/{name}/images/edits` | multipart/form-data mit `image`, `prompt`, `size`, `quality` |

Die Antwort enthaelt `data[0].b64_json` (Default) oder `data[0].url`.
