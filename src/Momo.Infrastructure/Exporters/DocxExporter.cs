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

        using (var wordDocument = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDocument.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();
            mainPart.Document.Append(body);

            // Configure Page Layout: Standard A4 size, 2.54cm margins (1440 twentieths of a point = 1 inch = 2.54 cm)
            var sectionProperties = new SectionProperties();
            var pageSize = new PageSize() { Width = 11906U, Height = 16838U }; // A4 portrait
            var pageMargin = new PageMargin() { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440 }; // 2.54cm
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
                    pSpeakerPr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "60" });
                    pSpeakerPr.Append(new OutlineLevel() { Val = 0 }); // Outline Level 1
                    pSpeaker.Append(pSpeakerPr);

                    var rSpeaker = new Run();
                    var rSpeakerPr = new RunProperties();
                    rSpeakerPr.Append(new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft JhengHei" });
                    rSpeakerPr.Append(new FontSize() { Val = "22" }); // 11pt
                    rSpeakerPr.Append(new Bold());
                    rSpeaker.Append(rSpeakerPr);
                    rSpeaker.Append(new Text(p.Speaker));
                    pSpeaker.Append(rSpeaker);
                    body.Append(pSpeaker);

                    // Paragraph 2: Timestamp (Gray, Italic, 8 pt)
                    var pTime = new Paragraph();
                    var pTimePr = new ParagraphProperties();
                    pTimePr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "60" });
                    pTime.Append(pTimePr);

                    var rTime = new Run();
                    var rTimePr = new RunProperties();
                    rTimePr.Append(new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft JhengHei" });
                    rTimePr.Append(new FontSize() { Val = "16" }); // 8pt
                    rTimePr.Append(new Italic());
                    rTimePr.Append(new Color() { Val = "888888" });
                    rTime.Append(rTimePr);
                    rTime.Append(new Text($"[{FormatTime(p.StartTime)}]"));
                    pTime.Append(rTime);
                    body.Append(pTime);

                    // Paragraph 3: Text content (14 pt)
                    var pText = new Paragraph();
                    var pTextPr = new ParagraphProperties();
                    pTextPr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "180" });
                    pText.Append(pTextPr);

                    var rText = new Run();
                    var rTextPr = new RunProperties();
                    rTextPr.Append(new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft JhengHei" });
                    rTextPr.Append(new FontSize() { Val = "28" }); // 14pt
                    rText.Append(rTextPr);
                    rText.Append(new Text(p.Text));
                    pText.Append(rText);
                    body.Append(pText);
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
            pSpacePr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "60" });
            pSpace.Append(pSpacePr);
            body.Append(pSpace);
            return;
        }

        var p = new Paragraph();
        var pPr = new ParagraphProperties();
        pPr.Append(new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "120" });
        p.Append(pPr);

        var r = new Run();
        var rPr = new RunProperties();
        rPr.Append(new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft JhengHei" });
        r.Append(rPr);

        if (trimmed.StartsWith("# "))
        {
            // Heading 1
            rPr.Append(new FontSize() { Val = "36" }); // 18pt
            rPr.Append(new Bold());
            pPr.Append(new OutlineLevel() { Val = 0 }); // Outline Level 1
            r.Append(new Text(trimmed.Substring(2).Trim()));
            p.Append(r);
        }
        else if (trimmed.StartsWith("## "))
        {
            // Heading 2
            rPr.Append(new FontSize() { Val = "28" }); // 14pt
            rPr.Append(new Bold());
            r.Append(new Text(trimmed.Substring(3).Trim()));
            p.Append(r);
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
            // Regular paragraph
            rPr.Append(new FontSize() { Val = "22" }); // 11pt

            // Support bold inline headers e.g. **Speaker_01**:
            if (trimmed.StartsWith("**") && trimmed.Contains("**:") && trimmed.Length > 4)
            {
                int idx = trimmed.IndexOf("**:", 2);
                if (idx > 0)
                {
                    var boldRun = new Run();
                    var boldPr = new RunProperties();
                    boldPr.Append(new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft JhengHei" });
                    boldPr.Append(new FontSize() { Val = "22" }); // 11pt
                    boldPr.Append(new Bold());
                    boldRun.Append(boldPr);
                    boldRun.Append(new Text(trimmed.Substring(2, idx - 2) + ": "));
                    p.Append(boldRun);

                    r.Append(new Text(trimmed.Substring(idx + 3).Trim()));
                    p.Append(r);
                    body.Append(p);
                    return;
                }
            }

            r.Append(new Text(trimmed));
            p.Append(r);
        }

        body.Append(p);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
