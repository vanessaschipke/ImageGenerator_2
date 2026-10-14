using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SlideGenerator.Core.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Baut genau eine Folie aus dem gpt-5-Layout.
/// Text und flache Flächen werden PowerPoint-Objekte. Fotos, Icons, Logos und
/// Diagramme werden als Ausschnitt des Originals eingesetzt. Es gibt keine zweite Folie.
/// </summary>
public sealed class PptxExportService : IPptxExportService
{
    // 13.333 Zoll. 1 inch = 914400 EMU.
    private const long LongSideEmu = 12192000;
    private const long FallbackHeightEmu = 6858000;
    private const long EmuPerInch = 914400;

    public byte[] BuildPptx(byte[] imageBytes, SlideLayout layout, string? userPrompt = null)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            throw new ArgumentException("imageBytes ist leer", nameof(imageBytes));
        if (layout is null) throw new ArgumentNullException(nameof(layout));
        if (layout.Elements is null || layout.Elements.Count == 0)
            throw new InvalidOperationException("Layout enthaelt keine Folienobjekte.");

        var (imgW, imgH) = PngCropper.Size(imageBytes);
        var (slideCx, slideCy) = SlideSizeFor(imgW, imgH);

        using var ms = new MemoryStream();
        using (var pptx = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var presentationPart = pptx.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            CreatePresentationParts(presentationPart, imageBytes, layout, userPrompt, slideCx, slideCy);
        }
        return ms.ToArray();
    }

    private static void CreatePresentationParts(
        PresentationPart presentationPart,
        byte[] imageBytes,
        SlideLayout layout,
        string? userPrompt,
        long slideCx,
        long slideCy)
    {
        var slideMasterPart = CreateSlideMasterPart(presentationPart);
        var slideLayoutPart = CreateSlideLayoutPart(slideMasterPart);
        CreateThemePart(slideMasterPart);
        var slidePart = CreateSlidePart(presentationPart, slideLayoutPart, imageBytes, layout, slideCx, slideCy);

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            CreateNotesSlidePart(slidePart, userPrompt!);
        }

        var presentation = presentationPart.Presentation;
        presentation.SlideMasterIdList = new P.SlideMasterIdList(
            new P.SlideMasterId
            {
                Id = 2147483648U,
                RelationshipId = presentationPart.GetIdOfPart(slideMasterPart)
            });
        presentation.SlideIdList = new P.SlideIdList(
            new P.SlideId
            {
                Id = 256U,
                RelationshipId = presentationPart.GetIdOfPart(slidePart)
            });
        presentation.SlideSize = new P.SlideSize
        {
            Cx = (int)slideCx,
            Cy = (int)slideCy,
            Type = P.SlideSizeValues.Custom
        };
        presentation.NotesSize = new P.NotesSize { Cx = 6858000L, Cy = 9144000L };
        presentation.DefaultTextStyle = new P.DefaultTextStyle();

        presentation.Save();
    }

    // ---------- Slide Master ----------
    private static SlideMasterPart CreateSlideMasterPart(PresentationPart presentationPart)
    {
        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();

        var slideMaster = new P.SlideMaster();
        var commonSlideData = new P.CommonSlideData();
        var shapeTree = new P.ShapeTree();

        var nvGrp = new P.NonVisualGroupShapeProperties();
        nvGrp.Append(new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty });
        nvGrp.Append(new P.NonVisualGroupShapeDrawingProperties());
        nvGrp.Append(new P.ApplicationNonVisualDrawingProperties());
        shapeTree.Append(nvGrp);

        var grpSpPr = new P.GroupShapeProperties();
        grpSpPr.Append(new A.TransformGroup());
        shapeTree.Append(grpSpPr);

        commonSlideData.Append(shapeTree);
        slideMaster.Append(commonSlideData);

        slideMaster.Append(new P.ColorMap
        {
            Background1 = A.ColorSchemeIndexValues.Light1,
            Text1 = A.ColorSchemeIndexValues.Dark1,
            Background2 = A.ColorSchemeIndexValues.Light2,
            Text2 = A.ColorSchemeIndexValues.Dark2,
            Accent1 = A.ColorSchemeIndexValues.Accent1,
            Accent2 = A.ColorSchemeIndexValues.Accent2,
            Accent3 = A.ColorSchemeIndexValues.Accent3,
            Accent4 = A.ColorSchemeIndexValues.Accent4,
            Accent5 = A.ColorSchemeIndexValues.Accent5,
            Accent6 = A.ColorSchemeIndexValues.Accent6,
            Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
            FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
        });

        slideMasterPart.SlideMaster = slideMaster;
        return slideMasterPart;
    }

    // ---------- Slide Layout ----------
    private static SlideLayoutPart CreateSlideLayoutPart(SlideMasterPart slideMasterPart)
    {
        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();

        var slideLayout = new P.SlideLayout
        {
            Type = P.SlideLayoutValues.Blank,
            Preserve = true
        };
        var cSld = new P.CommonSlideData { Name = "Blank" };
        var spTree = new P.ShapeTree();

        var nvGrp = new P.NonVisualGroupShapeProperties();
        nvGrp.Append(new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty });
        nvGrp.Append(new P.NonVisualGroupShapeDrawingProperties());
        nvGrp.Append(new P.ApplicationNonVisualDrawingProperties());
        spTree.Append(nvGrp);

        var grpSpPr = new P.GroupShapeProperties();
        grpSpPr.Append(new A.TransformGroup());
        spTree.Append(grpSpPr);

        cSld.Append(spTree);
        slideLayout.Append(cSld);
        slideLayout.Append(new P.ColorMapOverride(new A.MasterColorMapping()));

        slideLayoutPart.SlideLayout = slideLayout;

        // Layout -> Master Rueck-Relation registrieren (sonst schreibt das SDK
        // die Datei ppt/slideLayouts/_rels/slideLayout1.xml.rels nicht - PowerPoint
        // verweigert dann die Datei mit "Sorry, PowerPoint can't read ...").
        slideLayoutPart.AddPart(slideMasterPart);

        // Slide-Master -> Layout verlinken
        slideMasterPart.SlideMaster.SlideLayoutIdList = new P.SlideLayoutIdList(
            new P.SlideLayoutId
            {
                Id = 2147483649U,
                RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart)
            });
        slideMasterPart.SlideMaster.Save();
        return slideLayoutPart;
    }

    // ---------- Theme ----------
    private static ThemePart CreateThemePart(SlideMasterPart slideMasterPart)
    {
        var themePart = slideMasterPart.AddNewPart<ThemePart>();
        themePart.Theme = new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "1F497D" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "EEECE1" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "003C7E" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "0099CC" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "9BBB59" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "8064A2" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "4BACC6" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "F79646" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "0000FF" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "800080" }))
                { Name = "Office" },
                new A.FontScheme(
                    new A.MajorFont(
                        new A.LatinFont { Typeface = "Calibri Light" },
                        new A.EastAsianFont { Typeface = string.Empty },
                        new A.ComplexScriptFont { Typeface = string.Empty }),
                    new A.MinorFont(
                        new A.LatinFont { Typeface = "Calibri" },
                        new A.EastAsianFont { Typeface = string.Empty },
                        new A.ComplexScriptFont { Typeface = string.Empty }))
                { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                    new A.LineStyleList(
                        new A.Outline(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.PresetDash { Val = A.PresetLineDashValues.Solid })
                        { Width = 9525, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center },
                        new A.Outline(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.PresetDash { Val = A.PresetLineDashValues.Solid })
                        { Width = 25400, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center },
                        new A.Outline(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.PresetDash { Val = A.PresetLineDashValues.Solid })
                        { Width = 38100, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center }),
                    new A.EffectStyleList(
                        new A.EffectStyle(new A.EffectList()),
                        new A.EffectStyle(new A.EffectList()),
                        new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
                { Name = "Office" }),
            new A.ObjectDefaults(),
            new A.ExtraColorSchemeList())
        {
            Name = "Office Theme"
        };
        return themePart;
    }

    // ---------- Slide ----------
    private static SlidePart CreateSlidePart(
        PresentationPart presentationPart,
        SlideLayoutPart slideLayoutPart,
        byte[] imageBytes,
        SlideLayout layout,
        long slideCx,
        long slideCy)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        var slide = new P.Slide();
        var cSld = new P.CommonSlideData();
        var spTree = new P.ShapeTree();

        var nvGrp = new P.NonVisualGroupShapeProperties();
        nvGrp.Append(new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty });
        nvGrp.Append(new P.NonVisualGroupShapeDrawingProperties());
        nvGrp.Append(new P.ApplicationNonVisualDrawingProperties());
        spTree.Append(nvGrp);

        var grpSpPr = new P.GroupShapeProperties();
        grpSpPr.Append(new A.TransformGroup());
        spTree.Append(grpSpPr);

        var nextId = 2U;
        var background = SnapColor(layout.BackgroundColor) ?? "FFFFFF";
        AppendShape(spTree, ref nextId, "Background", new SlideElement
        {
            Kind = "rect",
            X = 0,
            Y = 0,
            W = 100,
            H = 100,
            Fill = background
        }, slideCx, slideCy, slidePart, imageBytes);

        var elements = PrepareLayout(layout.Elements, background, slideCx);
        foreach (var element in elements)
        {
            element.Fill = SnapColor(element.Fill) ?? string.Empty;
            element.Color = SnapColor(element.Color) ?? string.Empty;
        }
        elements = ApplyMeasuredGeometry(elements, imageBytes);
        foreach (var element in elements.Where(e => !IsPicture(e) && !IsText(e)))
            AppendShape(spTree, ref nextId, "Element" + nextId, element, slideCx, slideCy, slidePart, imageBytes);
        foreach (var element in elements.Where(IsPicture))
            AppendShape(spTree, ref nextId, "Element" + nextId, element, slideCx, slideCy, slidePart, imageBytes);
        foreach (var element in elements.Where(IsText))
            AppendShape(spTree, ref nextId, "Element" + nextId, element, slideCx, slideCy, slidePart, imageBytes);

        cSld.Append(spTree);
        slide.Append(cSld);
        slide.Append(new P.ColorMapOverride(new A.MasterColorMapping()));

        slidePart.Slide = slide;
        slidePart.AddPart(slideLayoutPart);
        slidePart.Slide.Save();
        return slidePart;
    }

    private static bool IsWholeSlidePicture(SlideElement element) =>
        IsPicture(element) && element.W >= 90 && element.H >= 90;

    private static bool IsPicture(SlideElement element) =>
        string.Equals(element.Kind, "picture", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(SlideElement element) =>
        string.Equals(element.Kind, "text", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(element.Text);

    /// <summary>
    /// Entfernt unsichtbare Flächen, zieht Textkästen auseinander und verbreitert Zeilen,
    /// die sonst in den Nachbarbereich umbrechen würden.
    /// </summary>
    private static List<SlideElement> PrepareLayout(IReadOnlyList<SlideElement> source, string background, long slideCx)
    {
        var elements = source
            .Where(e => !IsWholeSlidePicture(e) && !IsBackgroundPlate(e, background))
            .Select(Clone)
            .ToList();

        WidenWrappedLines(elements, slideCx);
        SeparateOverlappingText(elements);
        return elements;
    }

    /// <summary>
    /// Ersetzt geschätzte Karten und Icons durch gemessene Rahmen.
    /// Karten und Kopfleisten bleiben Formen, Icons und die Gauge bleiben Bildausschnitte.
    /// </summary>
    private static List<SlideElement> ApplyMeasuredGeometry(List<SlideElement> elements, byte[] imageBytes)
    {
        var structure = PngCropper.FindStructure(imageBytes);
        var cardCount = structure.Count(shape => shape.Kind == "roundRect");
        var measuredPictures = structure.Where(shape => shape.Kind == "picture").ToList();
        if (cardCount < 2)
        {
            if (measuredPictures.Count == 0)
                AddIconBadges(elements, imageBytes);
            else
                AddMeasuredPictures(elements, measuredPictures);
            return elements;
        }

        var merged = new List<SlideElement>();
        foreach (var shape in structure)
        {
            var element = new SlideElement
            {
                Kind = shape.Kind,
                X = shape.X,
                Y = shape.Y,
                W = shape.W,
                H = shape.H,
                Measured = true
            };
            if (shape.Kind == "roundRect")
            {
                element.Fill = SnapColor(shape.Fill) ?? "FFFFFF";
                element.Color = IsDarkHex(element.Fill) ? "009DEC" : "003C74";
            }
            else if (shape.Kind == "rect")
            {
                element.Fill = SnapColor(shape.Fill) ?? "002751";
                element.Color = element.Fill;
            }
            else if (shape.Kind == "line")
            {
                element.Color = "003C74";
            }
            merged.Add(element);
        }

        foreach (var photo in elements.Where(element => IsPicture(element) && element.W >= 28 && element.H >= 22))
        {
            var copy = Clone(photo);
            copy.Measured = true;
            merged.Add(copy);
        }

        TrimDividers(merged);
        var texts = elements.Where(IsText).Select(Clone).ToList();
        PlaceMeasuredText(texts, merged, DarkSlide(imageBytes));
        merged.RemoveAll(line =>
            string.Equals(line.Kind, "line", StringComparison.OrdinalIgnoreCase)
            && texts.Any(text => OverlapArea(line, text) > Math.Min(line.W, text.W) * 0.35));
        SeparateOverlappingText(texts);
        merged.AddRange(texts);
        return merged;
    }

    private static void TrimDividers(List<SlideElement> elements)
    {
        var pictures = elements.Where(IsPicture).Where(picture => picture.W < 16).ToList();
        foreach (var line in elements.Where(element => string.Equals(element.Kind, "line", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var picture in pictures)
            {
                if (OverlapArea(line, picture) < picture.H * 0.2) continue;
                var edge = picture.X + picture.W + 0.5;
                if (line.X < edge)
                {
                    var right = line.X + line.W;
                    line.X = edge;
                    line.W = Math.Max(4, right - edge);
                }
            }
        }
    }

    private static bool DarkSlide(byte[] imageBytes)
    {
        var (width, height) = PngCropper.Size(imageBytes);
        return width > 0 && height > width * 0.4 && PngCropper.SlideIsDark(imageBytes);
    }

    private static void PlaceMeasuredText(List<SlideElement> texts, List<SlideElement> geometry, bool darkSlide)
    {
        var pictures = geometry.Where(IsPicture).ToList();
        var headers = geometry.Where(element => element.Kind == "rect" && element.H < 14 && element.W > 12).ToList();
        var cards = geometry.Where(element => element.Kind == "roundRect").ToList();
        var used = new HashSet<SlideElement>();

        foreach (var header in headers)
        {
            SlideElement? best = null;
            var bestScore = double.MaxValue;
            foreach (var text in texts)
            {
                if (used.Contains(text) || text.Text.Length > 42) continue;
                if (text.X + 1 > header.X + header.W || text.X + text.W < header.X) continue;
                if (text.Y < header.Y - 2 || text.Y > header.Y + header.H + 12) continue;
                var score = Math.Abs(text.Y - header.Y) + text.Text.Length * 0.02;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = text;
                }
            }
            if (best is null) continue;
            used.Add(best);
            FitHeaderTitle(best, header, pictures);
        }

        var drop = new List<SlideElement>();
        foreach (var text in texts)
        {
            if (used.Contains(text)) continue;
            if (pictures.Any(picture => picture.W > 12 && picture.H > 14 && NearGraphic(text, picture)))
            {
                drop.Add(text);
                continue;
            }

            foreach (var picture in pictures.Where(picture => picture.W < 12 && picture.H < 16))
            {
                if (OverlapArea(text, picture) < picture.W * picture.H * 0.2) continue;
                var edge = picture.X + picture.W + 0.45;
                if (text.X >= edge) continue;
                var right = Math.Max(text.X + text.W, edge + 8);
                text.X = edge;
                text.W = Math.Max(6, right - edge);
            }

            var card = cards.FirstOrDefault(item => BelongsToCard(item, text));
            var onDark = darkSlide && (card is null || IsDarkHex(card.Fill));
            if (onDark && !IsLightHex(text.Color))
                text.Color = "FFFFFF";
            else if (!onDark && IsLightHex(text.Color))
                text.Color = "002751";
        }

        foreach (var text in drop)
            texts.Remove(text);
    }

    private static void FitHeaderTitle(SlideElement text, SlideElement header, List<SlideElement> pictures)
    {
        var icon = pictures
            .Where(picture => picture.X >= header.X - 0.5 && picture.X + picture.W <= header.X + header.W + 0.5
                && picture.Y + picture.H / 2 >= header.Y && picture.Y + picture.H / 2 <= header.Y + header.H)
            .OrderBy(picture => picture.X)
            .FirstOrDefault();
        var left = icon is null ? header.X + 1.4 : icon.X + icon.W + 0.7;
        text.X = left;
        text.Y = header.Y;
        text.W = Math.Max(8, header.X + header.W - left - 1.0);
        text.H = header.H;
        text.Color = IsDarkHex(header.Fill) ? "FFFFFF" : "002751";
        text.Fill = string.Empty;
        text.Bold = true;
        text.VAlign = "middle";
    }

    private static bool BelongsToCard(SlideElement card, SlideElement text)
    {
        var y = text.Y + Math.Min(text.H, 2.2) / 2d;
        return text.X >= card.X - 1.5
            && text.X <= card.X + card.W + 1.5
            && y >= card.Y
            && y <= card.Y + card.H;
    }

    private static void ReflowCard(List<SlideElement> texts, SlideElement card, SlideElement? header, List<SlideElement> pictures)
    {
        var contentTop = header is null ? card.Y + 1.0 : header.Y + header.H + 0.8;
        var contentBottom = card.Y + card.H - 1.8;
        var minY = texts.Min(text => text.Y);
        var maxY = texts.Max(text => text.Y);
        var span = Math.Max(1d, maxY - minY);
        var icons = pictures.Where(picture =>
            picture.W < 14
            && picture.X >= card.X - 0.4 && picture.X + picture.W <= card.X + card.W + 0.8
            && picture.Y >= contentTop - 1 && picture.Y <= contentBottom).ToList();

        foreach (var text in texts.OrderBy(text => text.Y))
        {
            var sourceY = text.Y;
            var relative = (sourceY - minY) / span;
            var height = Math.Clamp(Math.Max(text.H, 2.4), 2.4, 7.5);
            text.Y = contentTop + relative * Math.Max(0, contentBottom - contentTop - height);
            if (text.Y + height > contentBottom)
                text.Y = Math.Max(contentTop, contentBottom - height);
            text.H = Math.Min(height, Math.Max(1.8, contentBottom - text.Y));
            var anchorX = text.X;
            var icon = icons
                .Where(picture => Math.Abs(picture.Y - sourceY) < 8 && picture.X <= anchorX + 6)
                .OrderByDescending(picture => picture.X)
                .FirstOrDefault();
            var placedBesideIcon = false;
            if (icon is not null && card.X + card.W - (icon.X + icon.W) > 8)
            {
                var next = icons
                    .Where(picture => picture.X > icon.X + icon.W && Math.Abs(picture.Y - icon.Y) < 7)
                    .OrderBy(picture => picture.X)
                    .FirstOrDefault();
                var right = next is null ? card.X + card.W - 0.8 : next.X - 0.35;
                if (right - (icon.X + icon.W) > 7)
                {
                    text.X = icon.X + icon.W + 0.5;
                    text.W = right - text.X;
                    placedBesideIcon = true;
                }
            }
            if (!placedBesideIcon)
            {
                text.X = card.X + 0.9;
                text.W = Math.Max(6, card.W - 1.8);
            }
            var blocking = icons
                .Where(picture => OverlapArea(text, picture) >= picture.W * picture.H * 0.08 && text.X < picture.X + picture.W)
                .OrderBy(picture => picture.X)
                .FirstOrDefault();
            if (blocking is not null)
            {
                var edge = blocking.X + blocking.W + 0.45;
                var next = icons
                    .Where(picture => picture.X > blocking.X + blocking.W + 0.2)
                    .OrderBy(picture => picture.X)
                    .FirstOrDefault();
                var right = next is null ? card.X + card.W - 0.8 : next.X - 0.35;
                text.X = edge;
                text.W = Math.Max(6, right - edge);
            }
            text.H = Math.Max(text.H, Math.Min(6.5, EstimateHeight(text)));
            if (text.Y + text.H > contentBottom)
                text.Y = Math.Max(contentTop, contentBottom - text.H);
            if (IsLightHex(text.Color))
                text.Color = "002751";
            text.Fill = string.Empty;
        }
    }

    private static double EstimateHeight(SlideElement text)
    {
        var lines = (text.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var rows = 0d;
        foreach (var line in lines)
        {
            var width = Math.Max(3d, line.Length * (text.Bold ? 0.62d : 0.48d));
            rows += Math.Max(1d, Math.Ceiling(width / Math.Max(6d, text.W)));
        }
        return rows * 2.05d;
    }

    private static bool NearGraphic(SlideElement text, SlideElement picture)
    {
        var horizontal = Math.Min(text.X + text.W, picture.X + picture.W) - Math.Max(text.X, picture.X);
        if (horizontal < text.W * 0.35) return false;
        return text.Y < picture.Y + picture.H + 6.5 && text.Y + text.H > picture.Y - 0.4;
    }

    private static bool Contains(SlideElement element, double x, double y) =>
        x >= element.X && x <= element.X + element.W && y >= element.Y && y <= element.Y + element.H;

    private static bool IsDarkHex(string hex)
    {
        if (hex.Length != 6) return false;
        var luma = Convert.ToInt32(hex[..2], 16) + Convert.ToInt32(hex.Substring(2, 2), 16) + Convert.ToInt32(hex.Substring(4, 2), 16);
        return luma < 280;
    }

    private static bool IsLightHex(string? hex)
    {
        var normalized = NormalizeHex(hex);
        if (normalized is null) return false;
        var luma = Convert.ToInt32(normalized[..2], 16) + Convert.ToInt32(normalized.Substring(2, 2), 16) + Convert.ToInt32(normalized.Substring(4, 2), 16);
        return luma > 620;
    }

    /// <summary>
    /// Setzt für jedes erkannte Icon ein Bild aus dem Original, inklusive Kreis.
    /// Kleine Ersatzformen an derselben Stelle werden entfernt.
    /// </summary>
    private static void AddMeasuredPictures(List<SlideElement> elements, List<PngCropper.DetectedShape> pictures)
    {
        foreach (var picture in pictures)
        {
            if (elements.Any(existing => IsPicture(existing)
                && OverlapArea(existing, new SlideElement { X = picture.X, Y = picture.Y, W = picture.W, H = picture.H }) > picture.W * picture.H * 0.4))
                continue;
            elements.Add(new SlideElement
            {
                Kind = "picture",
                X = picture.X,
                Y = picture.Y,
                W = picture.W,
                H = picture.H,
                Measured = true
            });
        }

        elements.RemoveAll(text =>
            IsText(text)
            && pictures.Any(picture => picture.W > 14 && picture.H > 16
                && text.X + text.W / 2 >= picture.X && text.X + text.W / 2 <= picture.X + picture.W
                && text.Y + text.H / 2 >= picture.Y && text.Y + text.H / 2 <= picture.Y + picture.H));
    }

    private static void AddIconBadges(List<SlideElement> elements, byte[] imageBytes)
    {
        var badges = PngCropper.FindIconBadges(imageBytes);
        foreach (var badge in badges)
        {
            var covered = elements.Any(element =>
                IsPicture(element)
                && OverlapArea(element, badge.X, badge.Y, badge.W, badge.H) > badge.W * badge.H * 0.35d);
            if (covered) continue;

            elements.Add(new SlideElement
            {
                Kind = "picture",
                X = badge.X,
                Y = badge.Y,
                W = badge.W,
                H = badge.H
            });
        }

        elements.RemoveAll(element =>
        {
            var kind = (element.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind == "text")
            {
                var tx = element.X + element.W / 2d;
                var ty = element.Y + element.H / 2d;
                return badges.Any(badge =>
                    badge.W > 8 && tx >= badge.X && tx <= badge.X + badge.W && ty >= badge.Y && ty <= badge.Y + badge.H);
            }
            if (kind is "picture" || element.W > 18 || element.H > 18) return false;
            var cx = element.X + element.W / 2d;
            var cy = element.Y + element.H / 2d;
            return badges.Any(badge =>
                cx >= badge.X - 0.4 && cx <= badge.X + badge.W + 0.4
                && cy >= badge.Y - 0.4 && cy <= badge.Y + badge.H + 0.4);
        });

        ClearIconOverlaps(elements);
    }

    /// <summary>
    /// Kopfzeilen enden über dem Icon. Text beginnt rechts daneben.
    /// </summary>
    private static void ClearIconOverlaps(List<SlideElement> elements)
    {
        var pictures = elements.Where(IsPicture).Where(picture => picture.W < 14 && picture.H < 16).ToList();
        foreach (var picture in pictures)
        {
            foreach (var element in elements)
            {
                if (ReferenceEquals(element, picture) || IsPicture(element)) continue;
                if (OverlapArea(element, picture) < picture.W * picture.H * 0.2d) continue;

                var kind = (element.Kind ?? string.Empty).Trim().ToLowerInvariant();
                if (kind == "text")
                {
                    var edge = picture.X + picture.W + 0.8d;
                    if (element.X < edge)
                    {
                        var right = Math.Max(element.X + element.W, edge + 12d);
                        element.X = edge;
                        element.W = Math.Max(8d, right - edge);
                    }
                    continue;
                }

                if (element.Y + 1 < picture.Y && element.Y + element.H > picture.Y)
                    element.H = Math.Max(1.5d, picture.Y - element.Y - 0.4d);
            }
        }
    }

    private static SlideElement Clone(SlideElement element) => new()
    {
        Kind = element.Kind,
        X = element.X,
        Y = element.Y,
        W = element.W,
        H = element.H,
        Text = element.Text,
        FontSize = element.FontSize,
        Color = element.Color,
        Fill = element.Fill,
        Bold = element.Bold,
        Align = element.Align,
        VAlign = element.VAlign,
        Measured = element.Measured
    };

    private static bool IsBackgroundPlate(SlideElement element, string background)
    {
        var kind = (element.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is not ("rect" or "roundrect" or "ellipse")) return false;
        var fill = NormalizeHex(element.Fill);
        var stroke = NormalizeHex(element.Color);
        if (fill is null && stroke is null) return true;
        var bg = string.IsNullOrEmpty(background) ? "FFFFFF" : background;
        var fillMatches = fill is null || ColorDistance(fill, bg) < 24;
        var strokeMatches = stroke is null || stroke == fill || ColorDistance(stroke, bg) < 24;
        return fillMatches && strokeMatches;
    }

    private static int ColorDistance(string a, string b)
    {
        int Channel(string hex, int start) => Convert.ToInt32(hex.Substring(start, 2), 16);
        return Math.Abs(Channel(a, 0) - Channel(b, 0))
             + Math.Abs(Channel(a, 2) - Channel(b, 2))
             + Math.Abs(Channel(a, 4) - Channel(b, 4));
    }

    private static void WidenWrappedLines(List<SlideElement> elements, long slideCx)
    {
        var slideInches = slideCx / (double)EmuPerInch;
        foreach (var text in elements.Where(IsText))
        {
            var longest = text.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).MaxBy(l => l.Length) ?? string.Empty;
            if (longest.Length == 0) continue;

            var fontPt = text.FontSize is >= 8 and <= 96
                ? text.FontSize
                : Math.Clamp(text.H / 100d * slideInches * 72d * 0.7d, 12d, 40d);
            var neededInches = longest.Length * CharWidthEm(text.Bold, longest) * fontPt / 72d;
            var neededPercent = neededInches / slideInches * 100d + 0.8d;
            if (neededPercent <= text.W + 0.4d) continue;

            var expanded = Math.Min(100d - text.X, neededPercent);
            if (expanded <= text.W) continue;

            var trial = Clone(text);
            trial.W = expanded;
            var blocked = elements.Any(other => !ReferenceEquals(other, text) && OverlapArea(trial, other) > 0.4d);
            if (!blocked)
                text.W = expanded;
        }
    }

    private static void SeparateOverlappingText(List<SlideElement> elements)
    {
        var texts = elements.Where(IsText).ToList();
        for (var pass = 0; pass < 4; pass++)
        {
            var changed = false;
            foreach (var upper in texts)
            {
                foreach (var lower in texts)
                {
                    if (ReferenceEquals(upper, lower) || upper.Y > lower.Y) continue;
                    if (OverlapArea(upper, lower) < Math.Min(upper.W * upper.H, lower.W * lower.H) * 0.12d)
                        continue;

                    var gap = 0.5d;
                    var newH = lower.Y - upper.Y - gap;
                    if (newH >= 1.2d && newH < upper.H - 0.2d)
                    {
                        upper.H = newH;
                        changed = true;
                    }
                }
            }
            if (!changed) break;
        }
    }

    private static double OverlapArea(SlideElement a, SlideElement b) =>
        OverlapArea(a, b.X, b.Y, b.W, b.H);

    private static double OverlapArea(SlideElement a, double x, double y, double w, double h)
    {
        var width = Math.Max(0, Math.Min(a.X + a.W, x + w) - Math.Max(a.X, x));
        var height = Math.Max(0, Math.Min(a.Y + a.H, y + h) - Math.Max(a.Y, y));
        return width * height;
    }

    private static double CharWidthEm(bool bold, string sample)
    {
        var caps = sample.Count(char.IsUpper) >= Math.Max(1, sample.Count(char.IsLetter)) * 0.6d;
        if (bold && caps) return 0.70d;
        if (bold) return 0.56d;
        if (caps) return 0.62d;
        return 0.50d;
    }

    /// <summary>
    /// Schrift so verkleinern, dass die Zeilen im Kasten bleiben und nicht über Foto oder Nachbartext laufen.
    /// </summary>
    private static double FitFontSize(SlideElement element, (long X, long Y, long Cx, long Cy) box)
    {
        var lines = (element.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var widthIn = Math.Max(0.15d, box.Cx / (double)EmuPerInch);
        var heightIn = Math.Max(0.12d, box.Cy / (double)EmuPerInch);
        var size = element.FontSize is >= 8 and <= 96
            ? element.FontSize
            : Math.Clamp(heightIn * 72d / Math.Max(1, lines.Length) * 0.85d, 11d, 44d);

        for (var i = 0; i < 28; i++)
        {
            if (TextFits(lines, size, element.Bold, widthIn, heightIn))
                return Math.Round(size, 1);
            size *= 0.92d;
            if (size < 8d) return 8d;
        }

        return 8d;
    }

    private static bool TextFits(string[] lines, double fontPt, bool bold, double widthIn, double heightIn)
    {
        var lineIn = fontPt / 72d * 1.05d;
        var total = 0;
        foreach (var line in lines)
        {
            var sample = string.IsNullOrEmpty(line) ? " " : line;
            var textIn = sample.Length * CharWidthEm(bold, sample) * fontPt / 72d;
            total += Math.Max(1, (int)Math.Ceiling(textIn / widthIn - 0.001d));
        }
        return total * lineIn <= heightIn + 0.02d;
    }

    private static void AppendShape(
        P.ShapeTree tree,
        ref uint nextId,
        string name,
        SlideElement element,
        long slideCx,
        long slideCy,
        SlidePart slidePart,
        byte[] imageBytes)
    {
        var kind = (element.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind == "picture" && !element.Measured)
        {
            var snapped = PngCropper.FitIconBounds(imageBytes, element);
            var largePhoto = element.W >= 28d && element.H >= 22d;
            if (!snapped && !largePhoto) return;
        }

        var box = Box(element, slideCx, slideCy);
        if (box.Cx < 1 || box.Cy < 1) return;

        if (kind == "picture")
        {
            var crop = PngCropper.CropPercent(imageBytes, element.X, element.Y, element.W, element.H);
            if (crop is null) return;
            tree.Append(CreatePicture(slidePart, nextId++, name, crop, box));
            return;
        }

        if (kind == "text" && string.IsNullOrWhiteSpace(element.Text)) return;

        var preset = kind switch
        {
            "ellipse" => A.ShapeTypeValues.Ellipse,
            "roundrect" => A.ShapeTypeValues.RoundRectangle,
            "triangle" => A.ShapeTypeValues.Triangle,
            "diamond" => A.ShapeTypeValues.Diamond,
            "plus" => A.ShapeTypeValues.MathPlus,
            "pie" => A.ShapeTypeValues.Pie,
            "pentagon" => A.ShapeTypeValues.Pentagon,
            "chevron" => A.ShapeTypeValues.Chevron,
            "arrow" => A.ShapeTypeValues.RightArrow,
            "leftarrow" => A.ShapeTypeValues.LeftArrow,
            "uparrow" => A.ShapeTypeValues.UpArrow,
            "downarrow" => A.ShapeTypeValues.DownArrow,
            "line" => A.ShapeTypeValues.Rectangle,
            "text" => A.ShapeTypeValues.Rectangle,
            _ => A.ShapeTypeValues.Rectangle
        };

        if (kind == "line")
            box = ThinLine(box);

        var shape = new P.Shape();
        var nv = new P.NonVisualShapeProperties();
        nv.Append(new P.NonVisualDrawingProperties { Id = nextId++, Name = name });
        nv.Append(new P.NonVisualShapeDrawingProperties());
        nv.Append(new P.ApplicationNonVisualDrawingProperties());
        shape.Append(nv);

        var spPr = new P.ShapeProperties();
        var xfrm = new A.Transform2D();
        xfrm.Append(new A.Offset { X = box.X, Y = box.Y });
        xfrm.Append(new A.Extents { Cx = box.Cx, Cy = box.Cy });
        spPr.Append(xfrm);
        var geom = new A.PresetGeometry { Preset = preset };
        geom.Append(new A.AdjustValueList());
        spPr.Append(geom);

        var fill = NormalizeHex(element.Fill);
        var stroke = NormalizeHex(element.Color);
        if (kind is "rect" or "roundrect" && fill is not null && (stroke is null || stroke == fill)
            && element.W is > 8 and < 96 && element.H is > 5 and < 96
            && !IsDarkHex(fill))
            stroke = "8AA0B8";
        if (kind == "text")
        {
            if (fill is null) spPr.Append(new A.NoFill());
            else spPr.Append(new A.SolidFill(new A.RgbColorModelHex { Val = fill }));
            spPr.Append(new A.Outline(new A.NoFill()));
        }
        else if (kind == "line")
        {
            var lineColor = stroke ?? fill ?? "002751";
            spPr.Append(new A.SolidFill(new A.RgbColorModelHex { Val = lineColor }));
            spPr.Append(new A.Outline(new A.NoFill()));
        }
        else
        {
            if (fill is null) spPr.Append(new A.NoFill());
            else spPr.Append(new A.SolidFill(new A.RgbColorModelHex { Val = fill }));

            var outline = new A.Outline { Width = 15875 };
            if (fill is null && stroke is null)
                outline.Append(new A.SolidFill(new A.RgbColorModelHex { Val = "009DEC" }));
            else if (stroke is null || stroke == fill)
                outline.Append(new A.NoFill());
            else
                outline.Append(new A.SolidFill(new A.RgbColorModelHex { Val = stroke }));
            spPr.Append(outline);
        }
        shape.Append(spPr);

        if (kind == "text")
            shape.Append(CreateTextBody(element, box));
        else
            shape.Append(new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));

        tree.Append(shape);
    }

    private static P.Picture CreatePicture(SlidePart slidePart, uint id, string name, byte[] png, (long X, long Y, long Cx, long Cy) box)
    {
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(png))
            imagePart.FeedData(stream);

        var pic = new P.Picture();
        var nv = new P.NonVisualPictureProperties();
        nv.Append(new P.NonVisualDrawingProperties { Id = id, Name = name });
        var locks = new P.NonVisualPictureDrawingProperties();
        locks.Append(new A.PictureLocks { NoChangeAspect = false });
        nv.Append(locks);
        nv.Append(new P.ApplicationNonVisualDrawingProperties());
        pic.Append(nv);

        var blipFill = new P.BlipFill();
        blipFill.Append(new A.Blip { Embed = slidePart.GetIdOfPart(imagePart) });
        var stretch = new A.Stretch();
        stretch.Append(new A.FillRectangle());
        blipFill.Append(stretch);
        pic.Append(blipFill);

        var spPr = new P.ShapeProperties();
        var xfrm = new A.Transform2D();
        xfrm.Append(new A.Offset { X = box.X, Y = box.Y });
        xfrm.Append(new A.Extents { Cx = box.Cx, Cy = box.Cy });
        spPr.Append(xfrm);
        var geom = new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle };
        geom.Append(new A.AdjustValueList());
        spPr.Append(geom);
        pic.Append(spPr);
        return pic;
    }

    private static P.TextBody CreateTextBody(SlideElement element, (long X, long Y, long Cx, long Cy) box)
    {
        var anchor = (element.VAlign ?? "top").ToLowerInvariant() switch
        {
            "middle" => A.TextAnchoringTypeValues.Center,
            "bottom" => A.TextAnchoringTypeValues.Bottom,
            _ => A.TextAnchoringTypeValues.Top
        };
        var align = (element.Align ?? "left").ToLowerInvariant() switch
        {
            "center" => A.TextAlignmentTypeValues.Center,
            "right" => A.TextAlignmentTypeValues.Right,
            _ => A.TextAlignmentTypeValues.Left
        };

        var fontPt = FitFontSize(element, box);

        var color = SnapColor(element.Color) ?? "002751";
        var body = new P.TextBody();
        var bodyProps = new A.BodyProperties
        {
            Anchor = anchor,
            Wrap = A.TextWrappingValues.Square,
            LeftInset = 0,
            RightInset = 0,
            TopInset = 0,
            BottomInset = 0
        };
        bodyProps.Append(new A.NormalAutoFit());
        body.Append(bodyProps);
        body.Append(new A.ListStyle());

        foreach (var line in (element.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new A.Paragraph();
            var paragraphProps = new A.ParagraphProperties { Alignment = align };
            paragraphProps.Append(new A.LineSpacing(new A.SpacingPercent { Val = 100000 }));
            paragraphProps.Append(new A.SpaceBefore(new A.SpacingPoints { Val = 0 }));
            paragraphProps.Append(new A.SpaceAfter(new A.SpacingPoints { Val = 0 }));
            paragraph.Append(paragraphProps);
            var run = new A.Run();
            var props = new A.RunProperties
            {
                FontSize = (int)Math.Round(fontPt * 100d),
                Bold = element.Bold,
                Language = "en-US",
                Dirty = false
            };
            props.Append(new A.SolidFill(new A.RgbColorModelHex { Val = color }));
            props.Append(new A.LatinFont { Typeface = element.Bold ? "Arial Narrow" : "Arial" });
            run.Append(props);
            run.Append(new A.Text(string.IsNullOrEmpty(line) ? " " : line));
            paragraph.Append(run);
            body.Append(paragraph);
        }

        return body;
    }

    private static (long X, long Y, long Cx, long Cy) Box(SlideElement element, long slideCx, long slideCy)
    {
        var x = Percent(element.X, slideCx);
        var y = Percent(element.Y, slideCy);
        var cx = Math.Max(0, Percent(element.X + element.W, slideCx) - x);
        var cy = Math.Max(0, Percent(element.Y + element.H, slideCy) - y);
        return (x, y, cx, cy);
    }

    private static (long X, long Y, long Cx, long Cy) ThinLine((long X, long Y, long Cx, long Cy) box)
    {
        const long thickness = 12700; // 1 pt
        if (box.Cx >= box.Cy)
        {
            var y = box.Y + Math.Max(0, (box.Cy - thickness) / 2);
            return (box.X, y, box.Cx, thickness);
        }

        var x = box.X + Math.Max(0, (box.Cx - thickness) / 2);
        return (x, box.Y, thickness, box.Cy);
    }

    private static long Percent(double percent, long total)
    {
        var clamped = Math.Clamp(percent, 0d, 100d);
        return (long)Math.Round(total * clamped / 100d);
    }

    private static readonly string[] Palette =
    [
        "002751", "003C74", "001130", "FFFFFF", "000000", "F8AD3E",
        "009DEC", "003E76", "A50F14", "007973", "BFC3C6", "0083A2", "EA5B0C"
    ];

    private static string? SnapColor(string? value)
    {
        var hex = NormalizeHex(value);
        if (hex is null) return null;

        var best = Palette[0];
        var bestDistance = int.MaxValue;
        foreach (var candidate in Palette)
        {
            var distance = ColorDistance(hex, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 3 && hex.All(Uri.IsHexDigit))
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }

    // ---------- Notes Slide ----------
    private static void CreateNotesSlidePart(SlidePart slidePart, string noteText)
    {
        var notesPart = slidePart.AddNewPart<NotesSlidePart>();

        var notes = new P.NotesSlide();
        var cSld = new P.CommonSlideData();
        var spTree = new P.ShapeTree();

        var nvGrp = new P.NonVisualGroupShapeProperties();
        nvGrp.Append(new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty });
        nvGrp.Append(new P.NonVisualGroupShapeDrawingProperties());
        nvGrp.Append(new P.ApplicationNonVisualDrawingProperties());
        spTree.Append(nvGrp);

        var grpSpPr = new P.GroupShapeProperties();
        grpSpPr.Append(new A.TransformGroup());
        spTree.Append(grpSpPr);

        // Notes Placeholder Shape
        var sp = new P.Shape();
        var nvSpPr = new P.NonVisualShapeProperties();
        nvSpPr.Append(new P.NonVisualDrawingProperties { Id = 2U, Name = "Notes Placeholder" });
        var cNvSpPr = new P.NonVisualShapeDrawingProperties();
        cNvSpPr.Append(new A.ShapeLocks { NoGrouping = true });
        nvSpPr.Append(cNvSpPr);
        var appNvSpPr = new P.ApplicationNonVisualDrawingProperties();
        appNvSpPr.Append(new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U });
        nvSpPr.Append(appNvSpPr);
        sp.Append(nvSpPr);
        sp.Append(new P.ShapeProperties());

        var txBody = new P.TextBody();
        txBody.Append(new A.BodyProperties());
        txBody.Append(new A.ListStyle());

        var lines = (noteText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            var para = new A.Paragraph();
            var run = new A.Run();
            run.Append(new A.RunProperties { Language = "en-US" });
            run.Append(new A.Text(line));
            para.Append(run);
            txBody.Append(para);
        }
        sp.Append(txBody);
        spTree.Append(sp);

        cSld.Append(spTree);
        notes.Append(cSld);
        notes.Append(new P.ColorMapOverride(new A.MasterColorMapping()));

        notesPart.NotesSlide = notes;
    }

    // Lange Seite = 13,333 Zoll, kurze Seite folgt dem PNG. Unbekannte Maße: 16:9.
    private static (long Cx, long Cy) SlideSizeFor(int imgW, int imgH)
    {
        if (imgW <= 0 || imgH <= 0)
            return (LongSideEmu, FallbackHeightEmu);

        if (imgW >= imgH)
        {
            var cy = (long)Math.Round(LongSideEmu * (double)imgH / imgW);
            return (LongSideEmu, Math.Max(cy, 1));
        }

        var cx = (long)Math.Round(LongSideEmu * (double)imgW / imgH);
        return (Math.Max(cx, 1), LongSideEmu);
    }
}
