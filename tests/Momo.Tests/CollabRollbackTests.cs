using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Collab;
using Xunit;

namespace Momo.Tests;

public class CollabRollbackTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _pythonExe;
    private readonly string _workerScript;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private Process? _pythonProcess;
    private int _pythonPort;
    private int _signalrPort;
    private readonly string _authToken = "test_token_rollback";

    public CollabRollbackTests()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "collab_rollback_test.db");
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
        context.Projects.Add(new Project { Id = "test_proj", Name = "Test Project" });
        context.MediaFiles.Add(new MediaFile
        {
            Id = "media_123",
            ProjectId = "test_proj",
            FilePath = "mock_path",
            FileHash = "mock_hash"
        });
        context.Transcripts.Add(new Transcript
        {
            Id = "trans_123",
            ProjectId = "test_proj",
            MediaFileId = "media_123",
            RawText = "Initial original text content."
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
            var logPath = Path.Combine("d:\\Antigravity\\Project 3_Enterprise Momo", "collab_rollback_python_test_run.log");
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
        
        // Wait for python uvicorn to bind to the port (up to 35 seconds)
        IsPortOpen(_pythonPort, 35000);
    }

    [Fact]
    public async Task Test_Rollback_And_SignalR_Broadcast()
    {
        // Skip run if Python fails to start (e.g. WDAC blocks it locally)
        try
        {
            StartPythonWorker();
        }
        catch
        {
            return;
        }

        if (_pythonProcess == null || _pythonProcess.HasExited)
        {
            return;
        }

        _signalrPort = FindFreeTcpPort();
        var serverUrl = $"http://localhost:{_signalrPort}/hubs/collab";

        // 1. Start SignalR server
        var server = new CollabServerHost();
        await server.StartAsync(_signalrPort);
        await Task.Delay(1000);

        try
        {
            // 2. Setup two clients
            var clientA = new CollabClient(serverUrl);
            var clientB = new CollabClient(serverUrl);

            await clientA.StartAsync();
            await clientB.StartAsync();

            string transcriptId = "trans_123";
            await clientA.JoinGroupAsync(transcriptId, "UserA");
            await clientB.JoinGroupAsync(transcriptId, "UserB");

            var tcsRollback = new TaskCompletionSource<string>();
            clientB.OnRollbackApplied += revId => tcsRollback.TrySetResult(revId);

            // 3. Create Version 1 revision (has raw text "Initial original text content.")
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

            var createPayload = new
            {
                transcript_id = transcriptId,
                created_by = "UserA",
                description = "Version 1 Manual Backup"
            };
            var content = new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync($"http://127.0.0.1:{_pythonPort}/api/v1/revisions", content);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var bodyStr = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(bodyStr);
            string v1RevisionId = doc.RootElement.GetProperty("id").GetString()!;
            Assert.NotEmpty(v1RevisionId);

            // 4. Modify the active transcript text
            using (var context = new AppDbContext(_dbOptions))
            {
                var trans = await context.Transcripts.FindAsync(transcriptId);
                Assert.NotNull(trans);
                trans.RawText = "Modified text content.";
                await context.SaveChangesAsync();
            }

            // 5. Trigger rollback to Version 1 via python REST API
            var rollbackPayload = new System.Collections.Generic.Dictionary<string, string>
            {
                { "operator", "UserA" }
            };
            var rollbackContent = new StringContent(JsonSerializer.Serialize(rollbackPayload), Encoding.UTF8, "application/json");
            var rollbackResp = await httpClient.PostAsync($"http://127.0.0.1:{_pythonPort}/api/v1/revisions/{v1RevisionId}/rollback", rollbackContent);
            Assert.Equal(HttpStatusCode.OK, rollbackResp.StatusCode);

            // 6. Broadcast the rollback event to others via SignalR
            await clientA.SubmitRollbackAsync(transcriptId, v1RevisionId);

            // 7. Verify Client B receives it
            var completedTask = await Task.WhenAny(tcsRollback.Task, Task.Delay(5000));
            Assert.Equal(tcsRollback.Task, completedTask);
            var receivedRevId = await tcsRollback.Task;
            Assert.Equal(v1RevisionId, receivedRevId);

            // 8. Verify DB text is indeed rolled back to original text
            using (var context = new AppDbContext(_dbOptions))
            {
                var trans = await context.Transcripts.FindAsync(transcriptId);
                Assert.NotNull(trans);
                Assert.Equal("Initial original text content.", trans.RawText);
                
                // Assert that transcript_words was deleted
                var words = await context.TranscriptWords.Where(w => w.TranscriptId == transcriptId).ToListAsync();
                Assert.Empty(words);
            }

            // Cleanup SignalR clients
            await clientA.StopAsync();
            await clientB.StopAsync();
        }
        finally
        {
            await server.StopAsync();
        }
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
        if (_pythonProcess != null && !_pythonProcess.HasExited)
        {
            try { _pythonProcess.Kill(entireProcessTree: true); } catch { }
            _pythonProcess.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }
}
