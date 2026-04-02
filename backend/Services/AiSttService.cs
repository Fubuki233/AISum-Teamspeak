using System.Collections.Concurrent;

namespace TsAi.Services;

/// <summary>
/// STT via DashScope: buffer raw PCM per speaker and periodically
/// transcribe buffered audio, then feed the text into the summary pipeline.
/// </summary>
public class AiSttService : IDisposable
{
    private readonly ConcurrentDictionary<(string Server, ushort ClientId), BufferState> _buffers = new();
    private readonly ConcurrentDictionary<(string Server, ushort ClientId), byte> _announcedStreams = new();
    private readonly ByteDanceAiService _ai;
    private readonly ILogger<AiSttService> _log;
    private readonly Timer _flushTimer;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan IdleFlush = TimeSpan.FromMilliseconds(2200);

    public event Action<string, ushort, string>? OnSpeechRecognized;

    public AiSttService(ByteDanceAiService ai, ILogger<AiSttService> log)
    {
        _ai = ai;
        _log = log;
        _flushTimer = new Timer(_ => _ = FlushReadyAsync(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(600));
    }

    public void FeedAudio(string serverAddress, ushort clientId, uint frequency, byte[] audioData)
    {
        var key = (serverAddress, clientId);
        var created = false;
        var state = _buffers.GetOrAdd(key, _ =>
        {
            created = true;
            return new BufferState { Frequency = frequency };
        });

        if (created && _announcedStreams.TryAdd(key, 0))
        {
            _log.LogInformation("AI-STT stream active for [{Server}] client {Id} @ {Freq}Hz",
                serverAddress, clientId, frequency);
        }

        lock (state.Gate)
        {
            state.Frequency = frequency;
            state.LastActivity = DateTime.UtcNow;
            state.Pcm.Write(audioData, 0, audioData.Length);
        }
    }

    private async Task FlushReadyAsync()
    {
        foreach (var kv in _buffers)
        {
            var key = kv.Key;
            var state = kv.Value;

            byte[]? payload = null;
            uint freq = 48000;

            lock (state.Gate)
            {
                var now = DateTime.UtcNow;
                var longEnough = (now - state.LastFlush) >= FlushInterval;
                var idleEnough = (now - state.LastActivity) >= IdleFlush;
                if (state.Pcm.Length == 0 || (!longEnough && !idleEnough))
                    continue;

                payload = state.Pcm.ToArray();
                freq = state.Frequency;
                state.Pcm.SetLength(0);
                state.LastFlush = now;
            }

            if (payload is null || payload.Length == 0) continue;

            _log.LogInformation("AI-STT flushing [{Server}] client {Id}: {Bytes} bytes @ {Freq}Hz",
                key.Server, key.ClientId, payload.Length, freq);

            var text = await _ai.TranscribePcmAsync(payload, freq);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _log.LogInformation("AI-STT [{Server}] client {Id}: {Text}", key.Server, key.ClientId, text);
                OnSpeechRecognized?.Invoke(key.Server, key.ClientId, text);
            }
            else
            {
                _log.LogInformation("AI-STT produced no text for [{Server}] client {Id}", key.Server, key.ClientId);
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        foreach (var kv in _buffers)
        {
            lock (kv.Value.Gate)
                kv.Value.Pcm.Dispose();
        }
        _buffers.Clear();
        _announcedStreams.Clear();
    }

    private sealed class BufferState
    {
        public object Gate { get; } = new();
        public MemoryStream Pcm { get; } = new();
        public uint Frequency { get; set; } = 48000;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public DateTime LastFlush { get; set; } = DateTime.UtcNow;
    }
}
