using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Microsoft.EntityFrameworkCore;
using Momo.App.Controls;
using Momo.App.ViewModels;
using Momo.Core.Entities;
using Momo.Infrastructure.Db;
using Xunit;

namespace Momo.Tests;

public class TestSpeakerTimeline : SpeakerTimeline
{
    public void SimulatePointerPressed(PointerPressedEventArgs e)
    {
        OnPointerPressed(e);
    }
}

public class UIFeatureTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public UIFeatureTests()
    {
        _testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "momo_ui_test.db");

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite($"Data Source={_testDbPath}");
        _dbOptions = builder.Options;

        using var context = new AppDbContext(_dbOptions);
        Initializer.Initialize(context);

        // Seed a default project
        if (!context.Projects.Any(p => p.Id == "default"))
        {
            context.Projects.Add(new Project { Id = "default", Name = "Default Project" });
            context.SaveChanges();
        }
    }

    [Fact]
    public void Test_Timeline_ClickJump()
    {
        // 1. Initialize control
        var timeline = new TestSpeakerTimeline
        {
            Duration = 100.0,
            Segments = new List<TimelineSegment>
            {
                new TimelineSegment { SpeakerId = "Speaker_00", DisplayName = "Alice", StartTime = 10.0, EndTime = 40.0 },
                new TimelineSegment { SpeakerId = "Speaker_01", DisplayName = "Bob", StartTime = 50.0, EndTime = 80.0 }
            }
        };

        // Layout the control in tests (width 200, height 50)
        timeline.Measure(new Size(200, 50));
        timeline.Arrange(new Rect(0, 0, 200, 50));

        // 2. Simulate clicking empty space (e.g. position X = 90, which is time 45.0)
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var pointerPressedArgs = new PointerPressedEventArgs(
            timeline,
            pointer,
            timeline,
            new Point(90, 25),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None
        );

        timeline.SimulatePointerPressed(pointerPressedArgs);

        // Clamped clicked time should be (90 / 200) * 100 = 45.0
        Assert.Equal(45.0, timeline.CurrentTime);

        // 3. Simulate clicking inside a segment block (e.g. position X = 30, which is inside Alice segment: 10.0 to 40.0)
        var pointerPressedAlice = new PointerPressedEventArgs(
            timeline,
            pointer,
            timeline,
            new Point(30, 25),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None
        );

        timeline.SimulatePointerPressed(pointerPressedAlice);

        // Clicking a segment block should jump to its StartTime (10.0)
        Assert.Equal(10.0, timeline.CurrentTime);
    }

    [Fact]
    public async Task Test_TagCloud_Filter()
    {
        // 1. Seed a completed job with transcript words containing glossary terms
        using (var context = new AppDbContext(_dbOptions))
        {
            var mediaId = "media_ui_123";
            var mediaFile = new MediaFile
            {
                Id = mediaId,
                ProjectId = "default",
                FilePath = "dummy.wav",
                FileHash = "hash_ui_123",
                FileSizeBytes = 1000
            };
            context.MediaFiles.Add(mediaFile);

            var job = new Job
            {
                Id = "job_ui_123",
                ProjectId = "default",
                MediaFileId = mediaId,
                Status = "COMPLETED",
                SelectedGlossaries = "[\"medical.json\"]"
            };
            context.Jobs.Add(job);

            var transcript = new Transcript
            {
                Id = "transcript_ui_123",
                ProjectId = "default",
                MediaFileId = mediaId,
                RawText = "momo transcription using terzipatide and momo text."
            };
            context.Transcripts.Add(transcript);

            // Add transcript words matching terms in medical.json ("momo", "terzipatide")
            context.TranscriptWords.AddRange(
                new TranscriptWord { TranscriptId = transcript.Id, Word = "momo", StartTime = 1.0, EndTime = 2.0, SpeakerId = "Speaker_00" },
                new TranscriptWord { TranscriptId = transcript.Id, Word = "transcription", StartTime = 2.1, EndTime = 3.0, SpeakerId = "Speaker_00" },
                new TranscriptWord { TranscriptId = transcript.Id, Word = "using", StartTime = 3.1, EndTime = 4.0, SpeakerId = "Speaker_00" },
                new TranscriptWord { TranscriptId = transcript.Id, Word = "terzipatide", StartTime = 15.0, EndTime = 16.5, SpeakerId = "Speaker_01" }
            );

            await context.SaveChangesAsync();
        }

        // 2. Initialize MainViewModel with our test database
        var vm = new MainViewModel(_testDbPath);
        
        // Select the job
        var jobFromDb = vm.Jobs.FirstOrDefault(j => j.Id == "job_ui_123");
        Assert.NotNull(jobFromDb);

        vm.SelectedJob = jobFromDb;

        // 3. Verify Tag Cloud is populated based on selected glossaries
        Assert.NotEmpty(vm.TagCloudItems);
        
        var momoTag = vm.TagCloudItems.FirstOrDefault(t => t.Term.Equals("Momo", StringComparison.OrdinalIgnoreCase));
        var tirzepatideTag = vm.TagCloudItems.FirstOrDefault(t => t.Term.Equals("Tirzepatide", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(momoTag);
        Assert.NotNull(tirzepatideTag);

        Assert.True(momoTag.Count > 0);
        Assert.True(tirzepatideTag.Count > 0);

        // 4. Verify SelectTagCommand highlights paragraphs and jumps playhead
        vm.SelectTagCommand.Execute("Tirzepatide");

        // Playhead should jump to the start of the first matched word: "terzipatide" -> StartTime = 15.0
        Assert.Equal(15.0, vm.CurrentTime);

        // Matched paragraph should be highlighted
        var highlightedParagraphs = vm.CurrentParagraphs.Where(p => p.IsHighlighted).ToList();
        Assert.NotEmpty(highlightedParagraphs);
        Assert.Contains("terzipatide", highlightedParagraphs.First().Text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }
}
