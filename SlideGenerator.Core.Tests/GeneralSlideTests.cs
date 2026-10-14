using DocumentFormat.OpenXml.Packaging;
using Xunit;
using SlideGenerator.Core.Models;
using SlideGenerator.Core.Services;

namespace SlideGenerator.Core.Tests;

public class GeneralSlideTests
{
    [Fact]
    public void Instruction_lists_every_measured_line()
    {
        var regions = new List<SlideTextFit.MeasuredRegion>
        {
            new() { X = 4.6, Y = 6.4, W = 62.9, H = 6.2, Color = "FFFFFF" },
            new() { X = 4.7, Y = 14.8, W = 42.3, H = 2.9, Color = "0C9CDE" }
        };

        var instruction = SlideTextFit.BuildInstruction(regions);

        Assert.Contains("exactly 2", instruction);
        Assert.Contains("Line 1: x=4.6 y=6.4 w=62.9 h=6.2 color=FFFFFF", instruction);
        Assert.Contains("Line 2: x=4.7 y=14.8 w=42.3 h=2.9 color=0C9CDE", instruction);
        Assert.Contains("Do not return rectangles", instruction);
    }

    [Fact]
    public void Datasheet_measure_matches_the_visible_lines()
    {
        var regions = SlideTextFit.Measure(File.ReadAllBytes(Fixture("datasheet-analysis.png")));

        Assert.InRange(regions.Count, 22, 28);
        Assert.Contains(regions, region => region.Y is > 4 and < 10 && region.W > 50 && region.Color == "FFFFFF");
        Assert.Contains(regions, region => region.Y > 85 && region.W > 30);
    }

    [Fact]
    public void Architecture_finds_white_text_on_the_navy_headers()
    {
        var regions = SlideTextFit.Measure(File.ReadAllBytes(Fixture("08-arch.png")));

        var headers = regions.Where(region =>
            region.Y is > 18 and < 32
            && region.W > 6
            && region.H > 1.5
            && IsLight(region.Color)).ToList();

        Assert.True(headers.Count >= 3, "Helle Kopfzeilen: " + string.Join("; ", regions.Select(Describe)));
        Assert.Contains(regions, region => region.Y < 12 && region.W > 20 && !IsLight(region.Color));
    }

    [Fact]
    public void Aufbau_keeps_labels_and_drops_chart_bars()
    {
        var regions = SlideTextFit.Measure(File.ReadAllBytes(Fixture("03-aufbau.png")));

        Assert.InRange(regions.Count, 20, 70);
        Assert.Contains(regions, region => region.Y < 12 && region.H > 4 && !IsLight(region.Color));
        var bars = regions.Where(region => region.X < 48 && region.Y is > 38 and < 62 && region.W > 10 && region.H < 4).ToList();
        Assert.True(bars.Count == 0, "Balken als Text: " + string.Join("; ", bars.Select(Describe)));
    }

    [Fact]
    public void Phasen_does_not_box_the_particle_wave()
    {
        var regions = SlideTextFit.Measure(File.ReadAllBytes(Fixture("07-phasen.png")));

        Assert.InRange(regions.Count, 12, 55);
        var wave = regions.Where(region => region.Y < 40 && region.X > 55).ToList();
        Assert.True(wave.Count <= 6, "Welle als Text: " + string.Join("; ", wave.Select(Describe)));
        Assert.Contains(regions, region => region.Y > 70 && region.W > 4);
    }

    [Fact]
    public void Pptx_embeds_both_font_families()
    {
        var png = File.ReadAllBytes(Fixture("datasheet-analysis.png"));
        var pptx = new PptxExportService().BuildPptx(png, new SlideLayout());
        using var doc = PresentationDocument.Open(new MemoryStream(pptx), false);
        var names = doc.PresentationPart!.Presentation.EmbeddedFontList!
            .Elements<DocumentFormat.OpenXml.Presentation.EmbeddedFont>()
            .Select(font => font.Font?.Typeface?.Value)
            .ToList();
        Assert.Contains("Barlow Condensed", names);
        Assert.Contains("Barlow", names);
    }

    private static bool IsLight(string color)
    {
        if (color.Length != 6) return false;
        var r = Convert.ToInt32(color[..2], 16);
        var g = Convert.ToInt32(color.Substring(2, 2), 16);
        var b = Convert.ToInt32(color.Substring(4, 2), 16);
        return (r + g + b) / 3 > 180;
    }

    private static string Describe(SlideTextFit.MeasuredRegion region) =>
        $"{region.X:0.0},{region.Y:0.0} {region.W:0.0}x{region.H:0.0} {region.Color}";

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
