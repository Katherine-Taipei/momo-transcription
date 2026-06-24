using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Momo.Infrastructure.Db;

namespace Momo.Infrastructure.Exporters;

public class SrtExporter
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SrtExporter(DbContextOptions<AppDbContext> dbOptions)
    {
        _dbOptions = dbOptions;
    }

    public async Task ExportAsync(string mediaFileId, string outputPath)
    {
        using var context = new AppDbContext(_dbOptions);
        var transcript = await context.Transcripts
            .FirstOrDefaultAsync(t => t.MediaFileId == mediaFileId);

        if (transcript == null) return;

        var words = await context.TranscriptWords
            .Where(w => w.TranscriptId == transcript.Id)
            .OrderBy(w => w.StartTime)
            .ToListAsync();

        var paragraphs = ParagraphBuilder.BuildFromWords(words);

        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
        int index = 1;
        foreach (var p in paragraphs)
        {
            writer.WriteLine(index);
            writer.WriteLine($"{FormatSrtTime(p.StartTime)} --> {FormatSrtTime(p.EndTime)}");
            writer.WriteLine($"{p.Speaker}：{p.Text}");
            writer.WriteLine();
            index++;
        }
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
