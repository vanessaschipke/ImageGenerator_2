using System.Diagnostics;
using System.Globalization;
using System.Text;
using SkiaSharp;
using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Misst Textzeilen im Folienbild und legt sie als echte Schrift über einen Hintergrund,
/// in dem diese Zeilen entfernt sind. Icons, Linien und Verläufe bleiben Pixel.
/// </summary>
public static class SlideTextFit
{
    public const string CondensedTypeface = "Barlow Condensed";
    public const string RegularTypeface = "Barlow";
    public const string Typeface = CondensedTypeface;

    public sealed class FittedSlide
    {
        public required byte[] BackgroundPng { get; init; }
        public required List<FittedLine> Lines { get; init; }
    }

    public sealed class FittedLine
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double W { get; init; }
        public double H { get; init; }
        public string Text { get; init; } = string.Empty;
        public string Color { get; init; } = "FFFFFF";
        public bool Bold { get; init; }
        public string Typeface { get; init; } = SlideTextFit.CondensedTypeface;
        public double FontSizePt { get; init; }
        /// <summary>Zusätzlicher Zeichenabstand in Punkt. Negativ zieht die Zeile zusammen.</summary>
        public double TrackingPt { get; init; }
    }

    /// <summary>Eine gemessene Textzeile in Prozent der Bildfläche, noch ohne gelesenen Text.</summary>
    public sealed class MeasuredRegion
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double W { get; init; }
        public double H { get; init; }
        public string Color { get; init; } = "FFFFFF";
    }

    /// <summary>
    /// Misst die Textzeilen, ohne sie zu löschen. Dieselben Kästen steuern den Lese-Prompt und den Export.
    /// </summary>
    public static List<MeasuredRegion> Measure(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        var lines = FindLines(bitmap);
        return lines.Select(line => new MeasuredRegion
        {
            X = line.X * 100d / bitmap.Width,
            Y = line.Y * 100d / bitmap.Height,
            W = line.W * 100d / bitmap.Width,
            H = line.H * 100d / bitmap.Height,
            Color = line.Color
        }).ToList();
    }

    /// <summary>
    /// Prompt für genau die gemessenen Zeilen. Das Modell liefert den Wortlaut, die Geometrie bleibt aus dem Bild.
    /// </summary>
    public static string BuildInstruction(IReadOnlyList<MeasuredRegion> regions)
    {
        var text = new StringBuilder();
        text.Append("Text lines on this slide were measured from the pixels. Return exactly ");
        text.Append(regions.Count.ToString(CultureInfo.InvariantCulture));
        text.Append(" elements of kind text, in the listed order, one visual line each. ");
        text.Append("Copy every character exactly, including spaces, case, ~ ≈ × – € Ø and punctuation. Do not translate, correct, shorten, merge, or invent a line. ");
        text.Append("Use the x, y, w, h values below (percent of the image, origin top left). ");
        text.Append("fontSize is 0. color is the ink color RRGGBB. fill is empty. bold is true for headlines and big numbers. align is left and vAlign is top. ");
        text.Append("Do not return rectangles, icons, pictures, arrows or lines. backgroundColor is the page color as RRGGBB.");
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            text.AppendLine();
            text.Append("Line ");
            text.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            text.Append(": x=");
            text.Append(region.X.ToString("0.0", CultureInfo.InvariantCulture));
            text.Append(" y=");
            text.Append(region.Y.ToString("0.0", CultureInfo.InvariantCulture));
            text.Append(" w=");
            text.Append(region.W.ToString("0.0", CultureInfo.InvariantCulture));
            text.Append(" h=");
            text.Append(region.H.ToString("0.0", CultureInfo.InvariantCulture));
            text.Append(" color=");
            text.Append(region.Color);
        }
        return text.ToString();
    }

    public static FittedSlide Fit(byte[] imageBytes, SlideLayout layout)
    {
        using var bitmap = Decode(imageBytes);
        var width = bitmap.Width;
        var height = bitmap.Height;

        var lines = FindLines(bitmap);
        AssignText(lines, layout, bitmap);
        lines.RemoveAll(line => string.IsNullOrWhiteSpace(line.Text));

        var (slideCx, _) = SlideMetrics.Size(width, height);
        var pxPerPoint = SlideMetrics.PixelsPerPoint(width, slideCx);
        foreach (var line in lines)
        {
            var bold = IsBold(line, height);
            var (fontPx, extraPx, typeface) = FitFont(line.Text, line.W, line.H, bold);
            line.Bold = bold;
            line.Typeface = typeface;
            line.FontSizePt = Math.Max(6, fontPx / pxPerPoint);
            line.TrackingPt = extraPx / pxPerPoint;
        }

        var erased = Erase(bitmap, lines);
        return new FittedSlide
        {
            BackgroundPng = erased,
            Lines = lines.Select(line => ToPercent(line, width, height)).ToList()
        };
    }

    private static FittedLine ToPercent(MeasuredLine line, int width, int height) => new()
    {
        X = line.X * 100d / width,
        Y = line.Y * 100d / height,
        W = line.W * 100d / width,
        H = line.H * 100d / height,
        Text = line.Text,
        Color = line.Color,
        Bold = line.Bold,
        Typeface = line.Typeface,
        FontSizePt = line.FontSizePt,
        TrackingPt = line.TrackingPt
    };

    private static bool IsBold(MeasuredLine line, int imageHeight)
    {
        var letters = line.Text.Count(char.IsLetter);
        var uppers = line.Text.Count(char.IsUpper);
        var upperRatio = letters == 0 ? 0 : uppers / (double)letters;
        return upperRatio >= 0.55 || line.H >= imageHeight * 0.045;
    }

    private sealed class MeasuredLine
    {
        public int X, Y, W, H;
        public string Text = string.Empty;
        public string Color = "FFFFFF";
        public bool Bold;
        public string Typeface = CondensedTypeface;
        public double FontSizePt;
        public double TrackingPt;
        public List<int> Pixels = new();
        public List<Glyph> Glyphs = new();
    }

    private class Glyph
    {
        public int X, Y, W, H;
        public int Area;
        public int R, G, B;
        public bool Cyan;
        public bool Reversed;
        public List<int> Pixels = new();
    }

    private readonly record struct Icon(int X, int Y, int W, int H);

    private readonly record struct Candidate(string Text, double X, double Y, double W, double H);

    private static SKBitmap Decode(byte[] imageBytes) =>
        SKBitmap.Decode(imageBytes) ?? throw new InvalidOperationException("Bild konnte nicht gelesen werden.");

    private static List<MeasuredLine> FindLines(SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = bitmap.Pixels;
        var background = BackgroundOf(pixels, width, height);
        var distance = new int[pixels.Length];
        var foreground = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var d = ChannelDistance(pixels[i], background);
            distance[i] = d;
            foreground[i] = pixels[i].Alpha > 200 && d > 38;
        }

        var icons = new List<Icon>();
        var glyphs = new List<Glyph>();
        foreach (var glyph in Components(foreground, pixels, distance, width, height, background, reversed: false))
        {
            if (glyph.Icon)
                icons.Add(new Icon(glyph.X, glyph.Y, glyph.W, glyph.H));
            else
                glyphs.Add(glyph);
        }

        if (Luma(background) > 150)
            icons.AddRange(HeaderIcons(pixels, distance, width, height, background));
        glyphs.RemoveAll(glyph => InsideIcon(glyph, icons, width));
        if (Luma(background) > 150)
        {
            foreach (var glyph in BarLetters(pixels, distance, width, height, background))
            {
                if (InsideIcon(glyph, icons, width) || OverlapsGlyph(glyph, glyphs))
                    continue;
                glyphs.Add(glyph);
            }
        }

        icons.AddRange(StrokeIcons(glyphs));
        glyphs.RemoveAll(glyph => InsideIcon(glyph, icons, width));

        var lines = Cluster(glyphs);
        SortReading(lines);
        return lines;
    }

    private sealed class RawGlyph : Glyph
    {
        public bool Icon;
    }

    private static List<RawGlyph> Components(
        bool[] mask,
        SKColor[] pixels,
        int[] distance,
        int width,
        int height,
        SKColor background,
        bool reversed)
    {
        var found = new List<RawGlyph>();
        var seen = new bool[mask.Length];
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var minX = start % width;
            var maxX = minX;
            var minY = start / width;
            var maxY = minY;
            var area = 0;
            var samples = new List<SKColor>();
            var pix = new List<int>();
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                area++;
                pix.Add(index);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (reversed || distance[index] > 80)
                    samples.Add(pixels[index]);
                if (x > 0) TryEnqueue(mask, seen, queue, index - 1);
                if (x + 1 < width) TryEnqueue(mask, seen, queue, index + 1);
                if (y > 0) TryEnqueue(mask, seen, queue, index - width);
                if (y + 1 < height) TryEnqueue(mask, seen, queue, index + width);
            }

            var boxW = maxX - minX + 1;
            var boxH = maxY - minY + 1;
            var fill = area / (double)(boxW * boxH);
            var thin = (boxH <= 4 && boxW > 80) || (boxW <= 4 && boxH > 40);
            var huge = area > 25000 || (area > 9000 && Math.Min(boxW, boxH) > 70);
            var solidBar = fill > 0.86 && Math.Min(boxW, boxH) > 10
                && ((boxW > Math.Max(70, boxH * 3.2) && boxH < 56)
                    || (Math.Min(boxW, boxH) > 32 && Math.Max(boxW, boxH) / (double)Math.Max(1, Math.Min(boxW, boxH)) < 2.4)
                    || (fill > 0.90 && boxW > 32 && boxH > 14 && boxW > boxH * 1.4));
            var block = area > 2800 && boxH > 48 && boxW > 60;
            var bigSparse = (boxW > 70 || boxH > 70) && fill < 0.35;
            if (thin || huge || solidBar || block || bigSparse || area < 12)
            {
                if (bigSparse && boxW < width * 0.22 && boxH < height * 0.28)
                    found.Add(new RawGlyph { Icon = true, X = minX, Y = minY, W = boxW, H = boxH });
                continue;
            }

            if (!reversed && (boxW > width * 0.28 || boxH > height * 0.22))
                continue;

            var (r, g, b) = CoreColor(samples, background, reversed);
            if (!IsCyan(r, g, b) && Math.Min(r, Math.Min(g, b)) > 248)
                r = g = b = 255;
            found.Add(new RawGlyph
            {
                X = minX,
                Y = minY,
                W = boxW,
                H = boxH,
                Area = area,
                R = r,
                G = g,
                B = b,
                Cyan = IsCyan(r, g, b),
                Reversed = reversed,
                Pixels = pix
            });
        }

        return found;
    }

    /// <summary>
    /// Helle Schrift auf dunklen Balken einer hellen Seite. Sie hat die Seitenfarbe und fehlt deshalb in der Vordergrundmaske.
    /// </summary>
    private static List<Glyph> BarLetters(SKColor[] pixels, int[] distance, int width, int height, SKColor background)
    {
        var letters = new List<Glyph>();
        foreach (var glyph in Components(LightOnBars(distance, width, height), pixels, distance, width, height, background, reversed: true))
        {
            if (glyph.Icon) continue;
            var fill = glyph.Area / (double)Math.Max(1, glyph.W * glyph.H);
            if (glyph.Area < 28 || glyph.W > 28 || glyph.H > 26 || glyph.H < 10 || glyph.W < 3 || fill < 0.28)
                continue;
            letters.Add(glyph);
        }
        return letters;
    }

    /// <summary>
    /// Weiße Icons in dunklen Überschriftenbalken. Ihre Striche und Innenflächen dürfen keine Textzeile werden.
    /// </summary>
    private static List<Icon> HeaderIcons(SKColor[] pixels, int[] distance, int width, int height, SKColor background)
    {
        var parts = Components(LightOnBars(distance, width, height), pixels, distance, width, height, background, reversed: true);
        var usable = parts.Where(part => part.Area >= 12 && part.W < 400 && part.H < 160).ToList();
        var parent = Enumerable.Range(0, usable.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = i + 1; j < usable.Count; j++)
            {
                if (BoxGap(usable[i], usable[j]) > 8) continue;
                var ra = Find(i);
                var rb = Find(j);
                if (ra != rb) parent[rb] = ra;
            }
        }

        var icons = new List<Icon>();
        foreach (var group in usable.GroupBy(part => Find(usable.IndexOf(part))))
        {
            var list = group.ToList();
            var x0 = list.Min(part => part.X);
            var y0 = list.Min(part => part.Y);
            var x1 = list.Max(part => part.X + part.W);
            var y1 = list.Max(part => part.Y + part.H);
            if (!IsHeaderIcon(x1 - x0, y1 - y0, list.Sum(part => part.Area))) continue;
            icons.Add(new Icon(x0, y0, x1 - x0, y1 - y0));
        }
        return icons;
    }

    private static bool[] LightOnBars(int[] distance, int width, int height)
    {
        var dark = new bool[distance.Length];
        for (var i = 0; i < distance.Length; i++)
            dark[i] = distance[i] > 75;
        var bridged = CloseRect(dark, width, height, 10, 1);
        var bars = OpenRect(bridged, width, height, 34, 3);
        var grown = DilateRect(bars, width, height, 2, 4);
        var light = new bool[distance.Length];
        for (var i = 0; i < distance.Length; i++)
            light[i] = grown[i] && distance[i] < 60;
        return light;
    }

    /// <summary>
    /// Strich-Icons neben Beschriftungen. Buchstaben sind dichter und bleiben übrig.
    /// </summary>
    private static List<Icon> StrokeIcons(List<Glyph> glyphs)
    {
        var usable = glyphs.Where(glyph =>
        {
            var fill = glyph.Area / (double)Math.Max(1, glyph.W * glyph.H);
            return fill < 0.36 && glyph.Area >= 20 && Math.Max(glyph.W, glyph.H) <= 140 && Math.Min(glyph.W, glyph.H) >= 3;
        }).ToList();
        var parent = Enumerable.Range(0, usable.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = i + 1; j < usable.Count; j++)
            {
                if (BoxGap(usable[i], usable[j]) > 12) continue;
                var ra = Find(i);
                var rb = Find(j);
                if (ra != rb) parent[rb] = ra;
            }
        }

        var icons = new List<Icon>();
        var groups = new Dictionary<int, List<Glyph>>();
        for (var i = 0; i < usable.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<Glyph>();
                groups[root] = list;
            }
            list.Add(usable[i]);
        }

        foreach (var list in groups.Values)
        {
            var x0 = list.Min(part => part.X);
            var y0 = list.Min(part => part.Y);
            var x1 = list.Max(part => part.X + part.W);
            var y1 = list.Max(part => part.Y + part.H);
            if (IsTextRun(list, x1 - x0, y1 - y0)) continue;
            if (!IsStrokeIcon(x1 - x0, y1 - y0, list.Sum(part => part.Area))) continue;
            icons.Add(new Icon(x0, y0, x1 - x0, y1 - y0));
        }
        return icons;
    }

    private static bool IsTextRun(List<Glyph> glyphs, int boxW, int boxH)
    {
        if (glyphs.Count < 2 || boxW < boxH * 1.6) return false;
        var heights = glyphs.Select(glyph => glyph.H).OrderBy(value => value).ToList();
        var median = heights[heights.Count / 2];
        if (median < 8) return false;
        return glyphs.All(glyph => glyph.H > median * 0.6 && glyph.H < median * 1.65)
            && boxH < median * 1.45;
    }

    private static bool IsStrokeIcon(int boxW, int boxH, int area)
    {
        if (boxW < 26 || boxH < 22 || boxW > 130 || boxH > 110) return false;
        var aspect = boxW / (double)boxH;
        if (aspect < 0.40 || aspect > 2.6) return false;
        var fill = area / (double)Math.Max(1, boxW * boxH);
        return fill is >= 0.05 and <= 0.42;
    }

    private static bool IsHeaderIcon(int boxW, int boxH, int area)
    {
        if (boxW < 26 || boxH < 26 || boxW > 92 || boxH > 92) return false;
        var aspect = boxW / (double)boxH;
        if (aspect < 0.70 || aspect > 1.42) return false;
        var fill = area / (double)(boxW * boxH);
        return fill is >= 0.08 and <= 0.42;
    }

    private static int BoxGap(Glyph a, Glyph b)
    {
        var dx = 0;
        if (a.X + a.W < b.X) dx = b.X - (a.X + a.W);
        else if (b.X + b.W < a.X) dx = a.X - (b.X + b.W);
        var dy = 0;
        if (a.Y + a.H < b.Y) dy = b.Y - (a.Y + a.H);
        else if (b.Y + b.H < a.Y) dy = a.Y - (b.Y + b.H);
        return Math.Max(dx, dy);
    }

    private static bool InsideIcon(Glyph glyph, List<Icon> icons, int width)
    {
        foreach (var icon in icons)
        {
            const int pad = 4;
            var extraTop = (int)(icon.H * 0.30);
            var extraBottom = icon.X > width * 0.75 ? (int)(icon.H * 0.85) : (int)(icon.H * 0.12);
            var left = Math.Max(0, icon.X - pad);
            var top = Math.Max(0, icon.Y - extraTop);
            var right = icon.X + icon.W + pad;
            var bottom = icon.Y + icon.H + extraBottom;
            var overlapW = Math.Max(0, Math.Min(glyph.X + glyph.W, right) - Math.Max(glyph.X, left));
            var overlapH = Math.Max(0, Math.Min(glyph.Y + glyph.H, bottom) - Math.Max(glyph.Y, top));
            if (overlapW * overlapH > 0.45 * glyph.W * glyph.H)
                return true;
        }
        return false;
    }

    private static bool OverlapsGlyph(Glyph glyph, List<Glyph> hosts)
    {
        foreach (var host in hosts)
        {
            var overlapW = Math.Max(0, Math.Min(glyph.X + glyph.W, host.X + host.W) - Math.Max(glyph.X, host.X));
            var overlapH = Math.Max(0, Math.Min(glyph.Y + glyph.H, host.Y + host.H) - Math.Max(glyph.Y, host.Y));
            if (overlapW * overlapH > 0.45 * glyph.W * glyph.H)
                return true;
        }
        return false;
    }

    private static List<MeasuredLine> Cluster(List<Glyph> glyphs)
    {
        var parent = Enumerable.Range(0, glyphs.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        for (var i = 0; i < glyphs.Count; i++)
        {
            for (var j = i + 1; j < glyphs.Count; j++)
            {
                var a = glyphs[i];
                var b = glyphs[j];
                if (VerticalOverlap(a, b) < 0.55) continue;
                var limit = Math.Max(Math.Min(a.H, b.H) * 0.85, (a.W + b.W) / 2d * 1.25);
                if (limit < 10) limit = 10;
                if (HorizontalGap(a, b) <= limit)
                    Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<Glyph>>();
        for (var i = 0; i < glyphs.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<Glyph>();
                groups[root] = list;
            }
            list.Add(glyphs[i]);
        }

        var lines = new List<MeasuredLine>();
        foreach (var group in groups.Values)
        {
            group.Sort((a, b) => a.X.CompareTo(b.X));
            var parts = new List<List<Glyph>>();
            var current = new List<Glyph> { group[0] };
            var cyan = group[0].Cyan;
            foreach (var glyph in group.Skip(1))
            {
                if (glyph.Cyan != cyan && current.Count >= 2)
                {
                    parts.Add(current);
                    current = new List<Glyph>();
                    cyan = glyph.Cyan;
                }
                current.Add(glyph);
                cyan = glyph.Cyan;
            }
            parts.Add(current);

            foreach (var part in parts)
            {
                var medW = part.Select(glyph => glyph.W).OrderBy(v => v).ElementAt(part.Count / 2);
                var iconMedH = part.Select(glyph => glyph.H).OrderBy(v => v).ElementAt(part.Count / 2);
                part.RemoveAll(glyph =>
                    glyph.W > medW * 2.2
                    && glyph.H > iconMedH * 1.8
                    && glyph.W / (double)Math.Max(1, glyph.H) is > 0.7 and < 1.45);
                if (part.Count == 0) continue;
                var x0 = part.Min(g => g.X);
                var y0 = part.Min(g => g.Y);
                var x1 = part.Max(g => g.X + g.W);
                var y1 = part.Max(g => g.Y + g.H);
                var bw = x1 - x0;
                var bh = y1 - y0;
                if (bw < 8 || bh < 8) continue;
                var heights = part.Select(glyph => glyph.H).OrderBy(v => v).ToList();
                var medH = heights[heights.Count / 2];
                var glyphArea = part.Sum(glyph => Math.Max(1, glyph.Area));
                var fill = glyphArea / (double)Math.Max(1, bw * bh);
                var dots = part.Count(glyph => glyph.Area < 45 && glyph.H > 0 && glyph.W / (double)glyph.H is >= 0.55 and <= 1.7);
                if (part.Count >= 4 && dots >= 0.7 * part.Count && medH < 16 && bh < 26)
                    continue;
                if (part.Count == 1 && (bw < 11 || bh < 13))
                    continue;
                if (part.Count is >= 2 and <= 6 && bh >= 36 && bw <= bh * 2.1 && medW <= 18)
                    continue;
                if (bh > 34 && medH < 0.48 * bh && bw < bh * 2.4)
                    continue;
                if (bw * bh > 4500 && fill < 0.20 && medH < 0.62 * bh)
                    continue;
            if (Math.Min(bw, bh) > 44 && bw / (double)bh is >= 0.72 and <= 1.4 && medH < 0.62 * bh)
                continue;
            if (part.Count <= 4 && Math.Max(bw, bh) < 48 && Math.Abs(bw - bh) < Math.Max(bw, bh) * 0.4)
                continue;
            if (fill < 0.12)
                continue;
            if (fill < 0.22 && part.Count <= 4)
                continue;
            if (bw < 80 && bh < 42 && part.Count <= 3 && fill < 0.48)
                continue;
            if (bh < 12 && part.Count <= 4 && bw < 50)
                continue;
                var r = Median(part.Select(g => g.R));
                var g = Median(part.Select(g => g.G));
                var b = Median(part.Select(g => g.B));
                if (!IsCyan(r, g, b) && Math.Min(r, Math.Min(g, b)) > 248)
                    r = g = b = 255;
                lines.Add(new MeasuredLine
                {
                    X = x0,
                    Y = y0,
                    W = x1 - x0,
                    H = y1 - y0,
                    Color = $"{r:X2}{g:X2}{b:X2}",
                    Pixels = part.SelectMany(glyph => glyph.Pixels).ToList(),
                    Glyphs = part
                });
            }
        }

        return lines;
    }

    private static void AssignText(List<MeasuredLine> lines, SlideLayout layout, SKBitmap bitmap)
    {
        var candidates = Candidates(layout).ToList();
        SortReading(lines);
        var matched = GreedyAssign(lines, candidates, bitmap.Width, bitmap.Height, out _);
        if (candidates.Count == lines.Count && lines.Count > 0 && matched < lines.Count)
        {
            for (var i = 0; i < lines.Count; i++)
                lines[i].Text = candidates[i].Text;
            return;
        }

        foreach (var line in lines.Where(line => string.IsNullOrWhiteSpace(line.Text)))
        {
            var ocr = TryOcr(bitmap, line);
            if (!string.IsNullOrWhiteSpace(ocr))
                line.Text = ocr;
        }
    }

    private static int GreedyAssign(List<MeasuredLine> lines, List<Candidate> candidates, int imageW, int imageH, out double maxDistance)
    {
        maxDistance = 0;
        var pairs = new List<(double Distance, int Line, int Candidate)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lx = (line.X + line.W / 2d) * 100d / imageW;
            var ly = (line.Y + line.H / 2d) * 100d / imageH;
            var lineW = line.W * 100d / imageW;
            var lineH = line.H * 100d / imageH;
            for (var j = 0; j < candidates.Count; j++)
            {
                var candidate = candidates[j];
                if (candidate.W * candidate.H > lineW * lineH * 8 && candidate.H > lineH * 2.4)
                    continue;
                var dx = lx - (candidate.X + candidate.W / 2d);
                var dy = ly - (candidate.Y + candidate.H / 2d);
                pairs.Add((Math.Sqrt(dx * dx + dy * dy), i, j));
            }
        }

        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var usedLines = new bool[lines.Count];
        var usedCandidates = new bool[candidates.Count];
        var matched = 0;
        foreach (var (distance, line, candidate) in pairs)
        {
            if (distance > 9) break;
            if (usedLines[line] || usedCandidates[candidate]) continue;
            lines[line].Text = candidates[candidate].Text;
            usedLines[line] = true;
            usedCandidates[candidate] = true;
            matched++;
            if (distance > maxDistance) maxDistance = distance;
        }
        return matched;
    }

    private static void SortReading(List<MeasuredLine> lines)
    {
        var pending = lines.OrderBy(line => line.Y).ThenBy(line => line.X).ToList();
        var rows = new List<List<MeasuredLine>>();
        foreach (var line in pending)
        {
            var row = rows.LastOrDefault();
            if (row is not null && row.Any(other => SameRow(line, other)))
                row.Add(line);
            else
                rows.Add(new List<MeasuredLine> { line });
        }

        lines.Clear();
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.X.CompareTo(b.X));
            lines.AddRange(row);
        }
    }

    private static bool SameRow(MeasuredLine a, MeasuredLine b)
    {
        var ratio = Math.Min(a.H, b.H) / (double)Math.Max(a.H, b.H);
        if (ratio < 0.55) return false;
        var overlap = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        return overlap > 0.45 * Math.Min(a.H, b.H);
    }

    private static IEnumerable<Candidate> Candidates(SlideLayout layout)
    {
        foreach (var element in layout.Elements ?? new List<SlideElement>())
        {
            if (!string.Equals(element.Kind, "text", StringComparison.OrdinalIgnoreCase))
                continue;
            var raw = (element.Text ?? string.Empty).Replace("\r\n", "\n").Trim();
            if (raw.Length == 0) continue;
            var parts = raw.Split('\n');
            if (parts.Length == 1)
            {
                yield return new Candidate(raw, element.X, element.Y, element.W, element.H);
                continue;
            }

            var slice = element.H / parts.Length;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0) continue;
                yield return new Candidate(part, element.X, element.Y + slice * i, element.W, slice);
            }
        }
    }

    private static string? TryOcr(SKBitmap bitmap, MeasuredLine line)
    {
        var tesseract = "/usr/bin/tesseract";
        if (!File.Exists(tesseract)) return null;
        var pad = 4;
        var left = Math.Max(0, line.X - pad);
        var top = Math.Max(0, line.Y - pad);
        var right = Math.Min(bitmap.Width, line.X + line.W + pad);
        var bottom = Math.Min(bitmap.Height, line.Y + line.H + pad);
        var w = right - left;
        var h = bottom - top;
        if (w < 8 || h < 8) return null;

        using var crop = new SKBitmap(w, h);
        bitmap.ExtractSubset(crop, new SKRectI(left, top, right, bottom));
        using var scaled = crop.Resize(new SKImageInfo(w * 4, h * 4), SKFilterQuality.Medium);
        if (scaled is null) return null;
        for (var y = 0; y < scaled.Height; y++)
        {
            for (var x = 0; x < scaled.Width; x++)
            {
                var color = scaled.GetPixel(x, y);
                var luma = (color.Red + color.Green + color.Blue) / 3;
                var inkIsLight = line.Color.Length == 6 && Luma(ParseColor(line.Color)) > 150;
                var ink = inkIsLight ? luma > 90 : luma < 150;
                scaled.SetPixel(x, y, ink ? SKColors.Black : SKColors.White);
            }
        }

        var path = Path.Combine(Path.GetTempPath(), "slide-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var image = SKImage.FromBitmap(scaled))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Create(path))
                data.SaveTo(stream);

            var info = new ProcessStartInfo(tesseract, $"\"{path}\" stdout --psm 7 -l eng")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length >= 2 ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp file */ }
        }
    }

    private static (double FontPx, double ExtraPx, string Typeface) FitFont(string text, int boxW, int boxH, bool bold)
    {
        var condensedFace = bold ? Fonts.CondensedBold : Fonts.CondensedMedium;
        var regularFace = bold ? Fonts.BarlowBold : Fonts.BarlowMedium;
        var condensed = FitOne(condensedFace, text, boxW, boxH);
        var regular = FitOne(regularFace, text, boxW, boxH);
        var condensedDist = Math.Abs(condensed.ExtraPx) / Math.Max(1, condensed.FontPx);
        var regularDist = Math.Abs(regular.ExtraPx) / Math.Max(1, regular.FontPx);
        return condensedDist <= regularDist
            ? (condensed.FontPx, condensed.ExtraPx, CondensedTypeface)
            : (regular.FontPx, regular.ExtraPx, RegularTypeface);
    }

    private static (double FontPx, double ExtraPx) FitOne(SKTypeface typeface, string text, int boxW, int boxH)
    {
        double lo = 6, hi = 280, size = 20;
        for (var i = 0; i < 18; i++)
        {
            size = (lo + hi) / 2d;
            var tight = Measure(typeface, (float)size, text);
            if (tight.Height < boxH) lo = size;
            else hi = size;
        }

        var bounds = Measure(typeface, (float)size, text);
        var gaps = Math.Max(1, text.Length - 1);
        var extra = (boxW - bounds.Width) / gaps;
        var guard = 0;
        while (extra < -0.045 * (bounds.Width / Math.Max(1, text.Length)) && guard < 8)
        {
            size *= 0.96;
            bounds = Measure(typeface, (float)size, text);
            extra = (boxW - bounds.Width) / gaps;
            guard++;
        }
        return (size, extra);
    }

    private static SKRect Measure(SKTypeface typeface, float size, string text)
    {
        using var font = new SKFont(typeface, size);
        using var paint = new SKPaint(font) { IsAntialias = true };
        var bounds = new SKRect();
        paint.MeasureText(text, ref bounds);
        return bounds;
    }

    private static byte[] Erase(SKBitmap bitmap, List<MeasuredLine> lines)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var mask = new bool[width * height];
        foreach (var line in lines)
        {
            foreach (var index in line.Pixels)
                mask[index] = true;
        }
        for (var pass = 0; pass < 2; pass++)
            mask = Dilate(mask, width, height);

        var colors = bitmap.Pixels.ToArray();
        // Eine Zeile reicht: links und rechts neben jedem Buchstaben liegt der Hintergrund.
        // Dicke Überschriften würden bei einer Nachbarschaft von 18 Pixeln in der Mitte stehen bleiben.
        colors = Inpaint(colors, mask, width, height);
        colors = Inpaint(colors, mask, width, height);

        using var result = new SKBitmap(width, height);
        result.Pixels = colors;
        using var image = SKImage.FromBitmap(result);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKColor[] Inpaint(SKColor[] colors, bool[] mask, int width, int height)
    {
        var horizontal = (SKColor[])colors.Clone();
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var x = 0;
            while (x < width)
            {
                if (!mask[row + x]) { x++; continue; }
                var start = x;
                while (x < width && mask[row + x]) x++;
                FillGap(colors, horizontal, row + start, start, x - start, 1, width);
            }
        }

        var vertical = (SKColor[])colors.Clone();
        for (var x = 0; x < width; x++)
        {
            var y = 0;
            while (y < height)
            {
                if (!mask[y * width + x]) { y++; continue; }
                var start = y;
                while (y < height && mask[y * width + x]) y++;
                FillGap(colors, vertical, start * width + x, start, y - start, width, height);
            }
        }

        var result = (SKColor[])colors.Clone();
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            result[i] = new SKColor(
                (byte)((horizontal[i].Red + vertical[i].Red) / 2),
                (byte)((horizontal[i].Green + vertical[i].Green) / 2),
                (byte)((horizontal[i].Blue + vertical[i].Blue) / 2));
        }
        return result;
    }

    private static void FillGap(SKColor[] source, SKColor[] dest, int index, int start, int length, int step, int limit)
    {
        var hasLeft = start > 0;
        var hasRight = start + length < limit;
        if (!hasLeft && !hasRight) return;
        var left = hasLeft ? source[index - step] : source[index + length * step];
        var right = hasRight ? source[index + length * step] : left;
        for (var i = 0; i < length; i++)
        {
            var t = (i + 1d) / (length + 1d);
            dest[index + i * step] = new SKColor(
                (byte)(left.Red + (right.Red - left.Red) * t),
                (byte)(left.Green + (right.Green - left.Green) * t),
                (byte)(left.Blue + (right.Blue - left.Blue) * t));
        }
    }

    private static bool[] Dilate(bool[] mask, int width, int height)
    {
        var next = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var on = false;
                for (var dy = -1; dy <= 1 && !on; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= height) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= width) continue;
                        if (mask[yy * width + xx]) { on = true; break; }
                    }
                }
                next[y * width + x] = on;
            }
        }
        return next;
    }

    private static (int R, int G, int B) CoreColor(List<SKColor> samples, SKColor background, bool reversed)
    {
        if (samples.Count == 0) return (230, 230, 230);
        if (reversed)
        {
            var ordered = samples.OrderBy(color => color.Red + color.Green + color.Blue).ToList();
            var mid = ordered[ordered.Count / 2];
            return (mid.Red, mid.Green, mid.Blue);
        }

        var ranked = samples
            .Select(color => (Color: color, Distance: ChannelDistance(color, background)))
            .OrderBy(item => item.Distance)
            .ToList();
        var start = (int)(ranked.Count * 0.62);
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = start; i < ranked.Count; i++)
        {
            r += ranked[i].Color.Red;
            g += ranked[i].Color.Green;
            b += ranked[i].Color.Blue;
            n++;
        }
        if (n == 0) return (230, 230, 230);
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static int Median(IEnumerable<int> values)
    {
        var list = values.OrderBy(v => v).ToList();
        return list[list.Count / 2];
    }

    private static bool IsCyan(int r, int g, int b) =>
        b > 150 && g > 90 && r < 90 && b > r + 40 && b >= g + 5;

    private static int Luma(SKColor color) => (color.Red + color.Green + color.Blue) / 3;

    private static SKColor ParseColor(string hex)
    {
        if (hex.Length != 6) return SKColors.White;
        return new SKColor(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static bool[] DilateRect(bool[] source, int width, int height, int radiusX, int radiusY) =>
        ErodeOrDilate(ErodeOrDilate(source, width, height, radiusX, horizontal: true, erode: false), width, height, radiusY, horizontal: false, erode: false);

    private static bool[] CloseRect(bool[] source, int width, int height, int radiusX, int radiusY)
    {
        var dilated = ErodeOrDilate(ErodeOrDilate(source, width, height, radiusX, horizontal: true, erode: false), width, height, radiusY, horizontal: false, erode: false);
        return ErodeOrDilate(ErodeOrDilate(dilated, width, height, radiusX, horizontal: true, erode: true), width, height, radiusY, horizontal: false, erode: true);
    }

    private static bool[] OpenRect(bool[] source, int width, int height, int radiusX, int radiusY)
    {
        var eroded = ErodeOrDilate(ErodeOrDilate(source, width, height, radiusX, horizontal: true, erode: true), width, height, radiusY, horizontal: false, erode: true);
        return ErodeOrDilate(ErodeOrDilate(eroded, width, height, radiusX, horizontal: true, erode: false), width, height, radiusY, horizontal: false, erode: false);
    }

    private static bool[] ErodeOrDilate(bool[] source, int width, int height, int radius, bool horizontal, bool erode)
    {
        if (radius <= 0) return source;
        var dest = new bool[source.Length];
        if (horizontal)
        {
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                var prefix = new int[width + 1];
                for (var x = 0; x < width; x++)
                    prefix[x + 1] = prefix[x] + (source[row + x] ? 1 : 0);
                for (var x = 0; x < width; x++)
                {
                    var a = Math.Max(0, x - radius);
                    var b = Math.Min(width - 1, x + radius);
                    var count = prefix[b + 1] - prefix[a];
                    dest[row + x] = erode ? count == b - a + 1 : count > 0;
                }
            }
        }
        else
        {
            for (var x = 0; x < width; x++)
            {
                var prefix = new int[height + 1];
                for (var y = 0; y < height; y++)
                    prefix[y + 1] = prefix[y] + (source[y * width + x] ? 1 : 0);
                for (var y = 0; y < height; y++)
                {
                    var a = Math.Max(0, y - radius);
                    var b = Math.Min(height - 1, y + radius);
                    var count = prefix[b + 1] - prefix[a];
                    dest[y * width + x] = erode ? count == b - a + 1 : count > 0;
                }
            }
        }
        return dest;
    }

    private static double VerticalOverlap(Glyph a, Glyph b)
    {
        var top = Math.Max(a.Y, b.Y);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
        return Math.Max(0, bottom - top) / (double)Math.Max(1, Math.Min(a.H, b.H));
    }

    private static double HorizontalGap(Glyph a, Glyph b)
    {
        if (a.X + a.W < b.X) return b.X - (a.X + a.W);
        if (b.X + b.W < a.X) return a.X - (b.X + b.W);
        return 0;
    }

    private static void TryEnqueue(bool[] foreground, bool[] seen, Queue<int> queue, int index)
    {
        if (!foreground[index] || seen[index]) return;
        seen[index] = true;
        queue.Enqueue(index);
    }

    private static SKColor BackgroundOf(SKColor[] pixels, int width, int height)
    {
        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();
        var stepX = Math.Max(1, width / 48);
        var stepY = Math.Max(1, height / 36);
        for (var y = height / 12; y < height; y += stepY)
        {
            for (var x = width / 12; x < width; x += stepX)
            {
                var color = pixels[y * width + x];
                var key = (color.Red / 16 << 8) | (color.Green / 16 << 4) | (color.Blue / 16);
                var current = buckets.GetValueOrDefault(key);
                buckets[key] = (current.Count + 1, current.R + color.Red, current.G + color.Green, current.B + color.Blue);
            }
        }
        var best = buckets.Values.OrderByDescending(bucket => bucket.Count).First();
        return new SKColor((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    private static int ChannelDistance(SKColor color, SKColor other) =>
        Math.Abs(color.Red - other.Red) + Math.Abs(color.Green - other.Green) + Math.Abs(color.Blue - other.Blue);

    private static class Fonts
    {
        private static readonly List<Stream> OpenStreams = new();

        public static readonly SKTypeface CondensedBold = Load("BarlowCondensed-Bold.ttf");
        public static readonly SKTypeface CondensedMedium = Load("BarlowCondensed-Medium.ttf");
        public static readonly SKTypeface BarlowBold = Load("Barlow-Bold.ttf");
        public static readonly SKTypeface BarlowMedium = Load("Barlow-Medium.ttf");

        private static SKTypeface Load(string file)
        {
            var name = "SlideGenerator.Core.Fonts." + file;
            var stream = typeof(Fonts).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Schrift fehlt: " + name);
            OpenStreams.Add(stream);
            return SKTypeface.FromStream(stream)
                ?? throw new InvalidOperationException("Schrift konnte nicht geladen werden: " + file);
        }
    }
}

internal static class SlideMetrics
{
    public const long EmuPerInch = 914400;
    public const long LongSideEmu = 12192000;

    public static (long Cx, long Cy) Size(int imageW, int imageH)
    {
        if (imageW <= 0 || imageH <= 0)
            return (LongSideEmu, 6858000);
        if (imageW >= imageH)
            return (LongSideEmu, Math.Max(1, (long)Math.Round(LongSideEmu * (double)imageH / imageW)));
        return (Math.Max(1, (long)Math.Round(LongSideEmu * (double)imageW / imageH)), LongSideEmu);
    }

    public static double PixelsPerPoint(int imageWidth, long slideCx) =>
        imageWidth / (slideCx / (double)EmuPerInch) / 72d;
}
