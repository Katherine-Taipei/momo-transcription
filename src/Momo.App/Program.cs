using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace Momo.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("export-docx", StringComparison.OrdinalIgnoreCase))
        {
            RunExportDocxCli(args);
            return;
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void RunExportDocxCli(string[] args)
    {
        string? dbPath = null;
        string? transcriptId = null;
        string? outputPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dbPath = args[++i];
            }
            else if (args[i].Equals("--id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                transcriptId = args[++i];
            }
            else if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
        }

        if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(transcriptId) || string.IsNullOrEmpty(outputPath))
        {
            Console.WriteLine("Error: Missing required arguments for export-docx.");
            Console.WriteLine("Usage: Momo.App export-docx --db <dbPath> --id <transcriptId> --out <outputPath>");
            Environment.Exit(1);
        }

        try
        {
            var builder = new DbContextOptionsBuilder<Momo.Infrastructure.Db.AppDbContext>();
            builder.UseSqlite($"Data Source={dbPath}");
            
            using var context = new Momo.Infrastructure.Db.AppDbContext(builder.Options);
            var transcript = context.Transcripts.FirstOrDefault(t => t.Id == transcriptId);
            if (transcript == null)
            {
                Console.WriteLine($"Error: Transcript with ID '{transcriptId}' not found.");
                Environment.Exit(1);
            }

            var exporter = new Momo.Infrastructure.Exporters.DocxExporter(builder.Options);
            exporter.ExportAsync(transcript.MediaFileId, outputPath).GetAwaiter().GetResult();
            Console.WriteLine("DOCX export completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during DOCX export: {ex.Message}");
            Environment.Exit(1);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
