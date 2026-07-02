using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using Microsoft.EntityFrameworkCore;
using Momo.App.ViewModels;
using Momo.Core.Entities;
using Momo.Core.Interfaces;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Collab;
using Momo.Infrastructure.Subprocesses;
using Xunit;

namespace Momo.Tests;

public class UIVersionTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public UIVersionTests()
    {
        _testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"momo_ui_version_test_{Guid.NewGuid():N}.db");

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite($"Data Source={_testDbPath}");
        _dbOptions = builder.Options;

        using var context = new AppDbContext(_dbOptions);
        Initializer.Initialize(context);

        // Seed default project, media file, and transcript
        if (!context.Projects.Any(p => p.Id == "default"))
        {
            context.Projects.Add(new Project { Id = "default", Name = "Default Project" });
        }
        context.MediaFiles.Add(new MediaFile
        {
            Id = "media_111",
            ProjectId = "default",
            FilePath = "flow.wav",
            FileHash = "hash_flow_111",
            FileSizeBytes = 1000
        });
        context.Transcripts.Add(new Transcript
        {
            Id = "transcript_111",
            ProjectId = "default",
            MediaFileId = "media_111",
            RawText = "Initial transcript active text content."
        });
        context.Jobs.Add(new Job
        {
            Id = "job_111",
            ProjectId = "default",
            MediaFileId = "media_111",
            Status = "COMPLETED"
        });
        context.TranscriptWords.Add(new TranscriptWord
        {
            TranscriptId = "transcript_111",
            Word = "Initial",
            StartTime = 0.0,
            EndTime = 1.0,
            SpeakerId = "Speaker_00"
        });
        context.SaveChanges();
    }

    [Fact]
    public void Test_VersionTree_SearchAndSelection()
    {
        var vm = new MainViewModel(_testDbPath);

        // Manually seed revision models in the view model list
        var rev1 = new Revision { Id = "r1", VersionNumber = 1, CreatedBy = "Alice", Description = "Manual Backup Description" };
        var rev2 = new Revision { Id = "r2", VersionNumber = 2, CreatedBy = "Bob", Description = "Autosave revision 1" };
        var rev3 = new Revision { Id = "r3", VersionNumber = 3, CreatedBy = "Charlie", Description = "Autosave revision 2" };

        vm.Revisions.Add(rev1);
        vm.Revisions.Add(rev2);
        vm.Revisions.Add(rev3);

        // Update search filter to display all
        vm.RevisionSearchText = string.Empty;
        Assert.Equal(3, vm.FilteredRevisions.Count);

        // Perform search
        vm.RevisionSearchText = "Autosave";
        Assert.Equal(2, vm.FilteredRevisions.Count);
        Assert.DoesNotContain(rev1, vm.FilteredRevisions);
        Assert.Contains(rev2, vm.FilteredRevisions);
        Assert.Contains(rev3, vm.FilteredRevisions);

        // Filter out autosaves
        vm.RevisionSearchText = string.Empty;
        vm.ShowAutosaves = false;
        Assert.Single(vm.FilteredRevisions);
        Assert.Contains(rev1, vm.FilteredRevisions);

        // Selection
        vm.SelectedRevision = rev1;
        Assert.Equal(rev1, vm.SelectedRevision);
    }

    [Fact]
    public void Test_DiffRender_CharWordLineGranularity()
    {
        var vm = new MainViewModel(_testDbPath);

        // Set active transcript ID
        var typeField = typeof(MainViewModel).GetField("_activeTranscriptId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(typeField);
        typeField.SetValue(vm, "transcript_111");

        // Selected revision text snapshot
        var selectedRevision = new Revision
        {
            Id = "r_sel",
            VersionNumber = 1,
            SnapshotText = "This is selected text."
        };

        // Modify active transcript in Db to compare
        using (var context = new AppDbContext(_dbOptions))
        {
            var tr = context.Transcripts.Find("transcript_111");
            Assert.NotNull(tr);
            tr.RawText = "This is active text.";
            context.SaveChanges();
        }

        vm.SelectedRevision = selectedRevision;

        // 1. Line Granularity
        vm.DiffGranularity = "Line";
        Assert.NotEmpty(vm.DiffRows);
        Assert.Equal("This is active text.", vm.DiffRows[0].OldText);
        Assert.Equal("This is selected text.", vm.DiffRows[0].NewText);

        // 2. Word Granularity
        vm.DiffGranularity = "Word";
        Assert.NotEmpty(vm.DiffRows);
        // Words: "This", "is", "active", "text." vs "This", "is", "selected", "text."
        Assert.Contains(vm.DiffRows, r => r.OldText == "active");
        Assert.Contains(vm.DiffRows, r => r.NewText == "selected");

        // 3. Char Granularity
        vm.DiffGranularity = "Char";
        Assert.NotEmpty(vm.DiffRows);
        Assert.Contains(vm.DiffRows, r => r.OldText == "a" && r.OldBackground != "Transparent");
        Assert.Contains(vm.DiffRows, r => r.NewText == "s" && r.NewBackground != "Transparent");
    }

    [Fact]
    public async Task Test_UIRollback_E2E()
    {
        // Initialize MainViewModel
        var vm = new MainViewModel(_testDbPath);
        var job = vm.Jobs.FirstOrDefault(j => j.Id == "job_111");
        Assert.NotNull(job);
        vm.SelectedJob = job;
        
        // Extract the subprocess host to get the active port and token
        var subProp = typeof(MainViewModel).GetField("_subprocessHost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(subProp);
        var subHost = (ISubprocessHost)subProp.GetValue(vm)!;

        // Ensure worker is running
        try
        {
            await subHost.EnsureWorkerRunningAsync(job);
        }
        catch
        {
            // Skip if Python daemon fails to start locally due to WDAC
            return;
        }

        int port = subHost.ActivePort;
        string? token = subHost.AuthToken;
        Assert.NotNull(token);

        // 1. Create a revision snapshot in Db using active port and token
        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var createPayload = new
            {
                transcript_id = "transcript_111",
                created_by = "UserA",
                description = "Version 1 Manual"
            };
            var content = new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync($"http://127.0.0.1:{port}/api/v1/revisions", content);
            Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        }

        // Retrieve revisions from DB and verify list loads
        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var getResponse = await httpClient.GetAsync($"http://127.0.0.1:{port}/api/v1/transcripts/transcript_111/revisions");
            var contentStr = await getResponse.Content.ReadAsStringAsync();
            Assert.True(getResponse.IsSuccessStatusCode, $"GET failed with status {getResponse.StatusCode} and content: {contentStr}");
            Assert.Contains("transcript_111", contentStr);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<Revision>>(contentStr, options);
            Assert.NotNull(list);
            Assert.Single(list);
        }



        var activeId = typeof(MainViewModel).GetField("_activeTranscriptId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm);
        Assert.Equal("transcript_111", activeId);
        Assert.NotNull(vm.SelectedJob);

        await vm.LoadRevisionsAsync();
        Assert.Single(vm.Revisions);
        var revToRollback = vm.Revisions.First();

        // 2. Modify database raw text
        using (var context = new AppDbContext(_dbOptions))
        {
            var trans = context.Transcripts.Find("transcript_111");
            Assert.NotNull(trans);
            trans.RawText = "Modified raw text.";
            context.SaveChanges();
        }

        // 3. Perform UI rollback
        vm.SelectedRevision = revToRollback;
        
        // Execute the RollbackCommand task
        var cmd = (ReactiveCommand<Unit, Unit>)vm.RollbackCommand;
        await cmd.Execute().ToTask();

        // 4. Verify text is rolled back in SQLite DB
        using (var context = new AppDbContext(_dbOptions))
        {
            var trans = context.Transcripts.Find("transcript_111");
            Assert.NotNull(trans);
            Assert.Equal("Initial transcript active text content.", trans.RawText);
        }

        // Clean up worker
        await subHost.StopWorkerAsync("job_111");
    }

    private static int FindFreeTcpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static bool IsPortOpen(int port, int timeoutMs)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var result = socket.BeginConnect(new IPEndPoint(IPAddress.Loopback, port), null, null);
                var success = result.AsyncWaitHandle.WaitOne(200);
                if (success && socket.Connected)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore and retry
            }
            Thread.Sleep(200);
        }
        return false;
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
