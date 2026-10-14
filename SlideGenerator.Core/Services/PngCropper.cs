using SkiaSharp;
using SlideGenerator.Core.Models;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Schneidet Prozent-Rechtecke aus dem Originalbild. Die Ausschnitte werden als Bilder in die Folie gelegt.
/// </summary>
public static class PngCropper
{
    /// <summary>
    /// Verkleinert das Bild für den Vision-Aufruf. Die Folie wird weiter aus dem Original beschnitten.
    /// </summary>
    public static byte[] ShrinkForVision(byte[] imageBytes, int maxEdge = 1280)
    {
        using var bitmap = Decode(imageBytes);
        if (bitmap.Width <= maxEdge && bitmap.Height <= maxEdge)
            return imageBytes;

        var scale = maxEdge / (double)Math.Max(bitmap.Width, bitmap.Height);
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = bitmap.Resize(new SKImageInfo(width, height), SKFilterQuality.Medium)
            ?? throw new InvalidOperationException("Bild konnte nicht verkleinert werden.");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    /// <summary>Ein Icon inklusive Kreis oder Rahmen, in Prozent der Bildfläche.</summary>
    public readonly record struct PercentBox(double X, double Y, double W, double H);

    /// <summary>Schreibt Farbklassen und lange Kanten, damit Rahmen im Bild gefunden werden können.</summary>
    public static string Probe(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = bitmap.Pixels;
        var background = BackgroundOf(pixels, width, height);
        var navy = 0;
        var border = 0;
        var ink = 0;
        var other = 0;
        SKColor navySample = default, borderSample = default, inkSample = default;
        for (var i = 0; i < pixels.Length; i += 3)
        {
            var color = pixels[i];
            var kind = Classify(color, background);
            if (kind == 1) { navy++; if (navy == 40) navySample = color; }
            else if (kind == 2) { border++; if (border == 40) borderSample = color; }
            else if (kind == 3) { ink++; if (ink == 40) inkSample = color; }
            else if (kind == 4) other++;
        }

        var buckets = new Dictionary<int, int>();
        for (var i = 0; i < pixels.Length; i += 2)
        {
            var color = pixels[i];
            if (Classify(color, background) != 4) continue;
            var key = (color.Red / 16 << 8) | (color.Green / 16 << 4) | (color.Blue / 16);
            buckets[key] = buckets.GetValueOrDefault(key) + 1;
        }

        var top = string.Join(" ", buckets.OrderByDescending(pair => pair.Value).Take(8)
            .Select(pair => $"{pair.Key >> 8:X}{(pair.Key >> 4) & 15:X}{pair.Key & 15:X}:{pair.Value}"));
        return $"size={width}x{height} bg={background.Red:X2}{background.Green:X2}{background.Blue:X2} " +
               $"navy={navy}:{navySample.Red:X2}{navySample.Green:X2}{navySample.Blue:X2} " +
               $"border={border}:{borderSample.Red:X2}{borderSample.Green:X2}{borderSample.Blue:X2} " +
               $"ink={ink}:{inkSample.Red:X2}{inkSample.Green:X2}{inkSample.Blue:X2} other={other} buckets={top}";
    }

    /// <summary>0 Hintergrund, 1 Navy-Fläche, 2 Rahmen, 3 dunkle Schrift, 4 sonstiges.</summary>
    private static int Classify(SKColor color, SKColor background)
    {
        if (color.Alpha < 200) return 0;
        var distance = Math.Abs(color.Red - background.Red)
            + Math.Abs(color.Green - background.Green)
            + Math.Abs(color.Blue - background.Blue);
        if (distance < 22) return 0;
        var luma = (color.Red + color.Green + color.Blue) / 3;
        var navy = color.Red < 50 && color.Green < 110 && color.Blue > 55 && color.Blue + 15 > color.Green && luma < 130;
        if (navy) return 1;
        if (luma < 95) return 3;
        var blueish = color.Blue + 8 >= color.Red && color.Blue + 12 >= color.Green;
        if (blueish && distance is >= 22 and <= 170 && luma >= 120) return 2;
        return 4;
    }

    /// <summary>
    /// Findet kompakte Icon-Flächen (Kreis mit Symbol, Gauge). Karten und Fließtext bleiben außen vor.
    /// </summary>
    public static List<PercentBox> FindIconBadges(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width < 32 || height < 32) return new List<PercentBox>();

        var pixels = bitmap.Pixels;
        var background = BackgroundOf(pixels, width, height);
        var mark = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            mark[i] = IsBadgePixel(pixels[i], background);

        var badges = new List<PercentBox>();
        CollectBadges(badges, mark, pixels, width, height, disksAndStrokes: true);
        return badges;
    }

