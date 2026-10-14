using System.IO.Packaging;
using System.Reflection;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SlideGenerator.Core.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SlideGenerator.Core.Services;

/// <summary>
/// Eine Folie: das Originalbild als Hintergrund, der Text darüber als bearbeitbare Schrift.
/// Icons, Pfeile und Flächen bleiben im Bild, damit die Folie wie die Vorlage aussieht.
/// </summary>
public sealed class PptxExportService : IPptxExportService
{
    // LibreOffice 24.2 legt die Glyphen gegenüber der Textbox-Kante um diesen Anteil der Schriftgröße versetzt.
    // X negativ schiebt die Box nach links, Y negativ nach oben, damit die Schrift auf dem Original sitzt.
    private const double ShiftX = -0.02;
    private const double ShiftY = -0.18;

    public byte[] BuildPptx(byte[] imageBytes, SlideLayout layout, string? userPrompt = null)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            throw new ArgumentException("imageBytes ist leer", nameof(imageBytes));
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var fitted = SlideTextFit.Fit(imageBytes, layout);
        var (imgW, imgH) = PngCropper.Size(fitted.BackgroundPng);
        var (slideCx, slideCy) = SlideMetrics.Size(imgW, imgH);

        using var ms = new MemoryStream();
        using (var pptx = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var presentationPart = pptx.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            CreatePresentationParts(presentationPart, fitted, userPrompt, imgW, imgH, slideCx, slideCy);
        }
        return ms.ToArray();
    }

    private static void CreatePresentationParts(
        PresentationPart presentationPart,
        SlideTextFit.FittedSlide fitted,
        string? userPrompt,
        int imageW,
        int imageH,
        long slideCx,
        long slideCy)
    {
        var slideMasterPart = CreateSlideMasterPart(presentationPart);
        var slideLayoutPart = CreateSlideLayoutPart(slideMasterPart);
        CreateThemePart(slideMasterPart);
        EmbedFonts(presentationPart);
        var slidePart = CreateSlidePart(presentationPart, slideLayoutPart, fitted, imageW, imageH, slideCx, slideCy);

        if (!string.IsNullOrWhiteSpace(userPrompt))
            CreateNotesSlidePart(slidePart, userPrompt!);

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

    private static void EmbedFonts(PresentationPart presentationPart)
    {
        var list = new P.EmbeddedFontList();
        EmbedFamily(presentationPart, list, SlideTextFit.CondensedTypeface, "BarlowCondensed-Medium.ttf", "BarlowCondensed-Bold.ttf");
        EmbedFamily(presentationPart, list, SlideTextFit.RegularTypeface, "Barlow-Medium.ttf", "Barlow-Bold.ttf");
        presentationPart.Presentation.EmbeddedFontList = list;
    }

    private static void EmbedFamily(PresentationPart presentationPart, P.EmbeddedFontList list, string typeface, string regularFile, string boldFile)
    {
        var embedded = new P.EmbeddedFont();
        embedded.Append(new P.Font { Typeface = typeface, PitchFamily = 34, CharacterSet = 0 });
        embedded.Append(CreateFontSlot(presentationPart, regularFile, bold: false));
        embedded.Append(CreateFontSlot(presentationPart, boldFile, bold: true));
        list.Append(embedded);
    }

    private static OpenXmlElement CreateFontSlot(PresentationPart presentationPart, string file, bool bold)
    {
        var key = Guid.NewGuid();
        var bytes = FontBytes(file);
        var id = AddObfuscatedFont(presentationPart, key, bytes);
        return bold
            ? new P.BoldFont { Id = id }
            : new P.RegularFont { Id = id };
    }

    /// <summary>
    /// ECMA-376 liest die GUID aus dem Dateinamen. PowerPoint entschlüsselt die Schrift nur, wenn der Name diese GUID trägt.
    /// </summary>
    private static string AddObfuscatedFont(PresentationPart presentationPart, Guid key, byte[] bytes)
    {
        // IPackage liegt in DocumentFormat.OpenXml.Framework und ist für den Compiler nicht sichtbar.
        var package = presentationPart.OpenXmlPackage;
        var zip = package.GetType().GetProperty("Package", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(package)
            ?? throw new InvalidOperationException("Schriftpaket fehlt.");
        var name = key.ToString("D").ToUpperInvariant();
        var partUri = new Uri("/ppt/fonts/" + name + ".odttf", UriKind.Relative);
        var created = zip.GetType().GetMethod("CreatePart")!.Invoke(
            zip,
            new object[]
            {
                partUri,
                "application/vnd.openxmlformats-officedocument.obfuscatedFont",
                CompressionOption.Normal
            })!;
        var obfuscated = (byte[])bytes.Clone();
        Obfuscate(obfuscated, key);
        using (var dest = (Stream)created.GetType().GetMethod("GetStream")!.Invoke(created, new object[] { FileMode.Create, FileAccess.Write })!)
            dest.Write(obfuscated, 0, obfuscated.Length);

        var owner = zip.GetType().GetMethod("GetPart")!.Invoke(zip, new object[] { presentationPart.Uri })!;
        var relationships = owner.GetType().GetProperty("Relationships")!.GetValue(owner)!;
        var rel = relationships.GetType().GetMethod("Create")!.Invoke(
            relationships,
            new object?[]
            {
                partUri,
                TargetMode.Internal,
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/font",
                "rId" + name.Replace("-", string.Empty)[..12]
            })!;
        return (string)rel.GetType().GetProperty("Id")!.GetValue(rel)!;
    }

    private static byte[] FontBytes(string file)
    {
        var name = "SlideGenerator.Core.Fonts." + file;
        using var stream = typeof(PptxExportService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Schrift fehlt: " + name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>ECMA-376: die ersten 32 Bytes werden mit der GUID xor-verknüpft, damit PowerPoint die Schrift annimmt.</summary>
    private static void Obfuscate(byte[] data, Guid key)
    {
        var guid = key.ToByteArray();
        for (var i = 0; i < 32 && i < data.Length; i++)
            data[i] ^= guid[guid.Length - (i % guid.Length) - 1];
    }

    private static SlideMasterPart CreateSlideMasterPart(PresentationPart presentationPart)
    {
        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var slideMaster = new P.SlideMaster();
        var shapeTree = EmptyShapeTree();
        slideMaster.Append(new P.CommonSlideData(shapeTree));
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

    private static SlideLayoutPart CreateSlideLayoutPart(SlideMasterPart slideMasterPart)
    {
        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        var slideLayout = new P.SlideLayout { Type = P.SlideLayoutValues.Blank, Preserve = true };
        slideLayout.Append(new P.CommonSlideData(EmptyShapeTree()) { Name = "Blank" });
        slideLayout.Append(new P.ColorMapOverride(new A.MasterColorMapping()));
        slideLayoutPart.SlideLayout = slideLayout;
        slideLayoutPart.AddPart(slideMasterPart);
        slideMasterPart.SlideMaster.SlideLayoutIdList = new P.SlideLayoutIdList(
            new P.SlideLayoutId
            {
                Id = 2147483649U,
                RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart)
            });
        slideMasterPart.SlideMaster.Save();
        return slideLayoutPart;
    }

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
                        new A.LatinFont { Typeface = SlideTextFit.Typeface },
                        new A.EastAsianFont { Typeface = string.Empty },
                        new A.ComplexScriptFont { Typeface = string.Empty }),
                    new A.MinorFont(
                        new A.LatinFont { Typeface = SlideTextFit.Typeface },
                        new A.EastAsianFont { Typeface = string.Empty },
                        new A.ComplexScriptFont { Typeface = string.Empty }))
                { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                    new A.LineStyleList(
                        new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.PresetDash { Val = A.PresetLineDashValues.Solid }),
                        new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.PresetDash { Val = A.PresetLineDashValues.Solid }),
                        new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.PresetDash { Val = A.PresetLineDashValues.Solid })),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
                { Name = "Office" }),
            new A.ObjectDefaults(),
            new A.ExtraColorSchemeList())
        { Name = "Office Theme" };
        return themePart;
    }

    private static SlidePart CreateSlidePart(
        PresentationPart presentationPart,
        SlideLayoutPart slideLayoutPart,
        SlideTextFit.FittedSlide fitted,
        int imageW,
        int imageH,
        long slideCx,
        long slideCy)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        var tree = EmptyShapeTree();
        var nextId = 2U;
        tree.Append(CreatePicture(slidePart, nextId++, "Background", fitted.BackgroundPng, 0, 0, slideCx, slideCy));

        var pxPerPoint = SlideMetrics.PixelsPerPoint(imageW, slideCx);
        var emuPerPxX = slideCx / (double)imageW;
        var emuPerPxY = slideCy / (double)imageH;
        foreach (var line in fitted.Lines)
        {
            var fontPx = line.FontSizePt * pxPerPoint;
            var x = (long)Math.Round(line.X / 100d * slideCx + fontPx * ShiftX * emuPerPxX);
            var y = (long)Math.Round(line.Y / 100d * slideCy + fontPx * ShiftY * emuPerPxY);
            var cx = Math.Max(1, (long)Math.Round(line.W / 100d * slideCx + fontPx * 0.25 * emuPerPxX));
            var cy = Math.Max(1, (long)Math.Round(Math.Max(line.H / 100d * slideCy, fontPx * 1.35 * emuPerPxY)));
            tree.Append(CreateText(nextId++, line, x, y, cx, cy));
        }

        var slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
        slidePart.Slide = slide;
        slidePart.AddPart(slideLayoutPart);
        slidePart.Slide.Save();
        return slidePart;
    }

    private static P.Picture CreatePicture(SlidePart slidePart, uint id, string name, byte[] png, long x, long y, long cx, long cy)
    {
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(png))
            imagePart.FeedData(stream);

        var picture = new P.Picture();
        var nv = new P.NonVisualPictureProperties();
        nv.Append(new P.NonVisualDrawingProperties { Id = id, Name = name });
        var locks = new P.NonVisualPictureDrawingProperties();
        locks.Append(new A.PictureLocks { NoChangeAspect = true });
        nv.Append(locks);
        nv.Append(new P.ApplicationNonVisualDrawingProperties());
        picture.Append(nv);

        var blipFill = new P.BlipFill();
        blipFill.Append(new A.Blip { Embed = slidePart.GetIdOfPart(imagePart) });
        blipFill.Append(new A.Stretch(new A.FillRectangle()));
        picture.Append(blipFill);

        var props = new P.ShapeProperties();
        props.Append(new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }));
        props.Append(new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
        picture.Append(props);
        return picture;
    }

    private static P.Shape CreateText(uint id, SlideTextFit.FittedLine line, long x, long y, long cx, long cy)
    {
        var shape = new P.Shape();
        var nv = new P.NonVisualShapeProperties();
        nv.Append(new P.NonVisualDrawingProperties { Id = id, Name = line.Text });
        nv.Append(new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = false }));
        nv.Append(new P.ApplicationNonVisualDrawingProperties());
        shape.Append(nv);

        var props = new P.ShapeProperties();
        props.Append(new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }));
        props.Append(new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
        props.Append(new A.NoFill());
        props.Append(new A.Outline(new A.NoFill()));
        shape.Append(props);

        var body = new P.TextBody();
        var bodyProps = new A.BodyProperties
        {
            Anchor = A.TextAnchoringTypeValues.Top,
            Wrap = A.TextWrappingValues.None,
            LeftInset = 0,
            RightInset = 0,
            TopInset = 0,
            BottomInset = 0,
            HorizontalOverflow = A.TextHorizontalOverflowValues.Overflow
        };
        bodyProps.Append(new A.NoAutoFit());
        body.Append(bodyProps);
        body.Append(new A.ListStyle());

        var paragraph = new A.Paragraph();
        var paragraphProps = new A.ParagraphProperties { Alignment = A.TextAlignmentTypeValues.Left };
        paragraphProps.Append(new A.LineSpacing(new A.SpacingPercent { Val = 100000 }));
        paragraphProps.Append(new A.SpaceBefore(new A.SpacingPoints { Val = 0 }));
        paragraphProps.Append(new A.SpaceAfter(new A.SpacingPoints { Val = 0 }));
        paragraph.Append(paragraphProps);

        var run = new A.Run();
        var runProps = new A.RunProperties
        {
            FontSize = (int)Math.Round(line.FontSizePt * 100d),
            Bold = line.Bold,
            Spacing = (int)Math.Round(line.TrackingPt * 100d),
            Language = "en-US",
            Dirty = false
        };
        runProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = line.Color }));
        runProps.Append(new A.LatinFont { Typeface = line.Typeface });
        runProps.Append(new A.EastAsianFont { Typeface = line.Typeface });
        runProps.Append(new A.ComplexScriptFont { Typeface = line.Typeface });
        run.Append(runProps);
        run.Append(new A.Text(line.Text));
        paragraph.Append(run);
        body.Append(paragraph);
        shape.Append(body);
        return shape;
    }

    private static P.ShapeTree EmptyShapeTree()
    {
        var tree = new P.ShapeTree();
        var nv = new P.NonVisualGroupShapeProperties();
        nv.Append(new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty });
        nv.Append(new P.NonVisualGroupShapeDrawingProperties());
        nv.Append(new P.ApplicationNonVisualDrawingProperties());
        tree.Append(nv);
        tree.Append(new P.GroupShapeProperties(new A.TransformGroup(
            new A.Offset { X = 0, Y = 0 },
            new A.Extents { Cx = 0, Cy = 0 },
            new A.ChildOffset { X = 0, Y = 0 },
            new A.ChildExtents { Cx = 0, Cy = 0 })));
        return tree;
    }

    private static void CreateNotesSlidePart(SlidePart slidePart, string noteText)
    {
        var notesPart = slidePart.AddNewPart<NotesSlidePart>();
        var tree = EmptyShapeTree();
        var shape = new P.Shape();
        var nv = new P.NonVisualShapeProperties();
        nv.Append(new P.NonVisualDrawingProperties { Id = 2U, Name = "Notes Placeholder" });
        nv.Append(new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }));
        var app = new P.ApplicationNonVisualDrawingProperties();
        app.Append(new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U });
        nv.Append(app);
        shape.Append(nv);
        shape.Append(new P.ShapeProperties());
        var body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var line in noteText.Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new A.Paragraph();
            var run = new A.Run();
            run.Append(new A.RunProperties { Language = "en-US" });
            run.Append(new A.Text(line));
            paragraph.Append(run);
            body.Append(paragraph);
        }
        shape.Append(body);
        tree.Append(shape);
        notesPart.NotesSlide = new P.NotesSlide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }
}
