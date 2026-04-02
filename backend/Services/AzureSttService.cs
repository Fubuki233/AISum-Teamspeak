using System.Collections.Concurrent;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TsAi.Services;

/// <summary>
/// Manages one Azure SpeechRecognizer per active talker (server + clientId).
/// Receives raw PCM-16 mono audio and emits recognized text.
/// </summary>
public class AzureSttService : IDisposable
{
    private readonly ConcurrentDictionary<(string Server, ushort ClientId), ClientSttSession> _sessions = new();
    // Tracks clients that hit 4429; skip session creation until cooldown expires
    private readonly ConcurrentDictionary<(string Server, ushort ClientId), DateTime> _cooldowns = new();
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(30);
    private readonly string _speechKey;
    private readonly string _speechRegion;
    private readonly string _language;
    // <= 0 means unlimited (all active users can attempt STT sessions).
    private readonly int _maxConcurrent;
    private readonly ILogger<AzureSttService> _log;
    private readonly Timer _cleanupTimer;
    // Stop sessions idle longer than this to avoid hitting Azure concurrent-session limits
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    public event Action<string, ushort, string>? OnSpeechRecognized;

    public AzureSttService(IConfiguration cfg, ILogger<AzureSttService> log)
    {
        _speechKey = cfg["Azure:SpeechKey"] ?? "";
        _speechRegion = cfg["Azure:SpeechRegion"] ?? "eastasia";
        _language = cfg["Azure:SpeechLanguage"] ?? "zh-CN";
        _maxConcurrent = cfg.GetValue("Azure:MaxConcurrentSessions", 0);
        _log = log;
        _cleanupTimer = new Timer(_ => CleanupIdleSessions(), null,
                                  TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void CleanupIdleSessions()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var kv in _sessions)
        {
            if (kv.Value.LastActivity < cutoff)
            {
                _log.LogInformation("Stopping idle STT session [{S}]",
                                    kv.Key.Server);
                StopClient(kv.Key.Server, kv.Key.ClientId);
            }
        }
    }

    public void FeedAudio(string serverAddress, ushort clientId, uint frequency, byte[] audioData)
    {
        var key = (serverAddress, clientId);

        // Optional app-side cap: disabled by default to allow all users to connect.
        if (_maxConcurrent > 0 && !_sessions.ContainsKey(key) && _sessions.Count >= _maxConcurrent)
        {
            _log.LogDebug("STT session limit ({Max}) reached, dropping audio for [{S}]",
                          _maxConcurrent, serverAddress);
            return;
        }

        // Don't immediately reconnect after a 4429 rate-limit error
        if (_cooldowns.TryGetValue(key, out var until) && DateTime.UtcNow < until)
            return;

        var session = _sessions.GetOrAdd(key, _ => CreateSession(serverAddress, clientId, frequency));
        session.LastActivity = DateTime.UtcNow;
        session.AudioStream.Write(audioData);
    }

    public void StopClient(string serverAddress, ushort clientId)
    {
        var key = (serverAddress, clientId);
        if (_sessions.TryRemove(key, out var session))
            _ = StopSessionAsync(session);
    }

    private async Task StopSessionAsync(ClientSttSession session)
    {
        try
        {
            session.AudioStream.Close();
            await session.Recognizer.StopContinuousRecognitionAsync();
        }
        catch { /* best-effort */ }
        finally
        {
            session.Dispose();
        }
    }

    private ClientSttSession CreateSession(string serverAddress, ushort clientId, uint frequency)
    {
        var format = AudioStreamFormat.GetWaveFormatPCM(frequency, 16, 1);
        var pushStream = AudioInputStream.CreatePushStream(format);
        var audioConfig = AudioConfig.FromStreamInput(pushStream);

        var speechConfig = SpeechConfig.FromSubscription(_speechKey, _speechRegion);
        speechConfig.SpeechRecognitionLanguage = _language;

        var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech &&
                !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                _log.LogDebug("STT [{Server}]: {Text}", serverAddress, e.Result.Text);
                OnSpeechRecognized?.Invoke(serverAddress, clientId, e.Result.Text);
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                _log.LogWarning("STT error [{Server}]: {Detail}",
                                serverAddress, e.ErrorDetails);
                // Remove the zombie session immediately so future audio frames
                // can recreate a fresh connection instead of writing to a dead stream.
                StopClient(serverAddress, clientId);

                // 4429 = too many concurrent sessions; back off before reconnecting
                // to avoid a tight retry loop that keeps hammering the quota.
                if (e.ErrorDetails?.Contains("Error code: 4429", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var key = (serverAddress, clientId);
                    _cooldowns[key] = DateTime.UtcNow + RateLimitCooldown;
                    _log.LogWarning("Rate-limited by Azure (4429) for [{Server}]; " +
                                    "pausing STT for {Secs}s", serverAddress,
                                    (int)RateLimitCooldown.TotalSeconds);
                }
            }
        };

        recognizer.StartContinuousRecognitionAsync().Wait();
        _log.LogInformation("Started STT session for [{Server}] @ {Freq}Hz", serverAddress, frequency);

        return new ClientSttSession
        {
            AudioStream = pushStream,
            Recognizer = recognizer,
            AudioConfig = audioConfig
        };
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var kv in _sessions)
        {
            try
            {
                kv.Value.AudioStream.Close();
                kv.Value.Recognizer.StopContinuousRecognitionAsync().GetAwaiter().GetResult();
            }
            catch { /* best-effort */ }
            finally
            {
                kv.Value.Dispose();
            }
        }
        _sessions.Clear();
    }

    private class ClientSttSession : IDisposable
    {
        public required PushAudioInputStream AudioStream { get; init; }
        public required SpeechRecognizer Recognizer { get; init; }
        public required AudioConfig AudioConfig { get; init; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;

        public void Dispose()
        {
            Recognizer.Dispose();
            AudioConfig.Dispose();
            AudioStream.Dispose();
        }
    }
}
