using System.Collections.Concurrent;
using TsAi.Models;

namespace TsAi.Services;

/// <summary>
/// Manages TS UID → guessed real-name mapping.
/// Periodically asks AI to refine the mapping as more conversation is collected.
/// </summary>
public class UserMappingService
{
    private readonly ConcurrentDictionary<string, string> _map = new();
    private readonly ByteDanceAiService _ai;
    private readonly ILogger<UserMappingService> _log;

    public UserMappingService(ByteDanceAiService ai, ILogger<UserMappingService> log)
    {
        _ai = ai;
        _log = log;
    }

    public Dictionary<string, string> CurrentMap =>
        new(_map);

    public string Resolve(string nickname) =>
        _map.TryGetValue(nickname, out var real) ? real : nickname;

    public async Task RefreshAsync(List<ChatMessage> messages)
    {
        if (messages.Count < 5) return;

        var guesses = await _ai.GuessRealNamesAsync(messages, new Dictionary<string, string>(_map));
        foreach (var kv in guesses)
        {
            _map[kv.Key] = kv.Value;
            _log.LogInformation("Name mapping: {Nick} → {Real}", kv.Key, kv.Value);
        }
    }
}
