using DocumentFormat.OpenXml.Packaging;
using Xunit;
using SlideGenerator.Core.Models;
using SlideGenerator.Core.Services;

namespace SlideGenerator.Core.Tests;

public class DatasheetSlideTests
{
    private static readonly string[] Lines =
    [
        "AUTOMATED DATA SHEET ANALYSIS",
        "MAXIMUM TIME SAVINGS. MINIMUM COSTS.",
        "1. TIME SAVINGS – OUR STRONGEST LEVER",
        "TIME PER DATA SHEET",
        "TIME PER DATA SHEET",
        "TIME SAVINGS",
        "MANUAL",
        "AUTOMATED",
        "PER DATA SHEET",
        "30 MIN",
        "10 MIN",
        "20 MIN",
        "BASED ON 500 DATA SHEETS PER YEAR:",
        "166 HOURS SAVED ANNUALLY",
        "2. COSTS PER REQUEST – EXTREMELY EFFICIENT",
        "COST PER REQUEST",
        "COST COMPARISON",
        "COST ADVANTAGE",
        "~$0.14",
        "Manual process: 30 min × 60 €/h",
        "≈ 30 €",
        ">200×",
        "more cost-efficient",
        "Assumptions: 500 data sheets/year | Manual time Ø 30 min | Automated time 10 min | Labor cost 60 €/h | Token pricing based on GPT-4o"
    ];

    // Grobe Positionen in Prozent, nah an den gemessenen Zeilen. Reihenfolge ist Lesereihenfolge.
    private static readonly (double X, double Y, double W, double H)[] Boxes =
    [
        (4.6, 6.3, 62.9, 6.3),
        (4.7, 14.8, 42.3, 2.9),
        (4.7, 23.2, 29.8, 2.1),
        (13.0, 30.5, 14.4, 2.1),
        (42.1, 30.5, 14.4, 2.1),
        (72.7, 30.5, 9.2, 2.1),
        (13.2, 33.7, 5.9, 2.1),
        (42.2, 33.6, 8.3, 2.1),
        (72.7, 33.6, 10.7, 2.1),
        (13.2, 37.7, 14.9, 6.0),
        (42.3, 37.8, 13.9, 5.9),
        (72.7, 37.8, 14.1, 5.9),
        (13.2, 49.2, 37.0, 3.3),
        (51.2, 49.2, 30.1, 3.2),
        (4.6, 61.7, 32.7, 2.1),
        (13.5, 67.0, 13.0, 2.1),
        (42.1, 66.9, 12.8, 2.2),
        (68.4, 67.0, 11.9, 2.1),
        (13.5, 70.9, 13.3, 7.2),
        (42.1, 71.8, 18.9, 2.3),
        (42.2, 75.6, 9.6, 5.0),
        (68.4, 71.6, 11.7, 5.5),
        (68.4, 78.6, 11.1, 2.0),
        (4.5, 91.4, 48.0, 1.6)
    ];

    [Fact]
    public void Fit_places_every_line_on_the_measured_ink()
    {
        var png = File.ReadAllBytes(Fixture());
        var fitted = SlideTextFit.Fit(png, Layout());

        Assert.Equal(Lines.Length, fitted.Lines.Count);
        Assert.Equal(Lines.OrderBy(line => line), fitted.Lines.Select(line => line.Text).OrderBy(line => line));

        var title = fitted.Lines.Single(line => line.Text.StartsWith("AUTOMATED DATA"));
        Assert.InRange(title.Y, 4, 9);
        Assert.InRange(title.FontSizePt, 35, 70);
        Assert.True(title.Bold);
        Assert.Equal("FFFFFF", title.Color);

        var hours = fitted.Lines.Single(line => line.Text.Contains("166 HOURS"));
        Assert.StartsWith("0", hours.Color);
        Assert.True(fitted.BackgroundPng.Length > 10_000);
    }

    [Fact]
    public void BuildPptx_is_one_editable_slide()
    {
        var png = File.ReadAllBytes(Fixture());
        var pptx = new PptxExportService().BuildPptx(png, Layout(), "time and cost");
        using var doc = PresentationDocument.Open(new MemoryStream(pptx), false);
        var presentation = doc.PresentationPart!;
        Assert.Single(presentation.SlideParts);
        var slide = presentation.SlideParts.Single();
        Assert.True(slide.ImageParts.Any());
        Assert.Contains("166 HOURS", slide.Slide.InnerText);
        Assert.Contains("AUTOMATED DATA SHEET ANALYSIS", slide.Slide.InnerText);
    }

    private static SlideLayout Layout()
    {
        var layout = new SlideLayout { BackgroundColor = "011127" };
        for (var i = 0; i < Lines.Length; i++)
        {
            var box = Boxes[i];
            layout.Elements.Add(new SlideElement
            {
                Kind = "text",
                Text = Lines[i],
                X = box.X,
                Y = box.Y,
                W = box.W,
                H = box.H,
                Align = "left",
                VAlign = "top"
            });
        }
        return layout;
    }

    private static string Fixture()
    {
        var dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "Fixtures", "datasheet-analysis.png");
    }
}
