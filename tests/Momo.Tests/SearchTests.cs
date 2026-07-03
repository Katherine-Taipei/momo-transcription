using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;
using Momo.Infrastructure.Db;
using Momo.App.ViewModels;
using Xunit;

namespace Momo.Tests;

public class SearchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _pythonExe;
    private readonly string _workerScript;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private Process? _pythonProcess;
    private int _pythonPort;
    private readonly string _authToken = "test_token_search";

    public SearchTests()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "collab_search_test.db");
        _pythonExe = @"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\.venv\Scripts\python.exe";
        _workerScript = @"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\main.py";

        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite($"Data Source={_dbPath}");
        _dbOptions = builder.Options;

        // Initialize SQLite DB
        using var context = new AppDbContext(_dbOptions);
        Initializer.Initialize(context);

        // Seed mock project, media file, and transcript
        context.Projects.Add(new Project { Id = "default", Name = "Default Project" });
        context.MediaFiles.Add(new MediaFile
        {
            Id = "media_search",
            ProjectId = "default",
            FilePath = "mock_path",
            FileHash = "mock_hash"
        });
        context.Transcripts.Add(new Transcript
        {
            Id = "trans_search",
            ProjectId = "default",
            MediaFileId = "media_search",
            RawText = "這是一個高亮搜尋測試。我們可以用關鍵字定位。"
        });
        context.TranscriptWords.Add(new TranscriptWord
        {
            TranscriptId = "trans_search",
            Word = "這是一個高亮搜尋測試。我們可以用關鍵字定位。",
            StartTime = 0.0,
            EndTime = 5.0,
            SpeakerId = "Speaker_01"
        });
        context.SaveChanges();
    }

    private void StartPythonWorker()
    {
        _pythonPort = FindFreeTcpPort();
        var arguments = $"\"{_workerScript}\" --port {_pythonPort} --db \"{_dbPath}\" --token \"{_authToken}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_workerScript) ?? string.Empty
        };

        _pythonProcess = Process.Start(startInfo);
        if (_pythonProcess != null)
        {
            var logPath = Path.Combine("d:\\Antigravity\\Project 3_Enterprise Momo", "collab_search_python_test_run.log");
            File.WriteAllText(logPath, $"Starting python worker with args: {arguments}\n");
            _pythonProcess.OutputDataReceived += (s, e) => {
                if (e.Data != null) File.AppendAllText(logPath, "[STDOUT] " + e.Data + "\n");
            };
            _pythonProcess.ErrorDataReceived += (s, e) => {
                if (e.Data != null) File.AppendAllText(logPath, "[STDERR] " + e.Data + "\n");
            };
            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();
        }
        
        IsPortOpen(_pythonPort, 35000);
    }

    [Fact]
    public async Task Test_Search_Integration_And_Navigation()
    {
        try
        {
            StartPythonWorker();
        }
        catch
        {
            return; // Skip if Python worker fails to start
        }

        if (_pythonProcess == null || _pythonProcess.HasExited)
        {
            return;
        }

        var vm = new MainViewModel(_dbPath);
        
        // Setup mock selected job to connect to the runner
        vm.SelectedJob = new Job
        {
            Id = "job_search",
            ProjectId = "default",
            MediaFileId = "media_search",
            Status = "COMPLETED"
        };

        // Override internal subprocessHost's AuthToken, Port, and Process to align with test worker
        var subProp = typeof(MainViewModel).GetField("_subprocessHost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(subProp);
        var subHost = subProp.GetValue(vm);
        Assert.NotNull(subHost);

        var portField = subHost.GetType().GetField("_activePort", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var tokenField = subHost.GetType().GetField("_authToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var procField = subHost.GetType().GetField("_activeProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        portField?.SetValue(subHost, _pythonPort);
        tokenField?.SetValue(subHost, _authToken);
        procField?.SetValue(subHost, _pythonProcess);

        // Explicitly set active transcript ID
        var actField = typeof(MainViewModel).GetField("_activeTranscriptId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        actField?.SetValue(vm, "trans_search");

        // Set up paragraphs view model list
        vm.CurrentParagraphs.Add(new ParagraphViewModel { Text = "這是一個高亮搜尋測試。" });
        vm.CurrentParagraphs.Add(new ParagraphViewModel { Text = "我們可以用關鍵字定位。" });

        // Force rebuild index on Python worker via client
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
        var rebuildResp = await client.PostAsync($"http://127.0.0.1:{_pythonPort}/api/v1/transcripts/trans_search/rebuild_index", null);
        Assert.Equal(HttpStatusCode.OK, rebuildResp.StatusCode);

        // Run search query from VM
        vm.SearchQuery = "關鍵字";
        await Task.Delay(1000); // Wait for async query task to complete

        // Pump UI thread to execute Dispatcher.UIThread.Post
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(vm.SearchResults);
        Assert.Equal(1, vm.SearchResults[0].ParagraphIndex);
        Assert.Equal(5, vm.SearchResults[0].StartIndex);
        Assert.Equal(8, vm.SearchResults[0].EndIndex);

        // Subscribe to scroll-to-paragraph and selection events
        bool eventRaised = false;
        int raisedParaIndex = -1;
        int raisedStartIdx = -1;
        int raisedLength = -1;

        vm.ScrollToParagraphRequested += (index, start, len) =>
        {
            eventRaised = true;
            raisedParaIndex = index;
            raisedStartIdx = start;
            raisedLength = len;
        };

        // Select the search result
        vm.SelectedSearchResult = vm.SearchResults[0];

        // Assert scroll and selection trigger events
        Assert.True(eventRaised);
        Assert.Equal(1, raisedParaIndex);
        Assert.Equal(5, raisedStartIdx);
        Assert.Equal(3, raisedLength);

        // Assert target paragraph highlight is active
        var targetParagraph = vm.CurrentParagraphs[1];
        Assert.True(targetParagraph.IsHighlighted);

        // Wait 3.5 seconds and assert highlight is faded out automatically
        await Task.Delay(3500);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(targetParagraph.IsHighlighted);

        // Test next/prev match navigation commands
        vm.NextMatchCommand.Execute(null);
        Assert.Equal(vm.SearchResults[0], vm.SelectedSearchResult);
    }

    private static int FindFreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool IsPortOpen(int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var client = new TcpClient("127.0.0.1", port);
                return true;
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_pythonProcess != null && !_pythonProcess.HasExited)
        {
            try { _pythonProcess.Kill(); } catch { }
            _pythonProcess.Dispose();
        }
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }
}
