using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Momo.Infrastructure.Streaming;

public class AudioStreamingService
{
    private WaveInEvent? _waveIn;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly MemoryStream _audioBuffer = new();
    private const int TargetBufferSize = 5 * 16000 * 2; // 5 seconds of 16kHz 16-bit mono = 160,000 bytes
    private readonly object _lock = new();

    public event Action<string>? OnTranscriptReceived;
    public event Action<string>? OnError;

    public bool IsStreaming { get; private set; }

    public async Task StartStreamingAsync(string wsUrl)
    {
        if (IsStreaming) return;

        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();

        try
        {
            await _webSocket.ConnectAsync(new Uri(wsUrl), _cts.Token);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"WebSocket connection failed: {ex.Message}");
            Cleanup();
            return;
        }

        IsStreaming = true;

        // Start listening task for transcripts returned from WebSocket
        _ = Task.Run(() => ListenWebSocketAsync(_cts.Token));

        // Configure NAudio WaveIn for 16kHz, 16-bit, Mono PCM if device is available
        if (WaveIn.DeviceCount > 0)
        {
            try
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };

                _waveIn.DataAvailable += OnAudioDataAvailable;
                _waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                // Gracefully handle MmException/NoDriver on headless CI/CD systems
                System.Diagnostics.Debug.WriteLine($"[Warning] NAudio recording failed to start: {ex.Message}");
                _waveIn = null;
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[Warning] No audio recording devices found. Streaming will rely on mock audio data.");
        }
    }

    public void SendMockAudioData(byte[] buffer)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

        lock (_audioBuffer)
        {
            _audioBuffer.Write(buffer, 0, buffer.Length);

            if (_audioBuffer.Length >= TargetBufferSize)
            {
                byte[] chunk = _audioBuffer.ToArray();
                _audioBuffer.SetLength(0); // Reset buffer
                _ = SendAudioChunkAsync(chunk);
            }
        }
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

        lock (_audioBuffer)
        {
            _audioBuffer.Write(e.Buffer, 0, e.BytesRecorded);

            if (_audioBuffer.Length >= TargetBufferSize)
            {
                byte[] chunk = _audioBuffer.ToArray();
                _audioBuffer.SetLength(0); // Reset buffer

                // Send the accumulated 5s slice asynchronously
                _ = SendAudioChunkAsync(chunk);
            }
        }
    }

    private async Task SendAudioChunkAsync(byte[] data)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open || _cts == null) return;

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                true,
                _cts.Token
            );
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Error sending audio chunk: {ex.Message}");
        }
    }

    private async Task ListenWebSocketAsync(CancellationToken token)
    {
        byte[] buffer = new byte[8192];

        while (!token.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", token);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string jsonStr = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (doc.RootElement.TryGetProperty("text", out var textProp))
                    {
                        string text = textProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(text))
                        {
                            OnTranscriptReceived?.Invoke(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    OnError?.Invoke($"Error reading from WebSocket: {ex.Message}");
                }
                break;
            }
        }

        Cleanup();
    }

    public async Task StopStreamingAsync()
    {
        WaveInEvent? localWaveIn = null;
        ClientWebSocket? localWS = null;

        lock (_lock)
        {
            if (!IsStreaming) return;
            IsStreaming = false;
            
            _cts?.Cancel();
            
            localWaveIn = _waveIn;
            _waveIn = null;

            localWS = _webSocket;
            _webSocket = null;
        }

        if (localWaveIn != null)
        {
            try
            {
                localWaveIn.StopRecording();
            }
            catch { }
            try
            {
                localWaveIn.DataAvailable -= OnAudioDataAvailable;
            }
            catch { }
            try
            {
                localWaveIn.Dispose();
            }
            catch { }
        }

        if (localWS != null)
        {
            if (localWS.State == WebSocketState.Open)
            {
                try
                {
                    await localWS.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stop requested", CancellationToken.None);
                }
                catch { }
            }
            try { localWS.Dispose(); } catch { }
        }

        Cleanup();
    }

    private void Cleanup()
    {
        lock (_lock)
        {
            IsStreaming = false;
            if (_waveIn != null)
            {
                try
                {
                    _waveIn.Dispose();
                }
                catch { }
                _waveIn = null;
            }
            if (_webSocket != null)
            {
                try { _webSocket.Dispose(); } catch { }
                _webSocket = null;
            }
            if (_cts != null)
            {
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
            lock (_audioBuffer)
            {
                _audioBuffer.SetLength(0);
            }
        }
    }
}
