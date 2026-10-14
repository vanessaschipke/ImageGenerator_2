namespace SlideGenerator.Core.Models;

/// <summary>
/// Pro-User-Einstellungen, die zur Laufzeit (Settings-Seite) gepflegt werden.
/// Werden in Azure Blob Storage unter settings/{userId}/settings.json gespeichert.
/// Leere Felder fallen auf die Defaults aus appsettings.json (AzureImageConfig) zurueck.
/// </summary>
public sealed class UserSettings
{
    /// <summary>Azure-OpenAI Endpoint, z.B. "https://meine-resource.openai.azure.com".</summary>
    public string? Endpoint { get; set; }

    /// <summary>API-Key des Azure-OpenAI-Resources.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Deployment-Name, z.B. "gpt-image-2".</summary>
    public string? DeploymentName { get; set; }

    /// <summary>Chat-Deployment fuer den bearbeitbaren PPTX-Export, z.B. "gpt-5".</summary>
    public string? ChatDeploymentName { get; set; }

    /// <summary>Azure-OpenAI API-Version, z.B. "2025-04-01-preview".</summary>
    public string? ApiVersion { get; set; }
}
