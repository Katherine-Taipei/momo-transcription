using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Momo.Core.Entities;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Exporters;

namespace Momo.Tests;

public class DocxTrackChangesTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public DocxTrackChangesTests()
    {
        _testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"momo_docx_test_{Guid.NewGuid():N}.db");
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite($"Data Source={_testDbPath}");
        _dbOptions = builder.Options;

        using var context = new AppDbContext(_dbOptions);
        Initializer.Initialize(context);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            try
            {
                File.Delete(_testDbPath);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Test_Export_WithTrackChanges()
    {
        // 1. Seed database with a transcript and pending revision
        using (var context = new AppDbContext(_dbOptions))
        {
            if (!context.Projects.Any(p => p.Id == "proj_docx"))
            {
                context.Projects.Add(new Project { Id = "proj_docx", Name = "Docx Project" });
            }
            context.MediaFiles.Add(new MediaFile
            {
                Id = "media_docx",
                ProjectId = "proj_docx",
                FilePath = "docx_test.wav",
                FileHash = "hash_docx_123",
                FileSizeBytes = 100
            });
            context.Transcripts.Add(new Transcript
            {
                Id = "transcript_docx",
                ProjectId = "proj_docx",
                MediaFileId = "media_docx",
                RawText = "Hello beautiful workspace from Momo"
            });
            context.TranscriptWords.Add(new TranscriptWord
            {
                TranscriptId = "transcript_docx",
                Word = "Hello beautiful workspace from Momo",
                StartTime = 0.0,
                EndTime = 5.0,
                SpeakerId = "Speaker_01"
            });
            // Pending revision has different text: "Hello world from Momo"
            context.Revisions.Add(new Revision
            {
                Id = "rev_docx",
                TranscriptId = "transcript_docx",
                VersionNumber = 1,
                CreatedBy = "Alice",
                CreatedAt = DateTime.UtcNow,
                Description = "Pending changes from Alice",
                SnapshotText = "Hello world from Momo",
                Status = "pending"
            });
            context.SaveChanges();
        }

        // 2. Export using DocxExporter to a MemoryStream
        var exporter = new DocxExporter(_dbOptions);
        using var memoryStream = new MemoryStream();
        await exporter.ExportAsync("media_docx", memoryStream);

        // 3. Verify XML structure
        memoryStream.Position = 0;
        using (var wordDocument = WordprocessingDocument.Open(memoryStream, false))
        {
            var body = wordDocument.MainDocumentPart?.Document.Body;
            Assert.NotNull(body);

            var insertions = body.Descendants<InsertedRun>().ToList();
            var deletions = body.Descendants<DeletedRun>().ToList();
            Assert.NotEmpty(insertions);
            Assert.NotEmpty(deletions);

            // Verify count
            Assert.Equal(2, insertions.Count);
            Assert.Single(deletions);

            // Verify author and date attributes
            var ins = insertions.First();
            Assert.Equal("Alice", ins.Author?.Value);
            Assert.NotNull(ins.Date?.Value);

            var del = deletions.First();
            Assert.Equal("Alice", del.Author?.Value);
            Assert.NotNull(del.Date?.Value);

            // Verify text content of changes
            var insText = string.Join("", insertions.Select(i => i.InnerText).Select(t => t.Trim()));
            Assert.Contains("beautiful", insText);
            Assert.Contains("workspace", insText);

            var delText = string.Join("", deletions.Select(d => d.InnerText).Select(t => t.Trim()));
            Assert.Contains("world", delText);
        }
    }

    [Fact]
    public async Task Test_Export_DefaultLayoutStyling()
    {
        // 1. Seed database with a normal transcript
        using (var context = new AppDbContext(_dbOptions))
        {
            if (!context.Projects.Any(p => p.Id == "proj_docx"))
            {
                context.Projects.Add(new Project { Id = "proj_docx", Name = "Docx Project" });
            }
            context.MediaFiles.Add(new MediaFile
            {
                Id = "media_normal",
                ProjectId = "proj_docx",
                FilePath = "docx_normal.wav",
                FileHash = "hash_docx_normal",
                FileSizeBytes = 100
            });
            context.Transcripts.Add(new Transcript
            {
                Id = "transcript_normal",
                ProjectId = "proj_docx",
                MediaFileId = "media_normal",
                RawText = "Hello world. 這是中文測試。"
            });
            context.TranscriptWords.Add(new TranscriptWord
            {
                TranscriptId = "transcript_normal",
                Word = "Hello world. 這是中文測試。",
                StartTime = 0.0,
                EndTime = 5.0,
                SpeakerId = "Speaker_01"
            });
            context.SaveChanges();
        }

        // 2. Export using DocxExporter
        var exporter = new DocxExporter(_dbOptions);
        using var memoryStream = new MemoryStream();
        await exporter.ExportAsync("media_normal", memoryStream);

        // 3. Verify XML Layout Structure
        memoryStream.Position = 0;
        using (var wordDocument = WordprocessingDocument.Open(memoryStream, false))
        {
            var mainPart = wordDocument.MainDocumentPart;
            Assert.NotNull(mainPart);

            var body = mainPart.Document.Body;
            Assert.NotNull(body);

            // Verify margins: Left/Right = 2cm (1134U), Top/Bottom = 2.54cm (1440)
            var sectionProperties = body.Descendants<SectionProperties>().FirstOrDefault();
            Assert.NotNull(sectionProperties);
            var pageMargin = sectionProperties.Descendants<PageMargin>().FirstOrDefault();
            Assert.NotNull(pageMargin);
            Assert.Equal(1134U, pageMargin.Left.Value);
            Assert.Equal(1134U, pageMargin.Right.Value);
            Assert.Equal(1440, pageMargin.Top.Value);
            Assert.Equal(1440, pageMargin.Bottom.Value);

            // Verify paragraphs: Spacing line = 276 (1.15), before/after = 120 (6pt)
            var paragraphs = body.Descendants<Paragraph>().ToList();
            Assert.NotEmpty(paragraphs);
            foreach (var p in paragraphs)
            {
                var pPr = p.ParagraphProperties;
                if (pPr != null)
                {
                    var spacing = pPr.Descendants<SpacingBetweenLines>().FirstOrDefault();
                    if (spacing != null)
                    {
                        Assert.Equal("276", spacing.Line?.Value);
                        Assert.Equal("120", spacing.After?.Value);
                        Assert.Equal("120", spacing.Before?.Value);
                    }
                }
            }

            // Verify styles part (DocDefaults)
            var stylesPart = mainPart.StyleDefinitionsPart;
            Assert.NotNull(stylesPart);
            var docDefaults = stylesPart.Styles.Descendants<DocDefaults>().FirstOrDefault();
            Assert.NotNull(docDefaults);

            // Verify numbering levels
            var numberingPart = mainPart.NumberingDefinitionsPart;
            Assert.NotNull(numberingPart);
            var abstractNum = numberingPart.Numbering.Descendants<AbstractNum>().FirstOrDefault();
            Assert.NotNull(abstractNum);
            var levels = abstractNum.Descendants<Level>().ToList();
            Assert.Equal(6, levels.Count);

            // Level 0: chineseCountingThousand -> 一、
            var lvl0 = levels[0];
            Assert.Equal(NumberFormatValues.ChineseCountingThousand, lvl0.NumberingFormat.Val.Value);
            Assert.Equal("%1、", lvl0.LevelText.Val.Value);

            // Level 1: chineseCounting -> （一）
            var lvl1 = levels[1];
            Assert.Equal(NumberFormatValues.ChineseCounting, lvl1.NumberingFormat.Val.Value);
            Assert.Equal("（%2）", lvl1.LevelText.Val.Value);

            // Level 2: decimal -> 1、
            var lvl2 = levels[2];
            Assert.Equal(NumberFormatValues.Decimal, lvl2.NumberingFormat.Val.Value);
            Assert.Equal("%3、", lvl2.LevelText.Val.Value);

            // Level 3: decimal -> （1）
            var lvl3 = levels[3];
            Assert.Equal(NumberFormatValues.Decimal, lvl3.NumberingFormat.Val.Value);
            Assert.Equal("（%4）", lvl3.LevelText.Val.Value);

            // Level 4: upperLetter -> A、
            var lvl4 = levels[4];
            Assert.Equal(NumberFormatValues.UpperLetter, lvl4.NumberingFormat.Val.Value);
            Assert.Equal("%5、", lvl4.LevelText.Val.Value);

            // Level 5: lowerLetter -> a、
            var lvl5 = levels[5];
            Assert.Equal(NumberFormatValues.LowerLetter, lvl5.NumberingFormat.Val.Value);
            Assert.Equal("%6、", lvl5.LevelText.Val.Value);
        }
    }
}
