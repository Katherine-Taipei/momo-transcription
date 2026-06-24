using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Templates;

namespace Momo.Infrastructure.Exporters;

public class TxtExporter
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public TxtExporter(DbContextOptions<AppDbContext> dbOptions)
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

            await File.WriteAllTextAsync(outputPath, rendered, new UTF8Encoding(true));
        }
        else
        {
            using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
            foreach (var p in paragraphs)
            {
                writer.WriteLine($"[{FormatTime(p.StartTime)}] {p.Speaker}：{p.Text}");
            }
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