    private static void CollectBadges(
        List<PercentBox> badges,
        bool[] mark,
        SKColor[] pixels,
        int width,
        int height,
        bool disksAndStrokes)
    {
        var seen = new bool[mark.Length];
        var queue = new Queue<int>();
        for (var start = 0; start < mark.Length; start++)
        {
            if (!mark[start] || seen[start]) continue;

            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var left = start % width;
            var right = left;
            var top = start / width;
            var bottom = top;
            var count = 0;
            long red = 0;
            long green = 0;
            long blue = 0;

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                count++;
                var color = pixels[index];
                red += color.Red;
                green += color.Green;
                blue += color.Blue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;

                if (x > 0) TryEnqueue(mark, seen, queue, index - 1);
                if (x + 1 < width) TryEnqueue(mark, seen, queue, index + 1);
                if (y > 0) TryEnqueue(mark, seen, queue, index - width);
                if (y + 1 < height) TryEnqueue(mark, seen, queue, index + width);
            }

            var boxW = right - left + 1;
            var boxH = bottom - top + 1;
            var aspect = boxW / (double)Math.Max(1, boxH);
            var fill = count / (double)(boxW * boxH);
            var meanR = red / (double)count;
            var meanG = green / (double)count;
            var meanB = blue / (double)count;
            var mean = (meanR + meanG + meanB) / 3d;
            var wPct = boxW * 100d / width;
            var hPct = boxH * 100d / height;
            var brightBlue = meanB > 140 && meanG > 70;

            var keep = disksAndStrokes
                ? IsIconOnLight(wPct, hPct, aspect, fill, mean, brightBlue)
                : wPct is >= 1.5 and <= 6.5 && hPct is >= 1.5 and <= 8 && aspect is >= 0.6 and <= 1.6 && fill >= 0.1;
            if (!keep) continue;
            if (badges.Any(existing => Overlap(left * 100d / width, top * 100d / height, wPct, hPct, existing) > wPct * hPct * 0.4))
                continue;

            const int pad = 3;
            var x0 = Math.Max(0, left - pad);
            var y0 = Math.Max(0, top - pad);
            var x1 = Math.Min(width - 1, right + pad);
            var y1 = Math.Min(height - 1, bottom + pad);
            badges.Add(new PercentBox(
                x0 * 100d / width,
                y0 * 100d / height,
                (x1 - x0 + 1) * 100d / width,
                (y1 - y0 + 1) * 100d / height));
        }
    }

    private static bool IsIconOnLight(double wPct, double hPct, double aspect, double fill, double mean, bool blueInk)
    {
        if (wPct > 12 || hPct > 16) return false;
        if (aspect is < 0.62 or > 1.55) return false;
        var disk = wPct is >= 2.4 and <= 10 && hPct is >= 4.2 and <= 12 && fill >= 0.38 && mean >= 105;
        var stroke = blueInk && wPct is >= 2.2 and <= 7.5 && hPct is >= 4.0 and <= 8.5 && fill is >= 0.12 and < 0.5 && mean is >= 90 and <= 210;
        var gauge = blueInk && wPct is >= 10 and <= 22 && hPct is >= 14 and <= 30 && aspect is >= 0.8 and <= 1.25 && fill is >= 0.04 and <= 0.4;
        return disk || stroke || gauge;
    }

    /// <summary>
    /// Zieht einen zu kleinen Bildrahmen auf das Icon auf, das er trifft.
    /// </summary>
    public static bool FitIconBounds(byte[] imageBytes, SlideElement element)
    {
        if (element.W > 28 && element.H > 28) return false;

        PercentBox? best = null;
        var bestScore = 0d;
        foreach (var badge in FindIconBadges(imageBytes))
        {
            var score = Overlap(element.X, element.Y, element.W, element.H, badge);
            if (score > bestScore)
            {
                bestScore = score;
                best = badge;
            }
        }

        if (best is not { } box || bestScore < Math.Min(element.W * element.H, box.W * box.H) * 0.15d)
            return false;

        element.X = box.X;
        element.Y = box.Y;
        element.W = box.W;
        element.H = box.H;
        return true;
    }

    /// <summary>
    /// Liefert einen PNG-Ausschnitt. x, y, w, h sind Prozent der Bildfläche.
    /// </summary>
    public static byte[]? CropPercent(byte[] imageBytes, double x, double y, double w, double h)
    {
        using var bitmap = Decode(imageBytes);
        if (bitmap.Width < 2 || bitmap.Height < 2) return null;

        var left = Clamp((int)Math.Floor(x / 100d * bitmap.Width), 0, bitmap.Width - 1);
        var top = Clamp((int)Math.Floor(y / 100d * bitmap.Height), 0, bitmap.Height - 1);
        var width = Clamp((int)Math.Round(w / 100d * bitmap.Width), 1, bitmap.Width - left);
        var height = Clamp((int)Math.Round(h / 100d * bitmap.Height), 1, bitmap.Height - top);
        if (width < 2 || height < 2) return null;

        var info = new SKImageInfo(width, height);
        using var surface = SKSurface.Create(info);
        surface.Canvas.DrawBitmap(
            bitmap,
            new SKRect(left, top, left + width, top + height),
            new SKRect(0, 0, width, height));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static bool SlideIsDark(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        return Luma(BackgroundOf(bitmap.Pixels, bitmap.Width, bitmap.Height)) < 100;
    }

    public static (int Width, int Height) Size(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        return (bitmap.Width, bitmap.Height);
    }

    public static List<string> InkRuns(byte[] imageBytes, double yFrom, double yTo)
    {
        using var bitmap = Decode(imageBytes);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = bitmap.Pixels;
        var background = BackgroundOf(pixels, width, height);
        var notes = new List<string>();
        var y0 = Clamp((int)(yFrom / 100d * height), 0, height - 1);
        var y1 = Clamp((int)(yTo / 100d * height), 0, height - 1);
        for (var y = y0; y <= y1; y += 3)
        {
            var longest = 0;
            var current = 0;
            var start = 0;
            var bestStart = 0;
            SKColor sample = default;
            for (var x = 0; x < width; x++)
            {
                var color = pixels[y * width + x];
                if (ChannelDistance(color, background) > 28)
                {
                    if (current == 0) start = x;
                    current++;
                    if (current > longest)
                    {
                        longest = current;
                        bestStart = start;
                        sample = color;
                    }
                }
                else current = 0;
            }
            if (longest < width * 0.06) continue;
            notes.Add($"y={y * 100d / height:0.0} x={bestStart * 100d / width:0.0} w={longest * 100d / width:0.0} {sample.Red:X2}{sample.Green:X2}{sample.Blue:X2}");
        }
        return notes;
    }

    public static string SampleAt(byte[] imageBytes, double xPct, double yPct)
    {
        using var bitmap = Decode(imageBytes);
        var x = Clamp((int)Math.Round(xPct / 100d * (bitmap.Width - 1)), 0, bitmap.Width - 1);
        var y = Clamp((int)Math.Round(yPct / 100d * (bitmap.Height - 1)), 0, bitmap.Height - 1);
        var color = bitmap.Pixels[y * bitmap.Width + x];
        var background = BackgroundOf(bitmap.Pixels, bitmap.Width, bitmap.Height);
        return $"{xPct:0.0},{yPct:0.0} {color.Red:X2}{color.Green:X2}{color.Blue:X2} luma={Luma(color)} dist={ChannelDistance(color, background)} ink={IsFrameInk(color, background)} navy={IsNavyFill(color, background)}";
    }

    /// <summary>Ablehnungsgründe der letzten Rahmensuche.</summary>
    public static List<string> Trace { get; } = new();

    /// <summary>Karte, Kopfleiste, Trennlinie oder Icon, gemessen in Prozent.</summary>
    public readonly record struct DetectedShape(string Kind, double X, double Y, double W, double H, string Fill, string Color);

    /// <summary>
    /// Misst Karten, Kopfleisten, Trennlinien und Icons direkt im Bild.
    /// Grafiken, die sich nicht als Form nachbauen lassen, bleiben Bildausschnitte.
    /// </summary>
    public static List<DetectedShape> FindStructure(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = bitmap.Pixels;
        var background = BackgroundOf(pixels, width, height);
        var lightSlide = Luma(background) > 170;
        var shapes = new List<DetectedShape>();
        if (width < 32 || height < 32) return shapes;

        var ink = new bool[pixels.Length];
        var strong = new bool[pixels.Length];
        var navy = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            strong[i] = IsStrongInk(pixels[i], background);
            ink[i] = strong[i] || IsFrameInk(pixels[i], background) || IsLightOnDark(pixels[i], background);
            navy[i] = lightSlide && IsNavyFill(pixels[i], background);
        }

        Trace.Clear();
        AddGraphics(shapes, pixels, width, height, background);

        var thin = new bool[pixels.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (!ink[index]) continue;
            var around = CountWindow(ink, width, height, x, y, 3);
            thin[index] = around is >= 4 and <= 22;
        }

        var linked = Dilate(thin, width, height, 1);
        var frames = Components(linked, pixels, width, height, eight: true);
        foreach (var frame in frames.OrderByDescending(frame => frame.Area))
        {
            var wPct = frame.Width * 100d / width;
            var hPct = frame.Height * 100d / height;
            if (wPct < 8 || hPct < 6) continue;
            var tight = Tight(strong, width, height, frame);
            var use = tight.Width > width * 0.1 ? tight : frame;
            var fill = use.Count / (double)Math.Max(1, use.Area);
            var edges = EdgeScore(strong, navy, width, use);
            var round = Circularity(linked, width, use);
            Trace.Add($"{use.Width * 100d / width:0.0}x{use.Height * 100d / height:0.0} @{use.Left * 100d / width:0.0},{use.Top * 100d / height:0.0} fill={fill:0.00} edges={edges:0.00} round={round:0.00}");
            if (use.Width * 100d / width < 18 || use.Height * 100d / height < 16) continue;
            if (use.Width * 100d / width > 96 || use.Height * 100d / height > 62) continue;
            if (fill > 0.25) continue;
            if (edges < 0.45) continue;
            if (round > 0.72) continue;

            var color = Hex(Math.Max(1, use.Red) / Math.Max(1, use.Count), Math.Max(1, use.Green) / Math.Max(1, use.Count), Math.Max(1, use.Blue) / Math.Max(1, use.Count));
            if (Busy(pixels, width, use, background)) continue;
            var card = BoxShape("roundRect", use, width, height, SampleFill(pixels, width, height, use, background), color);
            if (OverlapsCard(shapes, card) || CoversGraphic(shapes, card)) continue;
            shapes.Add(card);
        }

        AddLightCards(shapes, ink, pixels, width, height, background);

        var cards = shapes.Where(shape => shape.Kind == "roundRect").ToList();
        foreach (var card in cards)
            AddDividers(shapes, thin, width, height, card);

        if (lightSlide)
        {
            foreach (var bar in Components(navy, pixels, width, height, eight: false))
            {
                var wPct = bar.Width * 100d / width;
                var hPct = bar.Height * 100d / height;
                if (wPct < 16 || hPct is < 2.2 or > 11) continue;
                if (bar.Count / (double)bar.Area < 0.42) continue;
                var fill = Hex(bar.Red / bar.Count, bar.Green / bar.Count, bar.Blue / bar.Count);
                var header = BoxShape("rect", bar, width, height, fill, fill);
                if (cards.Count > 0 && !cards.Any(card => Contains(card, header.X + header.W / 2, header.Y + header.H / 2)))
                    continue;
                shapes.Add(header);
                AddHeaderIcon(shapes, pixels, width, height, bar);
            }

            shapes.RemoveAll(shape =>
                shape.Kind == "line"
                && shapes.Any(header => header.Kind == "rect"
                    && Math.Abs(header.Y + header.H - shape.Y) < 1.8
                    && shape.X < header.X + header.W
                    && shape.X + shape.W > header.X));
        }

        AddRings(shapes, pixels, width, height, background);
        var ring = AddGauge(shapes, pixels, width, height, background, imageBytes);
        var graphics = shapes.Where(shape => shape.Kind == "picture" && shape.W > 12 && shape.H > 12).ToList();
        if (ring is { } found && !graphics.Contains(found)) graphics.Add(found);
        foreach (var badge in FindIconBadges(imageBytes))
        {
            if (graphics.Any(ring => Contains(ring, badge.X + badge.W / 2, badge.Y + badge.H / 2))) continue;
            shapes.Add(new DetectedShape("picture", badge.X, badge.Y, badge.W, badge.H, "", ""));
        }

        AddLineIcons(shapes, pixels, width, height, background, graphics);
        AddDarkIcons(shapes, pixels, width, height, background, graphics.FirstOrDefault());
        AddNavyDisks(shapes, pixels, width, height, background);
        shapes.RemoveAll(shape => shape.Kind == "picture" && shape.Y < 13 && shape.W < 5.5 && shape.H < 9);
        shapes.RemoveAll(shape => shape.Kind == "picture" && shape.W < 3.4 && shape.H < 5.2);
        DropLetterRuns(shapes);
        return shapes;
    }

    /// <summary>Zeichnet die gemessenen Rahmen auf das Bild, zum Prüfen.</summary>
    public static byte[] DrawOverlay(byte[] imageBytes)
    {
        using var bitmap = Decode(imageBytes);
        using var canvas = new SKCanvas(bitmap);
        foreach (var shape in FindStructure(imageBytes))
        {
            var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = shape.Kind == "picture" ? 2 : 3,
                Color = shape.Kind switch
                {
                    "picture" => SKColors.DeepSkyBlue,
                    "rect" => SKColors.LimeGreen,
                    "line" => SKColors.OrangeRed,
                    _ => SKColors.Crimson
                },
                IsAntialias = true
            };
            canvas.DrawRect(
                (float)(shape.X / 100d * bitmap.Width),
                (float)(shape.Y / 100d * bitmap.Height),
                (float)(shape.W / 100d * bitmap.Width),
                (float)(shape.H / 100d * bitmap.Height),
                paint);
            paint.Dispose();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] imageBytes) =>
        SKBitmap.Decode(imageBytes) ?? throw new InvalidOperationException("Bild konnte nicht gelesen werden.");

    private static SKColor BackgroundOf(SKColor[] pixels, int width, int height)
    {
        var buckets = new Dictionary<int, (int Count, long Red, long Green, long Blue)>();
        for (var y = height / 10; y < height; y += Math.Max(1, height / 14))
        for (var x = width / 10; x < width; x += Math.Max(1, width / 18))
        {
            var color = pixels[y * width + x];
            var key = (color.Red / 24 << 8) | (color.Green / 24 << 4) | (color.Blue / 24);
            var current = buckets.GetValueOrDefault(key);
            buckets[key] = (current.Count + 1, current.Red + color.Red, current.Green + color.Green, current.Blue + color.Blue);
        }

        var best = buckets.Values.OrderByDescending(bucket => bucket.Count).First();
        return new SKColor((byte)(best.Red / best.Count), (byte)(best.Green / best.Count), (byte)(best.Blue / best.Count));
    }

    private static bool IsBadgePixel(SKColor color, SKColor background)
    {
        if (color.Alpha < 200) return false;
        var distance = Math.Abs(color.Red - background.Red)
            + Math.Abs(color.Green - background.Green)
            + Math.Abs(color.Blue - background.Blue);
        return distance > 28;
    }

    private static void TryEnqueue(bool[] mark, bool[] seen, Queue<int> queue, int index)
    {
        if (!mark[index] || seen[index]) return;
        seen[index] = true;
        queue.Enqueue(index);
    }

    private static double Overlap(double x, double y, double w, double h, PercentBox other)
    {
        var width = Math.Max(0, Math.Min(x + w, other.X + other.W) - Math.Max(x, other.X));
        var height = Math.Max(0, Math.Min(y + h, other.Y + other.H) - Math.Max(y, other.Y));
        return width * height;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    private readonly record struct Region(int Left, int Top, int Right, int Bottom, int Count, long Red, long Green, long Blue)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
        public int Area => Math.Max(1, Width * Height);
    }

    private static bool IsStrongInk(SKColor color, SKColor background)
    {
        if (color.Alpha < 200) return false;
        var distance = ChannelDistance(color, background);
        if (distance < 48) return false;
        var luma = Luma(color);
        if (luma is < 78 or > 200) return false;
        return color.Blue + 8 >= color.Red && color.Blue + 6 >= color.Green - 12;
    }

    private static Region Tight(bool[] mask, int width, int height, Region outer)
    {
        var left = outer.Right;
        var right = outer.Left;
        var top = outer.Bottom;
        var bottom = outer.Top;
        var count = 0;
        long red = 0, green = 0, blue = 0;
        for (var y = outer.Top; y <= outer.Bottom; y++)
        for (var x = outer.Left; x <= outer.Right; x++)
        {
            if (!mask[y * width + x]) continue;
            count++;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }
        return count < 30 ? outer : new Region(left, top, right, bottom, count, red, green, blue);
    }

    private static bool IsLightOnDark(SKColor color, SKColor background)
    {
        if (Luma(background) > 90 || color.Alpha < 200) return false;
        return Luma(color) > Luma(background) + 45 && ChannelDistance(color, background) > 40;
    }

    private static bool IsFrameInk(SKColor color, SKColor background)
    {
        if (color.Alpha < 200) return false;
        var distance = ChannelDistance(color, background);
        if (distance < 16) return false;
        var luma = Luma(color);
        if (luma is < 70 or > 248) return false;
        return color.Blue + 18 >= color.Red && color.Green + 24 >= color.Red;
    }

    private static bool IsNavyFill(SKColor color, SKColor background)
    {
        if (color.Alpha < 200 || ChannelDistance(color, background) < 36) return false;
        var luma = Luma(color);
        return color.Red < 70 && color.Green < 130 && color.Blue > 40 && color.Blue > color.Red + 12 && luma < 150;
    }

    private static bool IsIconInk(SKColor color, SKColor background)
    {
        if (color.Alpha < 200 || ChannelDistance(color, background) < 40) return false;
        return color.Blue > 120 && color.Blue > color.Red + 28 && Luma(color) is > 60 and < 215;
    }

    private static int Luma(SKColor color) => (color.Red + color.Green + color.Blue) / 3;

    private static int ChannelDistance(SKColor color, SKColor other) =>
        Math.Abs(color.Red - other.Red) + Math.Abs(color.Green - other.Green) + Math.Abs(color.Blue - other.Blue);

    private static int CountWindow(bool[] mask, int width, int height, int x, int y, int radius)
    {
        var count = 0;
        for (var dy = -radius; dy <= radius; dy++)
        {
            var yy = y + dy;
            if (yy < 0 || yy >= height) continue;
            for (var dx = -radius; dx <= radius; dx++)
            {
                var xx = x + dx;
                if (xx < 0 || xx >= width) continue;
                if (mask[yy * width + xx]) count++;
            }
        }
        return count;
    }

    private static bool[] Dilate(bool[] source, int width, int height, int radius)
    {
        var result = new bool[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!source[y * width + x]) continue;
            for (var dy = -radius; dy <= radius; dy++)
            {
                var yy = y + dy;
                if (yy < 0 || yy >= height) continue;
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var xx = x + dx;
                    if (xx < 0 || xx >= width) continue;
                    result[yy * width + xx] = true;
                }
            }
        }
        return result;
    }

    private static List<Region> Components(bool[] mask, SKColor[] pixels, int width, int height, bool eight)
    {
        var seen = new bool[mask.Length];
        var queue = new Queue<int>();
        var regions = new List<Region>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            queue.Clear();
            queue.Enqueue(start);
            seen[start] = true;
            var left = start % width;
            var right = left;
            var top = start / width;
            var bottom = top;
            var count = 0;
            long red = 0, green = 0, blue = 0;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width;
                var y = index / width;
                count++;
                var color = pixels[index];
                red += color.Red;
                green += color.Green;
                blue += color.Blue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (!eight && Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                    var xx = x + dx;
                    var yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= width || yy >= height) continue;
                    var next = yy * width + xx;
                    if (!mask[next] || seen[next]) continue;
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
            regions.Add(new Region(left, top, right, bottom, count, red, green, blue));
        }
        return regions;
    }

    private static double EdgeScore(bool[] mask, bool[]? also, int width, Region region)
    {
        bool Hit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= mask.Length / width) return false;
            var index = y * width + x;
            return mask[index] || (also is not null && also[index]);
        }
        var top = 0;
        var bottom = 0;
        var left = 0;
        var right = 0;
        for (var x = region.Left; x <= region.Right; x++)
        {
            var topHit = false;
            var bottomHit = false;
            for (var pad = 0; pad < 6; pad++)
            {
                topHit |= Hit(x, region.Top + pad);
                bottomHit |= Hit(x, region.Bottom - pad);
            }
            if (topHit) top++;
            if (bottomHit) bottom++;
        }
        for (var y = region.Top; y <= region.Bottom; y++)
        {
            var leftHit = false;
            var rightHit = false;
            for (var pad = 0; pad < 6; pad++)
            {
                leftHit |= Hit(region.Left + pad, y);
                rightHit |= Hit(region.Right - pad, y);
            }
            if (leftHit) left++;
            if (rightHit) right++;
        }
        var widthSpan = Math.Max(1, region.Width);
        var heightSpan = Math.Max(1, region.Height);
        return Math.Min(
            Math.Min(top / (double)widthSpan, bottom / (double)widthSpan),
            Math.Min(left / (double)heightSpan, right / (double)heightSpan));
    }

    private static double Circularity(bool[] mask, int width, Region region)
    {
        double sum = 0;
        double sumSq = 0;
        var count = 0;
        var cx = (region.Left + region.Right) / 2d;
        var cy = (region.Top + region.Bottom) / 2d;
        for (var y = region.Top; y <= region.Bottom; y += 2)
        for (var x = region.Left; x <= region.Right; x += 2)
        {
            if (!mask[y * width + x]) continue;
            var dx = x - cx;
            var dy = (y - cy) * (region.Width / (double)Math.Max(1, region.Height));
            var radius = Math.Sqrt(dx * dx + dy * dy);
            sum += radius;
            sumSq += radius * radius;
            count++;
        }
        if (count < 20) return 0;
        var mean = sum / count;
        if (mean < 1) return 0;
        var variance = sumSq / count - mean * mean;
        var deviation = Math.Sqrt(Math.Max(0, variance));
        return 1d - Math.Clamp(deviation / mean, 0d, 1d);
    }

    private static void AddLightCards(List<DetectedShape> shapes, bool[] ink, SKColor[] pixels, int width, int height, SKColor background)
    {
        var bands = new List<(int Y, int X0, int X1)>();
        for (var y = (int)(height * 0.03); y < height * 0.96; y++)
        {
            var run = 0;
            var start = 0;
            for (var x = 0; x <= width; x++)
            {
                var on = x < width && ink[y * width + x];
                if (on)
                {
                    if (run == 0) start = x;
                    run++;
                    continue;
                }
                if (run > width * 0.09)
                    bands.Add((y, start, start + run - 1));
                run = 0;
            }
        }

        var used = new bool[bands.Count];
        for (var i = 0; i < bands.Count; i++)
        {
            if (used[i]) continue;
            var top = bands[i];
            var match = -1;
            for (var j = i + 1; j < bands.Count; j++)
            {
                var bottom = bands[j];
                var gap = bottom.Y - top.Y;
                if (gap < height * 0.12) continue;
                if (gap > height * 0.36) break;
                if (Math.Abs(bottom.X0 - top.X0) > width * 0.035) continue;
                if (Math.Abs((bottom.X1 - bottom.X0) - (top.X1 - top.X0)) > width * 0.045) continue;
                match = j;
            }
            if (match < 0) continue;
            used[i] = true;
            used[match] = true;
            var bottomBand = bands[match];
            var left = Math.Min(top.X0, bottomBand.X0);
            var right = Math.Max(top.X1, bottomBand.X1);
            var sideHits = 0;
            var sideSteps = 0;
            for (var y = top.Y; y <= bottomBand.Y; y += 2)
            {
                sideSteps++;
                var leftOn = false;
                var rightOn = false;
                for (var pad = 0; pad < 4; pad++)
                {
                    var lx = Math.Max(0, left - pad);
                    var rx = Math.Min(width - 1, right + pad);
                    leftOn |= ink[y * width + lx];
                    rightOn |= ink[y * width + rx];
                }
                if (leftOn && rightOn) sideHits++;
            }
            if (sideSteps == 0 || sideHits / (double)sideSteps < 0.28) continue;
            var region = new Region(left, top.Y, right, bottomBand.Y, 80, 0, 0, 0);
            if (Busy(pixels, width, region, background)) continue;
            var card = BoxShape("roundRect", region, width, height, SampleFill(pixels, width, height, region, background), "003C74");
            if (card.W < 16 || card.H is < 15 or > 42) continue;
            if (OverlapsCard(shapes, card) || CoversGraphic(shapes, card)) continue;
            shapes.Add(card);
        }
    }

    private static void AddDividers(List<DetectedShape> shapes, bool[] thin, int width, int height, DetectedShape card)
    {
        var left = Clamp((int)Math.Floor(card.X / 100d * width), 0, width - 1);
        var right = Clamp((int)Math.Floor((card.X + card.W) / 100d * width), 0, width - 1);
        var top = Clamp((int)Math.Floor((card.Y + card.H * 0.08) / 100d * height), 0, height - 1);
        var bottom = Clamp((int)Math.Floor((card.Y + card.H * 0.94) / 100d * height), 0, height - 1);
        var span = Math.Max(1, right - left);
        var runStart = -1;
        for (var y = top; y <= bottom; y++)
        {
            var longest = 0;
            var current = 0;
            for (var x = left + span / 12; x < right - span / 12; x++)
            {
                if (thin[y * width + x])
                {
                    current++;
                    if (current > longest) longest = current;
                }
                else current = 0;
            }
            var line = longest > span * 0.28;
            if (line && runStart < 0) runStart = y;
            if (!line && runStart >= 0)
            {
                CommitDivider(shapes, card, runStart, y - 1, height);
                runStart = -1;
            }
        }
        if (runStart >= 0) CommitDivider(shapes, card, runStart, bottom, height);
    }

    private static void CommitDivider(List<DetectedShape> shapes, DetectedShape card, int y0, int y1, int height)
    {
        if (y1 - y0 > height * 0.025) return;
        var y = ((y0 + y1) / 2d) / height * 100d;
        shapes.Add(new DetectedShape("line", card.X + card.W * 0.04, y, card.W * 0.92, 0.35, "", "003C74"));
    }

    private static void AddHeaderIcon(List<DetectedShape> shapes, SKColor[] pixels, int width, int height, Region header)
    {
        var seen = new bool[pixels.Length];
        var queue = new Queue<int>();
        for (var y = header.Top; y <= header.Bottom; y++)
        for (var x = header.Left; x <= header.Right; x++)
        {
            var index = y * width + x;
            if (seen[index] || Luma(pixels[index]) < 185) continue;
            queue.Clear();
            queue.Enqueue(index);
            seen[index] = true;
            var left = x;
            var right = x;
            var top = y;
            var bottom = y;
            var count = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var cx = current % width;
                var cy = current / width;
                count++;
                if (cx < left) left = cx;
                if (cx > right) right = cx;
                if (cy < top) top = cy;
                if (cy > bottom) bottom = cy;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                    var xx = cx + dx;
                    var yy = cy + dy;
                    if (xx < header.Left || xx > header.Right || yy < header.Top || yy > header.Bottom) continue;
                    var next = yy * width + xx;
                    if (seen[next] || Luma(pixels[next]) < 185) continue;
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
            var boxW = right - left + 1;
            var boxH = bottom - top + 1;
            if (boxH < header.Height * 0.45 || boxW < header.Height * 0.35) continue;
            var aspect = boxW / (double)Math.Max(1, boxH);
            if (aspect is < 0.65 or > 1.45) continue;
            if (count < boxW * boxH * 0.25) continue;
            const int pad = 2;
            shapes.Add(new DetectedShape(
                "picture",
                Math.Max(0, left - pad) * 100d / width,
                Math.Max(0, top - pad) * 100d / height,
                (Math.Min(width - 1, right + pad) - Math.Max(0, left - pad) + 1) * 100d / width,
                (Math.Min(height - 1, bottom + pad) - Math.Max(0, top - pad) + 1) * 100d / height,
                "",
                ""));
        }
    }

    private static void AddRings(List<DetectedShape> shapes, SKColor[] pixels, int width, int height, SKColor background)
    {
        if (Luma(background) < 140) return;
        var mask = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            mask[i] = IsNavyFill(pixels[i], background) || (ChannelDistance(pixels[i], background) > 48 && Luma(pixels[i]) < 150);
        var linked = Dilate(mask, width, height, 1);
        foreach (var region in Components(linked, pixels, width, height, eight: true))
        {
            var wPct = region.Width * 100d / width;
            var hPct = region.Height * 100d / height;
            if (wPct is < 10 or > 42 || hPct is < 14 or > 46) continue;
            if (wPct / hPct is < 0.48 or > 0.85) continue;
            var fill = region.Count / (double)region.Area;
            if (fill is < 0.08 or > 0.62) continue;
            if (Circularity(linked, width, region) < 0.45) continue;
            var box = BoxShape("picture", region, width, height, "", "");
            if (shapes.Any(shape => shape.Kind == "picture" && shape.W > 10 && Contains(shape, box.X + box.W / 2, box.Y + box.H / 2)))
                continue;
            if (shapes.Any(shape => shape.Kind == "roundRect" && Overlap(box.X, box.Y, box.W, box.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > box.W * box.H * 0.2))
                continue;
            shapes.Add(box);
        }
    }

    private static DetectedShape? AddGauge(
        List<DetectedShape> shapes,
        SKColor[] pixels,
        int width,
        int height,
        SKColor background,
        byte[] imageBytes)
    {
        DetectedShape? best = null;
        var bestCount = 120;
        foreach (var face in FindIconBadges(imageBytes))
        {
            var centerX = face.X + face.W / 2;
            var centerY = face.Y + face.H / 2;
            if (shapes.Any(shape => shape.Kind == "picture" && shape.W > 12 && Contains(shape, centerX, centerY))) continue;
            var cx = centerX / 100d * width;
            var cy = centerY / 100d * height;
            var faceRadius = Math.Max(face.W / 100d * width, face.H / 100d * height) * 0.55;
            var reach = faceRadius * 2.2;
            var left = width;
            var right = 0;
            var top = height;
            var bottom = 0;
            var count = 0;
            var quadrant = new int[4];
            var minX = Math.Max(0, (int)(cx - reach));
            var maxX = Math.Min(width - 1, (int)(cx + reach));
            var minY = Math.Max(0, (int)(cy - reach));
            var maxY = Math.Min(height - 1, (int)(cy + reach));
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < faceRadius * 1.3 || distance > reach) continue;
                if (ChannelDistance(pixels[y * width + x], background) < 42) continue;
                count++;
                quadrant[(dx >= 0 ? 1 : 0) + (dy >= 0 ? 2 : 0)]++;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
            if (count < bestCount || quadrant.Any(part => part < count * 0.12) || right <= left || bottom <= top) continue;
            var boxW = (right - left + 1) * 100d / width;
            var boxH = (bottom - top + 1) * 100d / height;
            if (boxW < face.W * 1.5 || boxH < face.H * 1.5 || boxW > 36 || boxH > 42) continue;
            if (boxW / boxH is < 0.48 or > 0.85) continue;
            var gauge = new DetectedShape("picture", left * 100d / width, top * 100d / height, boxW, boxH, "", "");
            if (shapes.Any(shape => shape.Kind == "roundRect" && Overlap(gauge.X, gauge.Y, gauge.W, gauge.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > gauge.W * gauge.H * 0.15))
                continue;
            best = gauge;
            bestCount = count;
        }

        if (best is not { } chosen) return null;
        shapes.Add(chosen);
        return chosen;
    }

    private static void AddNavyDisks(List<DetectedShape> shapes, SKColor[] pixels, int width, int height, SKColor background)
    {
        if (Luma(background) < 140) return;
        var mask = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            mask[i] = IsNavyFill(pixels[i], background);
        foreach (var region in Components(mask, pixels, width, height, eight: false))
        {
            var wPct = region.Width * 100d / width;
            var hPct = region.Height * 100d / height;
            if (wPct is < 3.2 or > 9 || hPct is < 4.5 or > 16) continue;
            if (region.Width / (double)Math.Max(1, region.Height) is < 0.7 or > 1.35) continue;
            if (region.Count < region.Area * 0.4) continue;
            var box = BoxShape("picture", region, width, height, "", "");
            if (shapes.Any(shape => shape.Kind == "rect" && Contains(shape, box.X + box.W / 2, box.Y + box.H / 2))) continue;
            if (shapes.Any(shape => shape.Kind == "picture" && Overlap(box.X, box.Y, box.W, box.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > box.W * box.H * 0.3))
                continue;
            shapes.Add(box);
        }
    }

    private static void AddLineIcons(
        List<DetectedShape> shapes,
        SKColor[] pixels,
        int width,
        int height,
        SKColor background,
        List<DetectedShape> graphics)
    {
        var mask = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            mask[i] = IsIconInk(pixels[i], background);

        foreach (var region in Components(mask, pixels, width, height, eight: true))
        {
            var wPct = region.Width * 100d / width;
            var hPct = region.Height * 100d / height;
            var aspect = region.Width / (double)Math.Max(1, region.Height);
            var centerX = (region.Left + region.Right) / 2d * 100d / width;
            var centerY = (region.Top + region.Bottom) / 2d * 100d / height;
            if (wPct is < 1.8 or > 8 || hPct is < 2.2 or > 10) continue;
            if (aspect is < 0.35 or > 1.9) continue;
            if (region.Count / (double)region.Area is < 0.06 or > 0.72) continue;
            var box = BoxShape("picture", region, width, height, "", "");
            if (graphics.Any(ring => ring.W > 12 && Contains(ring, centerX, centerY))) continue;
            if (shapes.Any(shape => shape.Kind == "picture" && Overlap(box.X, box.Y, box.W, box.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > box.W * box.H * 0.35))
                continue;
            shapes.Add(new DetectedShape("picture", box.X, box.Y, box.W, box.H, "", ""));
        }
    }

    private static void AddDarkIcons(
        List<DetectedShape> shapes,
        SKColor[] pixels,
        int width,
        int height,
        SKColor background,
        DetectedShape? gauge)
    {
        var cards = shapes.Where(shape => shape.Kind == "roundRect").ToList();
        var mask = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            mask[i] = IsNavyFill(pixels[i], background);

        foreach (var region in Components(mask, pixels, width, height, eight: true))
        {
            var wPct = region.Width * 100d / width;
            var hPct = region.Height * 100d / height;
            if (wPct is < 2.0 or > 7 || hPct is < 2.6 or > 8) continue;
            var aspect = region.Width / (double)Math.Max(1, region.Height);
            if (aspect is < 0.45 or > 1.35) continue;
            if (region.Count < region.Area * 0.12) continue;
            var box = BoxShape("picture", region, width, height, "", "");
            var centerX = box.X + box.W / 2;
            var centerY = box.Y + box.H / 2;
            if (gauge is { } ring && Contains(ring, centerX, centerY)) continue;
            var card = cards.FirstOrDefault(candidate => Contains(candidate, centerX, centerY));
            if (card.W <= 0) continue;
            if (centerY < card.Y + card.H * 0.24) continue;
            if (centerX > card.X + card.W * 0.46) continue;
            if (shapes.Any(shape => shape.Kind == "rect" && Contains(shape, centerX, centerY))) continue;
            if (shapes.Any(shape => shape.Kind == "picture" && Overlap(box.X, box.Y, box.W, box.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > box.W * box.H * 0.3))
                continue;
            shapes.Add(box);
        }
    }

    private static void AddGraphics(List<DetectedShape> shapes, SKColor[] pixels, int width, int height, SKColor background)
    {
        CollectGraphics(shapes, pixels, width, height, background, 40, 3, 8, 70, 0.5);
        CollectGraphics(shapes, pixels, width, height, background, 16, 5, 12, 48, 0.72);
        var photo = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            photo[i] = ChannelDistance(pixels[i], background) > 36;
        var grown = Dilate(photo, width, height, 3);
        foreach (var region in Components(grown, pixels, width, height, eight: true))
        {
            var wPct = region.Width * 100d / width;
            var hPct = region.Height * 100d / height;
            if (wPct < 16 || hPct < 16 || wPct > 58 || hPct > 58) continue;
            var raw = 0;
            var samples = 0;
            for (var y = region.Top; y <= region.Bottom; y += 2)
            for (var x = region.Left; x <= region.Right; x += 2)
            {
                samples++;
                if (photo[y * width + x]) raw++;
            }
            var density = raw / (double)Math.Max(1, samples);
            if (density is < 0.22 or > 0.88) continue;
            var box = BoxShape("picture", region, width, height, "", "");
            if (shapes.Any(shape => shape.Kind == "picture" && shape.W > 8
                && Overlap(box.X, box.Y, box.W, box.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > box.W * box.H * 0.35))
                continue;
            shapes.Add(box);
        }
        MergeTouchingPictures(shapes);
    }

    private static void CollectGraphics(
        List<DetectedShape> shapes,
        SKColor[] pixels,
        int width,
        int height,
        SKColor background,
        int distance,
        int radius,
        double minSize,
        double maxSize,
        double minFill)
    {
        var mask = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            mask[i] = ChannelDistance(pixels[i], background) > distance;
        var core = Erode(mask, width, height, radius);
        foreach (var region in Components(core, pixels, width, height, eight: true))
        {
            var wPct = region.Width * 100d / width;
            var hPct = region.Height * 100d / height;
            if (wPct < minSize || hPct < minSize || wPct > maxSize || hPct > maxSize) continue;
            if (region.Count / (double)region.Area < minFill) continue;
            var grown = new Region(
                Math.Max(0, region.Left - radius),
                Math.Max(0, region.Top - radius),
                Math.Min(width - 1, region.Right + radius),
                Math.Min(height - 1, region.Bottom + radius),
                region.Count,
                region.Red,
                region.Green,
                region.Blue);
            var box = BoxShape("picture", grown, width, height, "", "");
            if (shapes.Any(shape => shape.Kind is "roundRect" or "picture" && shape.W > 8
                && Overlap(box.X, box.Y, box.W, box.H, new PercentBox(shape.X, shape.Y, shape.W, shape.H)) > box.W * box.H * 0.3))
                continue;
            shapes.Add(box);
        }
    }

    private static bool[] Erode(bool[] source, int width, int height, int radius)
    {
        var result = new bool[source.Length];
        for (var y = radius; y < height - radius; y++)
        for (var x = radius; x < width - radius; x++)
        {
            var keep = true;
            for (var dy = -radius; dy <= radius && keep; dy++)
            for (var dx = -radius; dx <= radius && keep; dx++)
                keep = source[(y + dy) * width + x + dx];
            result[y * width + x] = keep;
        }
        return result;
    }

    private static void MergeTouchingPictures(List<DetectedShape> shapes)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var pictures = shapes.Where(shape => shape.Kind == "picture" && shape.W > 10 && shape.H > 10).ToList();
            for (var i = 0; i < pictures.Count && !changed; i++)
            for (var j = i + 1; j < pictures.Count && !changed; j++)
            {
                var a = pictures[i];
                var b = pictures[j];
                var gapX = Math.Max(0, Math.Max(a.X, b.X) - Math.Min(a.X + a.W, b.X + b.W));
                var gapY = Math.Max(0, Math.Max(a.Y, b.Y) - Math.Min(a.Y + a.H, b.Y + b.H));
                if (gapX > 2.5 || gapY > 2.5) continue;
                var x = Math.Min(a.X, b.X);
                var y = Math.Min(a.Y, b.Y);
                var w = Math.Max(a.X + a.W, b.X + b.W) - x;
                var h = Math.Max(a.Y + a.H, b.Y + b.H) - y;
                if (w > 72 || h > 72 || w / h is < 0.55 or > 1.85) continue;
                shapes.Remove(a);
                shapes.Remove(b);
                shapes.Add(new DetectedShape("picture", x, y, w, h, "", ""));
                changed = true;
            }
        }
    }

    private static double LumaVariance(SKColor[] pixels, int width, Region region)
    {
        double sum = 0;
        double sumSq = 0;
        var count = 0;
        for (var y = region.Top; y <= region.Bottom; y += 3)
        for (var x = region.Left; x <= region.Right; x += 3)
        {
            var luma = Luma(pixels[y * width + x]);
            sum += luma;
            sumSq += luma * luma;
            count++;
        }
        if (count < 8) return 0;
        var mean = sum / count;
        return sumSq / count - mean * mean;
    }

    private static string SampleFill(SKColor[] pixels, int width, int height, Region region, SKColor background)
    {
        long red = 0, green = 0, blue = 0, count = 0;
        var left = region.Left + region.Width / 5;
        var right = region.Right - region.Width / 5;
        var top = region.Top + region.Height / 5;
        var bottom = region.Bottom - region.Height / 5;
        if (right <= left || bottom <= top) return Hex(background.Red, background.Green, background.Blue);
        for (var y = top; y <= bottom; y += 4)
        for (var x = left; x <= right; x += 4)
        {
            var color = pixels[y * width + x];
            red += color.Red;
            green += color.Green;
            blue += color.Blue;
            count++;
        }
        if (count == 0) return Hex(background.Red, background.Green, background.Blue);
        return Hex(red / count, green / count, blue / count);
    }

    private static bool Busy(SKColor[] pixels, int width, Region region, SKColor background)
    {
        var content = 0;
        var samples = 0;
        var left = region.Left + region.Width / 6;
        var right = region.Right - region.Width / 6;
        var top = region.Top + region.Height / 6;
        var bottom = region.Bottom - region.Height / 6;
        for (var y = top; y <= bottom; y += 3)
        for (var x = left; x <= right; x += 3)
        {
            samples++;
            if (ChannelDistance(pixels[y * width + x], background) > 34) content++;
        }
        return samples > 0 && content > samples * 0.38;
    }

    private static bool OverlapsCard(List<DetectedShape> shapes, DetectedShape card)
    {
        return shapes.Any(other =>
        {
            if (other.Kind != "roundRect") return false;
            var area = Math.Min(card.W * card.H, other.W * other.H);
            return Overlap(card.X, card.Y, card.W, card.H, new PercentBox(other.X, other.Y, other.W, other.H)) > area * 0.22;
        });
    }

    private static bool CoversGraphic(List<DetectedShape> shapes, DetectedShape card)
    {
        return shapes.Any(picture =>
            picture.Kind == "picture"
            && picture.W > 8
            && Overlap(card.X, card.Y, card.W, card.H, new PercentBox(picture.X, picture.Y, picture.W, picture.H)) > picture.W * picture.H * 0.35);
    }

    private static void DropLetterRuns(List<DetectedShape> shapes)
    {
        var pictures = shapes.Where(shape => shape.Kind == "picture" && shape.W < 10 && shape.H < 12).ToList();
        shapes.RemoveAll(shape =>
        {
            if (shape.Kind != "picture" || shape.W >= 4.5 || shape.H >= 8) return false;
            var neighbors = pictures.Count(other =>
                Math.Abs(other.Y - shape.Y) < 1.6
                && Math.Abs(other.H - shape.H) < 2.2
                && Math.Abs(other.X - shape.X) > 0.4
                && Math.Abs(other.X - shape.X) < shape.W + 3.5);
            return neighbors >= 2;
        });
    }

    private static DetectedShape BoxShape(string kind, Region region, int width, int height, string fill, string color)
    {
        const int pad = 1;
        var left = Math.Max(0, region.Left - pad);
        var top = Math.Max(0, region.Top - pad);
        var right = Math.Min(width - 1, region.Right + pad);
        var bottom = Math.Min(height - 1, region.Bottom + pad);
        return new DetectedShape(
            kind,
            left * 100d / width,
            top * 100d / height,
            (right - left + 1) * 100d / width,
            (bottom - top + 1) * 100d / height,
            fill,
            color);
    }

    private static bool Contains(DetectedShape shape, double x, double y) =>
        x >= shape.X && x <= shape.X + shape.W && y >= shape.Y && y <= shape.Y + shape.H;

    private static string Hex(long red, long green, long blue) =>
        $"{Clamp((int)red, 0, 255):X2}{Clamp((int)green, 0, 255):X2}{Clamp((int)blue, 0, 255):X2}";
}
