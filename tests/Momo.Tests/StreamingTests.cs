using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Momo.Core.Entities;
using Momo.Infrastructure.Db;
using Momo.Infrastructure.Queue;
using Momo.Infrastructure.Streaming;
using Xunit;

namespace Momo.Tests;

public class StreamingTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly string _pythonExe;
    private readonly string _workerScript;
    private Process? _process;

    public StreamingTests()
    {
        _testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "momo_stream_test.db");
        _pythonExe = @"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\.venv\Scripts\python.exe";
        _workerScript = @"d:\Antigravity\Project 3_Enterprise Momo\src\momo_worker\main.py";

        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }

        // Initialize a clean SQLite DB for testing
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlite($"Data Source={_testDbPath}");
        using var context = new AppDbContext(builder.Options);
        Initializer.Initialize(context);
    }

    [Fact]
    public async Task Test_WebSocket_StreamTranscribe()
    {
        int port = FindFreeTcpPort();
        string token = "test_stream_token";

        // 1. Spawn Python worker process
        var arguments = $"\"{_workerScript}\" --port {port} --db \"{_testDbPath}\" --token \"{token}\"";
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerScript) ?? string.Empty
        };

        _process = Process.Start(psi);
        Assert.NotNull(_process);

        // 2. Setup ClientWebSocket and connect to Python streaming endpoint with retry loop
        var wsUrl = $"ws://127.0.0.1:{port}/ws/live-stream?token={token}";
        ClientWebSocket ws = null!;
        bool connected = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        for (int i = 0; i < 20; i++)
        {
            ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
                connected = true;
                break;
            }
            catch
            {
                ws.Dispose();
                await Task.Delay(1000);
            }
        }
        
        Assert.True(connected, "Failed to connect to the WebSocket server after retries.");
        Assert.Equal(WebSocketState.Open, ws.State);

        // 3. Send 5 seconds of mock PCM audio silence (160,000 bytes)
        byte[] mockPcmData = new byte[160000]; // 16kHz * 2 bytes/sample * 5 seconds
        await ws.SendAsync(
            new ArraySegment<byte>(mockPcmData),
            WebSocketMessageType.Binary,
            true,
            cts.Token
        );

        // 4. Receive the transcription JSON reply
        byte[] receiveBuffer = new byte[4096];
        var receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
        
        Assert.Equal(WebSocketMessageType.Text, receiveResult.MessageType);
        string jsonStr = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count);
        
        using var doc = JsonDocument.Parse(jsonStr);
        Assert.True(doc.RootElement.TryGetProperty("text", out var textProp));
        Assert.True(doc.RootElement.TryGetProperty("status", out var statusProp));
        Assert.Equal("partial", statusProp.GetString());

        // 5. Cleanup connection
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test done", cts.Token);
    }

    private static int FindFreeTcpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }
}
