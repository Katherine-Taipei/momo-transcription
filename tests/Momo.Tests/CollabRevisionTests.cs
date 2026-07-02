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
using Xunit;

namespace Momo.Tests;

public class CollabRevisionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _pythonExe;
    private readonly string _workerScript;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private Process? _pythonProcess;
    private int _pythonPort;
    private readonly string _authToken = "test_token_revision";

    public CollabRevisionTests()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "collab_revision_test.db");
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
            RawText = "This is the active transcript raw text snapshot."
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
            var logPath = Path.Combine("d:\\Antigravity\\Project 3_Enterprise Momo", "python_test_run.log");
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
    public async Task Test_Revisions_Create_And_Get_Flow()
    {
        StartPythonWorker();

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

        // 1. Create a revision (POST /api/v1/revisions)
        var createPayload = new
        {
            transcript_id = "trans_123",
            created_by = "UserA",
            description = "Manual Backup 1"
        };
        var createJson = JsonSerializer.Serialize(createPayload);
        var content = new StringContent(createJson, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"http://127.0.0.1:{_pythonPort}/api/v1/revisions", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Verify Location header exists
        var location = response.Headers.Location;
        Assert.NotNull(location);

        // Read result body
        var bodyStr = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(bodyStr);
        var root = doc.RootElement;
        
        Assert.Equal("trans_123", root.GetProperty("transcript_id").GetString());
        Assert.Equal("UserA", root.GetProperty("created_by").GetString());
        Assert.Equal("Manual Backup 1", root.GetProperty("description").GetString());
        Assert.Equal("This is the active transcript raw text snapshot.", root.GetProperty("snapshot_text").GetString());
        Assert.Equal(1, root.GetProperty("version_number").GetInt32());
        
        string revisionId = root.GetProperty("id").GetString()!;
        Assert.NotEmpty(revisionId);

        // 2. Fetch revision detail via GET location header
        var getDetailResponse = await client.GetAsync($"http://127.0.0.1:{_pythonPort}{location}");
        Assert.Equal(HttpStatusCode.OK, getDetailResponse.StatusCode);
        
        var getDetailStr = await getDetailResponse.Content.ReadAsStringAsync();
        using var getDetailDoc = JsonDocument.Parse(getDetailStr);
        Assert.Equal(revisionId, getDetailDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("This is the active transcript raw text snapshot.", getDetailDoc.RootElement.GetProperty("snapshot_text").GetString());

        // 3. Create a second revision (version number should increment to 2)
        var createPayload2 = new
        {
            transcript_id = "trans_123",
            created_by = "UserB",
            description = "Manual Backup 2"
        };
        var content2 = new StringContent(JsonSerializer.Serialize(createPayload2), Encoding.UTF8, "application/json");
        var response2 = await client.PostAsync($"http://127.0.0.1:{_pythonPort}/api/v1/revisions", content2);
        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);
        
        var bodyStr2 = await response2.Content.ReadAsStringAsync();
        using var doc2 = JsonDocument.Parse(bodyStr2);
        Assert.Equal(2, doc2.RootElement.GetProperty("version_number").GetInt32());

        // 4. Get revisions list for transcript
        var getListResponse = await client.GetAsync($"http://127.0.0.1:{_pythonPort}/api/v1/transcripts/trans_123/revisions");
        Assert.Equal(HttpStatusCode.OK, getListResponse.StatusCode);
        
        var listStr = await getListResponse.Content.ReadAsStringAsync();
        using var listDoc = JsonDocument.Parse(listStr);
        var array = listDoc.RootElement;
        Assert.Equal(2, array.GetArrayLength());
        // Descending order of version number: 2 first, then 1
        Assert.Equal(2, array[0].GetProperty("version_number").GetInt32());
        Assert.Equal(1, array[1].GetProperty("version_number").GetInt32());

        // 5. Test 404 scenarios
        var notFoundDetail = await client.GetAsync($"http://127.0.0.1:{_pythonPort}/api/v1/revisions/non_existent_id");
        Assert.Equal(HttpStatusCode.NotFound, notFoundDetail.StatusCode);

        var notFoundList = await client.GetAsync($"http://127.0.0.1:{_pythonPort}/api/v1/transcripts/non_existent_transcript/revisions");
        Assert.Equal(HttpStatusCode.NotFound, notFoundList.StatusCode);
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
