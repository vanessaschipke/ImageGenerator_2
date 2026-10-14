namespace SlideGenerator.Core.Models;

/// <summary>
/// Vom Chat-Modell gelesenes Folienlayout. Koordinaten sind Prozent der Bildfläche (0–100), Ursprung oben links.
/// </summary>
public sealed class SlideLayout
{
    public string BackgroundColor { get; set; } = "FFFFFF";

    public List<SlideElement> Elements { get; set; } = new();
}

/// <summary>
/// Ein Objekt auf der Folie. kind: text, rect, roundRect, ellipse, line, arrow, picture.
/// </summary>
public sealed class SlideElement
{
    public string Kind { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Schriftgröße in Punkt, bezogen auf eine Folie mit 13,333 Zoll Breite.</summary>
    public double FontSize { get; set; }

    /// <summary>Text- oder Linienfarbe, RRGGBB, oder leer.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>Füllfarbe, RRGGBB, oder leer.</summary>
    public string Fill { get; set; } = string.Empty;

    public bool Bold { get; set; }

    /// <summary>left, center oder right.</summary>
    public string Align { get; set; } = "left";

    /// <summary>top, middle oder bottom.</summary>
    public string VAlign { get; set; } = "top";

    /// <summary>Rahmen kommt aus dem Bild, nicht aus einer geschätzten Modellbox.</summary>
    public bool Measured { get; set; }
}
