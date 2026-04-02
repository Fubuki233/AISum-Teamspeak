using System.Text.Json;
using TsAi.Models;

namespace TsAi.Services;

/// <summary>
/// Persists channel transcripts/summaries to disk so chat history survives refreshes and container restarts.
/// </summary>
public sealed class ChatHistoryStore
{
    private readonly string _filePath;
    private readonly ILogger<ChatHistoryStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChatHistoryStore(IConfiguration cfg, ILogger<ChatHistoryStore> log)
    {
        _filePath = cfg["Persistence:ChatFilePath"] ?? "/app/data/chat-history.json";
        _log = log;
    }

    public async Task<Dictionary<string, PersistedChannelState>> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
                return [];

            await using var stream = File.OpenRead(_filePath);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, PersistedChannelState>>(stream, _jsonOptions);
            return data ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load persisted chat history from {Path}", _filePath);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<KeyValuePair<string, ChannelState>> channels)
    {
        var snapshot = channels.ToDictionary(
            kv => kv.Key,
            kv => Snapshot(kv.Value));

        await _gate.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tempPath = _filePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions);
                await stream.FlushAsync();
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist chat history to {Path}", _filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static PersistedChannelState Snapshot(ChannelState state)
    {
        return new PersistedChannelState
        {
            ChannelName = state.ChannelName,
            CurrentSession = CloneSession(state.CurrentSession, state.ChannelName),
            PastSessions = state.PastSessions.Select(s => CloneSession(s, state.ChannelName)).ToList()
        };
    }

    private static GameSession CloneSession(GameSession session, string fallbackChannelName)
    {
        var clone = new GameSession
        {
            Id = session.Id,
            StartTime = session.StartTime,
            EndTime = session.EndTime,
            ChannelName = string.IsNullOrWhiteSpace(session.ChannelName) ? fallbackChannelName : session.ChannelName
        };

        clone.Messages.AddRange(session.Messages.Select(m => new ChatMessage
        {
            Id = m.Id,
            ClientId = m.ClientId,
            ClientUid = m.ClientUid,
            ClientName = m.ClientName,
            Text = m.Text,
            Timestamp = m.Timestamp,
            ChannelName = m.ChannelName
        }));

        clone.Summaries.AddRange(session.Summaries.Select(s => new AiSummary
        {
            Id = s.Id,
            Timestamp = s.Timestamp,
            Content = s.Content,
            ChannelName = s.ChannelName,
            WasUpdated = s.WasUpdated
        }));

        return clone;
    }

    public sealed class PersistedChannelState
    {
        public string ChannelName { get; set; } = "";
        public GameSession CurrentSession { get; set; } = new();
        public List<GameSession> PastSessions { get; set; } = [];
    }
}
