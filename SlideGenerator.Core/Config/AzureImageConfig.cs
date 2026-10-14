namespace SlideGenerator.Core.Config;

/// <summary>
/// Konfiguration fuer das Azure-OpenAI-Image-Deployment (gpt-image-2).
/// Wird ueber appsettings.json (Section "AzureImage") an DI gebunden.
/// </summary>
public sealed class AzureImageConfig
{
    public const string SectionName = "AzureImage";

    /// <summary>z.B. "https://openai3csd2-rus.openai.azure.com"</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API-Key des Azure-OpenAI-Resources. Leer = Managed Identity verwenden.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Deployment-Name in Azure Foundry, z.B. "gpt-image-2".</summary>
    public string DeploymentName { get; set; } = "gpt-image-2.5-flare";

    /// <summary>
    /// Chat-Deployment, das das Bild liest und das Folien-Layout liefert.
    /// Muss gpt-5 Version 2025-08-07 auf demselben Endpoint sein.
    /// </summary>
    public string ChatDeploymentName { get; set; } = "gpt-5";

    /// <summary>Azure-OpenAI API-Version, z.B. "2025-04-01-preview".</summary>
    public string ApiVersion { get; set; } = "2025-04-01-preview";

    /// <summary>Default-Bildgroesse (Breite x Hoehe).</summary>
    public string DefaultSize { get; set; } = "1536x1024";

    /// <summary>Default-Qualitaet: low | medium | high | auto.</summary>
    public string DefaultQuality { get; set; } = "high";

    /// <summary>Master-/Corporate-Design-Prompt, der jedem User-Prompt vorangestellt wird.</summary>
    /// <remarks>
    /// Abgeleitet aus dem offiziellen "Corporate Design Manual Overview" (Okt 2024, R&amp;S GF-BS4).
    /// Farbwerte, Logo-Regeln und Bildsprache entsprechen den dortigen Basic Elements.
    /// </remarks>
    public string MasterPrompt { get; set; } =
        "Create the image in the Rohde & Schwarz corporate design: primary color dark blue #003c7e, " +
        "accent color light blue #0099cc, don\u2019t use the Rohde & Schwarz brand element and logos, " +
        "do not generate different blue filled shapes, don\u2019t create blue colored blue areas, " +
        "don\u2019t create blue lines, clear modern typography, use the font Univers cond bold for headlines, " +
        "headlines in capital letter, do not use italic fonts, professional image style, " +
        "landscape composition (3:2).";
}
