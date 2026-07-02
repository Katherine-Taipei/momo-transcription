using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Templates;

namespace Momo.Infrastructure.Exporters;

public class DocxExporter
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public DocxExporter(DbContextOptions<AppDbContext> dbOptions)
    {
        _dbOptions = dbOptions;
    }

    public async Task ExportAsync(string mediaFileId, string outputPath, string? templatePath = null)
    {
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await ExportAsync(mediaFileId, fileStream, templatePath);
    }

    public async Task ExportAsync(string mediaFileId, Stream outputStream, string? templatePath = null)
    {
        using var context = new AppDbContext(_dbOptions);
        var transcript = await context.Transcripts
            .FirstOrDefaultAsync(t => t.MediaFileId == mediaFileId);

        if (transcript == null) return;

        var words = await context.TranscriptWords
            .Where(w => w.TranscriptId == transcript.Id)
            .OrderBy(w => w.StartTime)
            .ToListAsync();

        var profiles = await context.SpeakerProfiles.ToListAsync();
        var profileMap = profiles.ToDictionary(p => p.Id, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        var paragraphs = ParagraphBuilder.BuildFromWords(words);

        // Map speaker IDs to global display names
        foreach (var p in paragraphs)
        {
            if (profileMap.TryGetValue(p.Speaker, out var dispName))
            {
                p.Speaker = dispName;
            }
        }

        var pendingRevisions = await context.Revisions
            .Where(r => r.TranscriptId == transcript.Id && r.Status == "pending")
            .OrderBy(r => r.VersionNumber)
            .ToListAsync();

        using (var wordDocument = WordprocessingDocument.Create(outputStream, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDocument.AddMainDocumentPart();
            
            // Register Style and Numbering Parts
            AddStylesPart(mainPart);
            AddNumberingPart(mainPart);

            if (pendingRevisions.Any())
            {
                // Write using OpenXmlPartWriter to support w:ins / w:del streamingly
                using (var writer = OpenXmlPartWriter.Create(mainPart))
                {
                    writer.WriteStartElement(new Document());
                    writer.WriteStartElement(new Body());

                    // Configure Page Layout: Standard A4 size, 2cm margins
                    writer.WriteStartElement(new SectionProperties());
                    writer.WriteElement(new PageSize() { Width = 11906U, Height = 16838U });
                    writer.WriteElement(new PageMargin() { Top = 1440, Bottom = 1440, Left = 1134, Right = 1134 }); // 2cm左右
                    writer.WriteEndElement(); // SectionProperties

                    string author = pendingRevisions.Last().CreatedBy;
                    DateTime date = pendingRevisions.Last().CreatedAt;
                    string baseText = pendingRevisions.First().SnapshotText ?? string.Empty;
                    string currentText = transcript.RawText ?? string.Empty;

                    string[] oldTokens = TokenizeText(baseText);
                    string[] newTokens = TokenizeText(currentText);
                    bool containsChinese = (baseText + currentText).Any(c => c >= 0x4E00 && c <= 0x9FFF);
                    bool addSpace = !containsChinese;

                    var diffBuilder = new DiffPlex.DiffBuilder.SideBySideDiffBuilder(new DiffPlex.Differ());
                    var oldJoined = string.Join("\n", oldTokens);
                    var newJoined = string.Join("\n", newTokens);
                    var diffModel = diffBuilder.BuildDiffModel(oldJoined, newJoined);

                    var oldLines = diffModel.OldText.Lines;
                    var newLines = diffModel.NewText.Lines;
                    int totalDiffCount = oldLines.Count;

                    int diffIndex = 0;
                    int nextRevisionId = 1;

                    foreach (var p in paragraphs)
                    {
                        // 1. Speaker Paragraph
                        writer.WriteStartElement(new Paragraph());
                        writer.WriteStartElement(new ParagraphProperties());
                        writer.WriteElement(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
                        writer.WriteElement(new OutlineLevel() { Val = 0 });
                        writer.WriteEndElement(); // ParagraphProperties

                        writer.WriteStartElement(new Run());
                        writer.WriteStartElement(new RunProperties());
                        writer.WriteElement(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
                        writer.WriteElement(new FontSize() { Val = "24" }); // Chinese 12pt default
                        writer.WriteElement(new Bold());
                        writer.WriteEndElement(); // RunProperties
                        writer.WriteElement(new Text(p.Speaker));
                        writer.WriteEndElement(); // Run
                        writer.WriteEndElement(); // Paragraph

                        // 2. Timestamp Paragraph
                        writer.WriteStartElement(new Paragraph());
                        writer.WriteStartElement(new ParagraphProperties());
                        writer.WriteElement(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
                        writer.WriteEndElement(); // ParagraphProperties

                        writer.WriteStartElement(new Run());
                        writer.WriteStartElement(new RunProperties());
                        writer.WriteElement(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
                        writer.WriteElement(new FontSize() { Val = "16" }); // 8pt
                        writer.WriteElement(new Italic());
                        writer.WriteElement(new Color() { Val = "888888" });
                        writer.WriteEndElement(); // RunProperties
                        writer.WriteElement(new Text($"[{FormatTime(p.StartTime)}]"));
                        writer.WriteEndElement(); // Run
                        writer.WriteEndElement(); // Paragraph

                        // 3. Text Paragraph
                        writer.WriteStartElement(new Paragraph());
                        writer.WriteStartElement(new ParagraphProperties());
                        writer.WriteElement(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
                        writer.WriteEndElement(); // ParagraphProperties

                        string[] pTokens = TokenizeText(p.Text);
                        int pTokenIndex = 0;

                        while (diffIndex < totalDiffCount)
                        {
                            var oldLine = oldLines[diffIndex];
                            var newLine = newLines[diffIndex];

                            if (oldLine.Type == DiffPlex.DiffBuilder.Model.ChangeType.Deleted)
                            {
                                string delText = oldLine.Text + (addSpace ? " " : "");
                                WriteDiffRun(writer, delText, false, true, author, date, ref nextRevisionId);
                                diffIndex++;
                            }
                            else if (oldLine.Type == DiffPlex.DiffBuilder.Model.ChangeType.Modified)
                            {
                                string delText = oldLine.Text + (addSpace ? " " : "");
                                WriteDiffRun(writer, delText, false, true, author, date, ref nextRevisionId);

                                if (pTokenIndex < pTokens.Length)
                                {
                                    string tokenText = pTokens[pTokenIndex] + (addSpace ? " " : "");
                                    WriteDiffRun(writer, tokenText, true, false, author, date, ref nextRevisionId);
                                    pTokenIndex++;
                                }

                                diffIndex++;
                            }
                            else if (pTokenIndex < pTokens.Length)
                            {
                                string tokenText = pTokens[pTokenIndex] + (addSpace ? " " : "");
                                bool isIns = newLine.Type == DiffPlex.DiffBuilder.Model.ChangeType.Inserted;
                                WriteDiffRun(writer, tokenText, isIns, false, author, date, ref nextRevisionId);
                                pTokenIndex++;
                                diffIndex++;
                            }
                            else
                            {
                                break;
                            }
                        }

                        // Write remaining deleted tokens at the end of paragraph if any
                        while (diffIndex < totalDiffCount && 
                              (oldLines[diffIndex].Type == DiffPlex.DiffBuilder.Model.ChangeType.Deleted || 
                               oldLines[diffIndex].Type == DiffPlex.DiffBuilder.Model.ChangeType.Modified))
                        {
                            var oldLine = oldLines[diffIndex];
                            string delText = oldLine.Text + (addSpace ? " " : "");
                            WriteDiffRun(writer, delText, false, true, author, date, ref nextRevisionId);

                            if (oldLine.Type == DiffPlex.DiffBuilder.Model.ChangeType.Modified && pTokenIndex < pTokens.Length)
                            {
                                string tokenText = pTokens[pTokenIndex] + (addSpace ? " " : "");
                                WriteDiffRun(writer, tokenText, true, false, author, date, ref nextRevisionId);
                                pTokenIndex++;
                            }

                            diffIndex++;
                        }

                        writer.WriteEndElement(); // Paragraph
                    }

                    writer.WriteEndElement(); // Body
                    writer.WriteEndElement(); // Document
                }
            }
            else
            {
                mainPart.Document = new Document();
                var body = new Body();
                mainPart.Document.Append(body);

                // Configure Page Layout: Standard A4 size, 2cm margins
                var sectionProperties = new SectionProperties();
                var pageSize = new PageSize() { Width = 11906U, Height = 16838U }; // A4 portrait
                var pageMargin = new PageMargin() { Top = 1440, Bottom = 1440, Left = 1134, Right = 1134 }; // 2cm左右
                sectionProperties.Append(pageSize);
                sectionProperties.Append(pageMargin);
                body.Append(sectionProperties);

                if (!string.IsNullOrEmpty(templatePath) && File.Exists(templatePath))
                {
                    var mediaFile = await context.MediaFiles
                        .Include(m => m.Project)
                        .FirstOrDefaultAsync(m => m.Id == mediaFileId);

                    string projectName = mediaFile?.Project?.Name ?? "Default Project";
                    double durationSeconds = mediaFile?.DurationSeconds ?? 0.0;
                    if (durationSeconds == 0.0 && words.Count > 0)
                    {
                        durationSeconds = words.Max(w => w.EndTime);
                    }
                    double durationMinutes = Math.Round(durationSeconds / 60.0, 1);

                    var job = await context.Jobs
                        .Where(j => j.MediaFileId == mediaFileId && j.Status == "COMPLETED")
                        .OrderByDescending(j => j.UpdatedAt)
                        .FirstOrDefaultAsync();
                    string roleName = job?.SelectedRole ?? "Standard";
                    if (roleName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    {
                        roleName = Path.GetFileNameWithoutExtension(roleName);
                    }

                    var speakerDurations = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in paragraphs)
                    {
                        double diff = p.EndTime - p.StartTime;
                        if (speakerDurations.ContainsKey(p.Speaker))
                            speakerDurations[p.Speaker] += diff;
                        else
                            speakerDurations[p.Speaker] = diff;
                    }

                    var speakersList = speakerDurations.Select(kvp => new {
                        SpeakerName = kvp.Key,
                        SpeakerDuration = Math.Round(kvp.Value, 1)
                    }).ToList();

                    var paragraphsList = paragraphs.Select(p => new {
                        Timestamp = FormatTime(p.StartTime),
                        SpeakerName = p.Speaker,
                        Text = p.Text
                    }).ToList();

                    var templateContext = new {
                        ProjectName = projectName,
                        DurationMinutes = durationMinutes,
                        RoleName = roleName,
                        Speakers = speakersList,
                        Paragraphs = paragraphsList
                    };

                    string templateContent = await File.ReadAllTextAsync(templatePath);
                    var engine = new StubbleTemplateEngine();
                    string rendered = engine.Render(templateContent, templateContext);

                    // Split rendered output line-by-line and convert Markdown constructs to OpenXML
                    var lines = rendered.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    foreach (var line in lines)
                    {
                        AddMarkdownParagraph(body, line);
                    }
                }
                else
                {
                    // Default layout (backward compatibility)
                    foreach (var p in paragraphs)
                    {
                        // Paragraph 1: Speaker (Bold, Outline level 1)
                        var pSpeaker = new Paragraph();
                        var pSpeakerPr = new ParagraphProperties();
                        pSpeakerPr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
                        pSpeakerPr.Append(new OutlineLevel() { Val = 0 }); // Outline Level 1
                        pSpeaker.Append(pSpeakerPr);

                        var rSpeaker = new Run();
                        var rSpeakerPr = new RunProperties();
                        rSpeakerPr.Append(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
                        rSpeakerPr.Append(new FontSize() { Val = "24" }); // Speaker bold 12pt
                        rSpeakerPr.Append(new Bold());
                        rSpeaker.Append(rSpeakerPr);
                        rSpeaker.Append(new Text(p.Speaker));
                        pSpeaker.Append(rSpeaker);
                        body.Append(pSpeaker);

                        // Paragraph 2: Timestamp (Gray, Italic, 8 pt)
                        var pTime = new Paragraph();
                        var pTimePr = new ParagraphProperties();
                        pTimePr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
                        pTime.Append(pTimePr);

                        var rTime = new Run();
                        var rTimePr = new RunProperties();
                        rTimePr.Append(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
                        rTimePr.Append(new FontSize() { Val = "16" }); // 8pt
                        rTimePr.Append(new Italic());
                        rTimePr.Append(new Color() { Val = "888888" });
                        rTime.Append(rTimePr);
                        rTime.Append(new Text($"[{FormatTime(p.StartTime)}]"));
                        pTime.Append(rTime);
                        body.Append(pTime);

                        // Paragraph 3: Text content (12pt Chinese / 11pt English)
                        var pText = new Paragraph();
                        var pTextPr = new ParagraphProperties();
                        pTextPr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
                        pText.Append(pTextPr);

                        AppendFormattedText(pText, p.Text);
                        body.Append(pText);
                    }
                }
            }
        }
    }

    private void AddMarkdownParagraph(Body body, string line)
    {
        string trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // Empty line translates to spacing
            var pSpace = new Paragraph();
            var pSpacePr = new ParagraphProperties();
            pSpacePr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
            pSpace.Append(pSpacePr);
            body.Append(pSpace);
            return;
        }

        var p = new Paragraph();
        var pPr = new ParagraphProperties();
        pPr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" });
        p.Append(pPr);

        var r = new Run();
        var rPr = new RunProperties();
        rPr.Append(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
        r.Append(rPr);

        if (trimmed.StartsWith("###### "))
        {
            SetHeadingParagraph(p, pPr, r, rPr, trimmed.Substring(7).Trim(), 5, "22", false);
        }
        else if (trimmed.StartsWith("##### "))
        {
            SetHeadingParagraph(p, pPr, r, rPr, trimmed.Substring(6).Trim(), 4, "22", false);
        }
        else if (trimmed.StartsWith("#### "))
        {
            SetHeadingParagraph(p, pPr, r, rPr, trimmed.Substring(5).Trim(), 3, "22", false);
        }
        else if (trimmed.StartsWith("### "))
        {
            SetHeadingParagraph(p, pPr, r, rPr, trimmed.Substring(4).Trim(), 2, "22", false);
        }
        else if (trimmed.StartsWith("## "))
        {
            SetHeadingParagraph(p, pPr, r, rPr, trimmed.Substring(3).Trim(), 1, "24", true);
        }
        else if (trimmed.StartsWith("# "))
        {
            SetHeadingParagraph(p, pPr, r, rPr, trimmed.Substring(2).Trim(), 0, "24", true);
        }
        else if (trimmed.StartsWith("* ") || trimmed.StartsWith("- "))
        {
            // Bullet Point
            rPr.Append(new FontSize() { Val = "22" }); // 11pt
            r.Append(new Text("•  " + trimmed.Substring(2).Trim()));
            p.Append(r);
        }
        else
        {
            // Regular paragraph, support bold inline headers e.g. **Speaker_01**:
            if (trimmed.StartsWith("**") && trimmed.Contains("**:") && trimmed.Length > 4)
            {
                int idx = trimmed.IndexOf("**:", 2);
                if (idx > 0)
                {
                    var boldRun = new Run();
                    var boldPr = new RunProperties();
                    boldPr.Append(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
                    boldPr.Append(new FontSize() { Val = "22" }); // 11pt
                    boldPr.Append(new Bold());
                    boldRun.Append(boldPr);
                    boldRun.Append(new Text(trimmed.Substring(2, idx - 2) + ": "));
                    p.Append(boldRun);

                    AppendFormattedText(p, trimmed.Substring(idx + 3).Trim());
                    body.Append(p);
                    return;
                }
            }

            AppendFormattedText(p, trimmed);
        }

        body.Append(p);
    }

    private void SetHeadingParagraph(Paragraph p, ParagraphProperties pPr, Run r, RunProperties rPr, string text, int level, string fontSizeVal, bool isBold)
    {
        pPr.Append(new OutlineLevel() { Val = level });
        
        var numPr = new NumberingProperties();
        numPr.Append(new NumberingLevelReference() { Val = level });
        numPr.Append(new NumberingId() { Val = 1 });
        pPr.Append(numPr);
        
        rPr.Append(new FontSize() { Val = fontSizeVal });
        if (isBold)
        {
            rPr.Append(new Bold());
        }
        
        r.Append(new Text(text));
        p.Append(r);
    }

    private void WriteDiffRun(OpenXmlWriter writer, string text, bool isInserted, bool isDeleted, string author, DateTime date, ref int nextRevisionId)
    {
        bool isChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        string sizeVal = isChinese ? "24" : "22";

        if (isDeleted)
        {
            writer.WriteStartElement(new DeletedRun() { Author = author, Date = date, Id = (nextRevisionId++).ToString() });
            writer.WriteStartElement(new Run());
            writer.WriteStartElement(new RunProperties());
            writer.WriteElement(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
            writer.WriteElement(new FontSize() { Val = sizeVal });
            writer.WriteEndElement(); // RunProperties
            writer.WriteElement(new DeletedText(text) { Space = SpaceProcessingModeValues.Preserve });
            writer.WriteEndElement(); // Run
            writer.WriteEndElement(); // DeletedRun
        }
        else if (isInserted)
        {
            writer.WriteStartElement(new InsertedRun() { Author = author, Date = date, Id = (nextRevisionId++).ToString() });
            writer.WriteStartElement(new Run());
            writer.WriteStartElement(new RunProperties());
            writer.WriteElement(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
            writer.WriteElement(new FontSize() { Val = sizeVal });
            writer.WriteEndElement(); // RunProperties
            writer.WriteElement(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            writer.WriteEndElement(); // Run
            writer.WriteEndElement(); // InsertedRun
        }
        else
        {
            writer.WriteStartElement(new Run());
            writer.WriteStartElement(new RunProperties());
            writer.WriteElement(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
            writer.WriteElement(new FontSize() { Val = sizeVal });
            writer.WriteEndElement(); // RunProperties
            writer.WriteElement(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            writer.WriteEndElement(); // Run
        }
    }

    private void AppendFormattedText(Paragraph p, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        bool? currentIsChinese = null;
        var sb = new StringBuilder();

        foreach (var c in text)
        {
            bool isCh = c >= 0x4E00 && c <= 0x9FFF;
            if (currentIsChinese == null)
            {
                currentIsChinese = isCh;
            }
            else if (currentIsChinese != isCh)
            {
                AddTextRun(p, sb.ToString(), currentIsChinese.Value);
                sb.Clear();
                currentIsChinese = isCh;
            }
            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            AddTextRun(p, sb.ToString(), currentIsChinese ?? false);
        }
    }

    private void AddTextRun(Paragraph p, string runText, bool isChinese)
    {
        var r = new Run();
        var rPr = new RunProperties();
        rPr.Append(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" });
        rPr.Append(new FontSize() { Val = isChinese ? "24" : "22" });
        r.Append(rPr);
        r.Append(new Text(runText) { Space = SpaceProcessingModeValues.Preserve });
        p.Append(r);
    }

    private static void AddStylesPart(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        var docDefaults = new DocDefaults(
            new RunPropertiesDefault(
                new RunProperties(
                    new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" },
                    new FontSize() { Val = "22" }
                )
            ),
            new ParagraphPropertiesDefault(
                new ParagraphProperties(
                    new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, Before = "120", After = "120" }
                )
            )
        );
        styles.Append(docDefaults);
        stylesPart.Styles = styles;
    }

    private static void AddNumberingPart(MainDocumentPart mainPart)
    {
        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        var numbering = new Numbering();

        var abstractNum = new AbstractNum() { AbstractNumberId = 1 };
        abstractNum.Append(new MultiLevelType() { Val = MultiLevelValues.Multilevel });

        // Level 0: 一、
        var lvl0 = new Level() { LevelIndex = 0 };
        lvl0.Append(new StartNumberingValue() { Val = 1 });
        lvl0.Append(new NumberingFormat() { Val = NumberFormatValues.ChineseCountingThousand });
        lvl0.Append(new LevelText() { Val = "%1、" });
        lvl0.Append(new LevelJustification() { Val = LevelJustificationValues.Left });
        lvl0.Append(new PreviousParagraphProperties(new Indentation() { Left = "480", Hanging = "480" }));
        lvl0.Append(new NumberingSymbolRunProperties(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" }));
        abstractNum.Append(lvl0);

        // Level 1: （一）
        var lvl1 = new Level() { LevelIndex = 1 };
        lvl1.Append(new StartNumberingValue() { Val = 1 });
        lvl1.Append(new NumberingFormat() { Val = NumberFormatValues.ChineseCounting });
        lvl1.Append(new LevelText() { Val = "（%2）" });
        lvl1.Append(new LevelJustification() { Val = LevelJustificationValues.Left });
        lvl1.Append(new PreviousParagraphProperties(new Indentation() { Left = "720", Hanging = "720" }));
        lvl1.Append(new NumberingSymbolRunProperties(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" }));
        abstractNum.Append(lvl1);

        // Level 2: 1、
        var lvl2 = new Level() { LevelIndex = 2 };
        lvl2.Append(new StartNumberingValue() { Val = 1 });
        lvl2.Append(new NumberingFormat() { Val = NumberFormatValues.Decimal });
        lvl2.Append(new LevelText() { Val = "%3、" });
        lvl2.Append(new LevelJustification() { Val = LevelJustificationValues.Left });
        lvl2.Append(new PreviousParagraphProperties(new Indentation() { Left = "960", Hanging = "960" }));
        lvl2.Append(new NumberingSymbolRunProperties(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" }));
        abstractNum.Append(lvl2);

        // Level 3: （1）
        var lvl3 = new Level() { LevelIndex = 3 };
        lvl3.Append(new StartNumberingValue() { Val = 1 });
        lvl3.Append(new NumberingFormat() { Val = NumberFormatValues.Decimal });
        lvl3.Append(new LevelText() { Val = "（%4）" });
        lvl3.Append(new LevelJustification() { Val = LevelJustificationValues.Left });
        lvl3.Append(new PreviousParagraphProperties(new Indentation() { Left = "1200", Hanging = "1200" }));
        lvl3.Append(new NumberingSymbolRunProperties(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" }));
        abstractNum.Append(lvl3);

        // Level 4: A、
        var lvl4 = new Level() { LevelIndex = 4 };
        lvl4.Append(new StartNumberingValue() { Val = 1 });
        lvl4.Append(new NumberingFormat() { Val = NumberFormatValues.UpperLetter });
        lvl4.Append(new LevelText() { Val = "%5、" });
        lvl4.Append(new LevelJustification() { Val = LevelJustificationValues.Left });
        lvl4.Append(new PreviousParagraphProperties(new Indentation() { Left = "1440", Hanging = "1440" }));
        lvl4.Append(new NumberingSymbolRunProperties(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" }));
        abstractNum.Append(lvl4);

        // Level 5: a、
        var lvl5 = new Level() { LevelIndex = 5 };
        lvl5.Append(new StartNumberingValue() { Val = 1 });
        lvl5.Append(new NumberingFormat() { Val = NumberFormatValues.LowerLetter });
        lvl5.Append(new LevelText() { Val = "%6、" });
        lvl5.Append(new LevelJustification() { Val = LevelJustificationValues.Left });
        lvl5.Append(new PreviousParagraphProperties(new Indentation() { Left = "1680", Hanging = "1680" }));
        lvl5.Append(new NumberingSymbolRunProperties(new RunFonts() { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "DFKai-SB" }));
        abstractNum.Append(lvl5);

        numbering.Append(abstractNum);

        var numInstance = new NumberingInstance() { NumberID = 1 };
        numInstance.Append(new AbstractNumId() { Val = 1 });
        numbering.Append(numInstance);

        numberingPart.Numbering = numbering;
    }

    private string[] TokenizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        
        bool containsChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        if (containsChinese)
        {
            return text.Select(c => c.ToString()).ToArray();
        }
        else
        {
            return text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
