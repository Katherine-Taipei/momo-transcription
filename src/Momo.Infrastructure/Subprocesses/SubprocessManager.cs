using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Momo.Core.Entities;
using Momo.Core.Interfaces;

namespace Momo.Infrastructure.Subprocesses;

public class SubprocessManager : ISubprocessHost
{
    private readonly string _pythonExePath;
    private readonly string _workerScriptPath;
    private readonly string _databasePath;
    private readonly HttpClient _httpClient;
    private Process? _activeProcess;
    private string? _activeJobId;
    private int _activePort;
    private string? _authToken;

    public Process? ActiveProcess => _activeProcess;
    public int ActivePort => _activePort;
    public string? AuthToken => _authToken;
    public string PythonExePath => _pythonExePath;
    public string WorkerScriptPath => _workerScriptPath;
    public string DatabasePath => _databasePath;

    public SubprocessManager(string pythonExePath, string workerScriptPath, string databasePath)
    {
        _pythonExePath = pythonExePath;
        _workerScriptPath = workerScriptPath;
        _databasePath = databasePath;
        _httpClient = new HttpClient();
    }

    public async Task StartWorkerAsync(Job job)
    {
        if (_activeProcess != null && !_activeProcess.HasExited)
        {
            throw new InvalidOperationException("A subprocess worker is already running.");
        }

        _activeJobId = job.Id;
        _activePort = FindFreeTcpPort();
        _authToken = GenerateSecureToken();

        var tempOutputDir = Path.Combine(Path.GetTempPath(), "Momo", "Chunks", job.Id);
        Directory.CreateDirectory(tempOutputDir);

        var arguments = $"\"{_workerScriptPath}\" --port {_activePort} --db \"{_databasePath}\" --token \"{_authToken}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerScriptPath) ?? string.Empty
        };

        _activeProcess = new Process { StartInfo = startInfo };
        _activeProcess.EnableRaisingEvents = true;

        _activeProcess.OutputDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[Python STDOUT] {e.Data}");
        };
        _activeProcess.ErrorDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[Python STDERR] {e.Data}");
        };

        _activeProcess.Start();
        _activeProcess.BeginOutputReadLine();
        _activeProcess.BeginErrorReadLine();

        // Wait for python uvicorn to bind to the port (up to 35 seconds)
        IsPortOpen(_activePort, 35000);

        await TriggerJobStartRequestAsync(job.Id, job.MediaFile?.FilePath ?? string.Empty, tempOutputDir, job.Diarization, job.Alignment);
    }

    public async Task StopWorkerAsync(string jobId)
    {
        if (_activeJobId != jobId || _activeProcess == null || _activeProcess.HasExited)
        {
            return;
        }

        try
        {
            var requestUrl = $"http://127.0.0.1:{_activePort}/api/v1/jobs/pause";
            var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);
            
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _activeProcess.WaitForExitAsync(cts.Token);
            }
        }
        catch
        {
            if (!_activeProcess.HasExited)
            {
                _activeProcess.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _activeProcess = null;
            _activeJobId = null;
            _authToken = null;
        }
    }

    public bool IsWorkerRunning(string jobId)
    {
        return _activeJobId == jobId && _activeProcess != null && !_activeProcess.HasExited;
    }

    private async Task TriggerJobStartRequestAsync(string jobId, string mediaPath, string outputDir, bool diarization, bool alignment)
    {
        var requestUrl = $"http://127.0.0.1:{_activePort}/api/v1/jobs/start";
        var diarizationStr = diarization ? "true" : "false";
        var alignmentStr = alignment ? "true" : "false";
        var jsonPayload = $$"""
        {
          "job_id": "{{jobId}}",
          "media_file_path": "{{mediaPath.Replace("\\", "\\\\")}}",
          "chunk_output_dir": "{{outputDir.Replace("\\", "\\\\")}}",
          "profile": {
            "compute_device": "cpu",
            "whisper_model": "base",
            "quantization": "int8",
            "cpu_threads": 4,
            "diarization": {{diarizationStr}},
            "alignment": {{alignmentStr}},
            "chunk_length": 300
          }
        }
        """;

        HttpResponseMessage? response = null;
        for (int i = 0; i < 35; i++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                if (i == 34) throw;
            }
            await Task.Delay(1000);
        }

        response?.EnsureSuccessStatusCode();
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
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    private static string GenerateSecureToken()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    public async Task EnsureWorkerRunningAsync(Job job)
    {
        if (_activeProcess != null && !_activeProcess.HasExited)
        {
            return;
        }

        _activeJobId = job.Id;
        _activePort = FindFreeTcpPort();
        _authToken = GenerateSecureToken();

        var tempOutputDir = Path.Combine(Path.GetTempPath(), "Momo", "Chunks", job.Id);
        Directory.CreateDirectory(tempOutputDir);

        var arguments = $"\"{_workerScriptPath}\" --port {_activePort} --db \"{_databasePath}\" --token \"{_authToken}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerScriptPath) ?? string.Empty
        };

        _activeProcess = new Process { StartInfo = startInfo };
        _activeProcess.EnableRaisingEvents = true;

        _activeProcess.OutputDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine($"[Python STDOUT] {e.Data}");
        };
        _activeProcess.ErrorDataReceived += (s, e) => {
            if (!string.IsNullOrEmpty(e.Data))
                Console.Error.WriteLine($"[Python STDERR] {e.Data}");
        };

        _activeProcess.Start();
        _activeProcess.BeginOutputReadLine();
        _activeProcess.BeginErrorReadLine();

        IsPortOpen(_activePort, 35000);
    }
}
