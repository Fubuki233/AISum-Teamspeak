using System.Collections.Concurrent;
using System.Threading;
using TsAi.Models;

namespace TsAi.Services;

/// <summary>
/// Central orchestrator: stores messages, manages game sessions and summary timers.
/// Fires events consumed by Blazor components via SignalR circuit.
/// </summary>
public class ChatSessionManager : IDisposable
{
    private readonly ClientRegistry _registry;
    private readonly AiSttService _stt;
    private readonly ByteDanceAiService _ai;
    private readonly UserMappingService _names;
    private readonly ChatHistoryStore _historyStore;
    private readonly ILogger<ChatSessionManager> _log;
    private readonly SemaphoreSlim _summaryRunGate = new(1, 1);
    private static readonly TimeSpan SummaryIdleDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SummaryPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumSummaryGap = TimeSpan.FromMinutes(8);

    // channel name → state
    private readonly ConcurrentDictionary<string, ChannelState> _channels = new();

    private Timer? _summaryTimer;
    private Timer? _nameGuessTimer;

    // ---- Events for UI ----
    public event Action<string, ChatMessage>? OnMessage;          // channel, msg
    public event Action<string, AiSummary>? OnSummary;            // channel, summary
    public event Action<string>? OnSessionEnded;                  // channel
    public event Action? OnClientsChanged;

    public ChatSessionManager(
        ClientRegistry registry,
        AiSttService stt,
        ByteDanceAiService ai,
        UserMappingService names,
        ChatHistoryStore historyStore,
        ILogger<ChatSessionManager> log)
    {
        _registry = registry;
        _stt = stt;
        _ai = ai;
        _names = names;
        _historyStore = historyStore;
        _log = log;

        _stt.OnSpeechRecognized += HandleRecognizedSpeech;
    }

    public void Start()
    {
        LoadPersistedHistory();
        _summaryTimer = new Timer(_ => _ = GenerateSummariesAsync(), null, SummaryPollInterval, SummaryPollInterval);
        _nameGuessTimer = new Timer(_ => _ = RefreshNameMappingsAsync(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5));
        _log.LogInformation("ChatSessionManager started with summary idle trigger {IdleSeconds}s and minimum gap {GapMinutes}m", SummaryIdleDelay.TotalSeconds, MinimumSummaryGap.TotalMinutes);
    }

    // ---- Public queries ----

    public ChannelState GetOrCreateChannel(string channelName)
    {
        return _channels.GetOrAdd(channelName, n => new ChannelState
        {
            ChannelName = n,
            CurrentSession = new GameSession { ChannelName = n }
        });
    }

    public IReadOnlyList<string> GetActiveChannels() =>
        _channels.Keys.ToList();

    public TsClient? ResolveClient(ushort clientId)
    {
        var info = _registry.GetActive().FirstOrDefault(c => c.ClientId == clientId);
        if (info == null) return null;
        return new TsClient { ClientId = info.ClientId, Uid = info.Uid, Nickname = info.Nickname };
    }

    public List<TsClient> GetCachedClients() =>
        _registry.GetActive()
            .Select(c => new TsClient { ClientId = c.ClientId, Uid = c.Uid, Nickname = c.Nickname })
            .ToList();

    public string ResolveDisplayName(string nickname) =>
        _names.Resolve(nickname);

    public Task GenerateSummaryNowAsync(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
            return Task.CompletedTask;

        return GenerateSummariesAsync(
            forceChannel: channelName,
            bypassIdle: true,
            bypassMinimumGap: true,
            bypassContentGate: true,
            allowResummary: true);
    }

    // ---- Internal handlers ----

    private void HandleRecognizedSpeech(string serverAddress, ushort clientId, string text)
    {
        var info = _registry.Find(serverAddress, clientId);

        if (info == null)
        {
            _log.LogWarning("Speech from unknown client {Server}/{Id}, dropping", serverAddress, clientId);
            return;
        }

        var channelName = info.ChannelId > 0
            ? $"{serverAddress}#ch-{info.ChannelId}"
            : $"{serverAddress}#general";
        var channelState = GetOrCreateChannel(channelName);

        var now = DateTime.UtcNow;
        var session = channelState.CurrentSession;
        var last = session.Messages.LastOrDefault();

        if (ShouldMergeMessage(last, clientId, text, now))
        {
            last!.Text = MergeText(last.Text, text);
            last.Timestamp = now;
            if (!channelState.HasPendingSummary)
                channelState.PendingSummarySince = now;
            channelState.HasPendingSummary = true;
            channelState.LastSpeechAt = now;
            OnMessage?.Invoke(channelState.ChannelName, last);
            PersistHistory();
            return;
        }

        var msg = new ChatMessage
        {
            ClientId = clientId,
            ClientUid = info.Uid,
            ClientName = info.Nickname,
            Text = text,
            ChannelName = channelState.ChannelName,
            Timestamp = now
        };

        session.Messages.Add(msg);
        if (!channelState.HasPendingSummary)
            channelState.PendingSummarySince = now;
        channelState.HasPendingSummary = true;
        channelState.LastSpeechAt = now;
        OnMessage?.Invoke(channelState.ChannelName, msg);
        PersistHistory();
    }

    private static bool ShouldMergeMessage(ChatMessage? last, ushort clientId, string currentText, DateTime now)
    {
        if (last is null || last.ClientId != clientId) return false;
        if ((now - last.Timestamp) > TimeSpan.FromSeconds(8)) return false;
        if (string.IsNullOrWhiteSpace(currentText)) return false;

        var previous = last.Text.Trim();
        var current = currentText.Trim();
        if (previous.Length == 0 || current.Length == 0) return false;

        var shortFragment = current.Length <= 18 || previous.Length <= 20;
        var previousOpenEnded = !EndsWithTerminalPunctuation(previous);
        var continuation = StartsWithContinuationWord(current) || char.IsLower(current[0]);

        return shortFragment || previousOpenEnded || continuation;
    }

    private static string MergeText(string previous, string current)
    {
        previous = previous.Trim();
        current = current.Trim();
        if (previous.Length == 0) return current;
        if (current.Length == 0) return previous;

        var needsSpace = char.IsLetterOrDigit(previous[^1]) && char.IsLetterOrDigit(current[0]);
        return needsSpace ? $"{previous} {current}" : previous + current;
    }

    private static bool EndsWithTerminalPunctuation(string text) =>
        ".!?。！？…".Contains(text[^1]);

    private static bool StartsWithContinuationWord(string text)
    {
        var lowered = text.ToLowerInvariant();
        return lowered.StartsWith("and ")
            || lowered.StartsWith("but ")
            || lowered.StartsWith("so ")
            || lowered.StartsWith("then ")
            || lowered.StartsWith("the ")
            || lowered.StartsWith("a ")
            || lowered.StartsWith("la ")
            || lowered.StartsWith("然后")
            || lowered.StartsWith("这里");
    }


    private async Task GenerateSummariesAsync(string? forceChannel = null, bool bypassIdle = false, bool bypassMinimumGap = false, bool bypassContentGate = false, bool allowResummary = false)
    {
        if (!await _summaryRunGate.WaitAsync(0))
            return;

        try
        {
            var now = DateTime.UtcNow;

            foreach (var (chName, state) in _channels)
            {
                if (!string.IsNullOrWhiteSpace(forceChannel) && !string.Equals(chName, forceChannel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var canResummarizeLast = allowResummary
                    && !string.IsNullOrWhiteSpace(forceChannel)
                    && string.Equals(chName, forceChannel, StringComparison.OrdinalIgnoreCase)
                    && state.CurrentSession.Summaries.Count > 0;

                if ((!state.HasPendingSummary && !canResummarizeLast) || state.IsSummaryInProgress)
                    continue;

                if (!bypassIdle && (state.LastSpeechAt == default || (now - state.LastSpeechAt) < SummaryIdleDelay))
                    continue;

                state.IsSummaryInProgress = true;
                try
                {
                    var session = state.CurrentSession;
                    var lastSummary = session.Summaries.LastOrDefault();
                    var recentMessages = session.Messages
                        .Where(m => lastSummary is null
                            ? m.Timestamp > DateTime.UtcNow.AddMinutes(-3)
                            : m.Timestamp > lastSummary.Timestamp)
                        .ToList();

                    var usedLastSummaryFallback = false;
                    if (recentMessages.Count == 0 && canResummarizeLast && lastSummary is not null)
                    {
                        recentMessages = GetMessagesForLastSummary(session, lastSummary);
                        usedLastSummaryFallback = recentMessages.Count > 0;
                    }

                    if (recentMessages.Count == 0) continue;

                    var totalTextLength = recentMessages.Sum(m => m.Text?.Trim().Length ?? 0);
                    var hasEnoughContent = bypassContentGate || recentMessages.Count >= 2 || totalTextLength >= 60;
                    var cooldownSatisfied = bypassMinimumGap || state.LastSummaryAt == default || (now - state.LastSummaryAt) >= MinimumSummaryGap || recentMessages.Count >= 3;

                    if (!hasEnoughContent || !cooldownSatisfied)
                        continue;

                    var previous = session.Summaries.TakeLast(1).ToList();
                    var nameMap = _names.CurrentMap;

                    var result = await _ai.SummarizeAsync(chName, recentMessages, previous, nameMap);
                    if (string.IsNullOrWhiteSpace(result)) continue;

                    var requestedUpdate = result.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);
                    var isUpdate = usedLastSummaryFallback || (requestedUpdate && ShouldApplySummaryUpdate(session, recentMessages));
                    var content = result;
                    var firstNewline = result.IndexOf('\n');
                    if (firstNewline > 0)
                        content = result[(firstNewline + 1)..].Trim();

                    if (isUpdate && session.Summaries.Count > 0)
                    {
                        var last = session.Summaries[^1];
                        last.Content = content;
                        last.Timestamp = DateTime.UtcNow;
                        last.WasUpdated = true;
                        OnSummary?.Invoke(chName, last);
                    }
                    else
                    {
                        var summary = new AiSummary
                        {
                            Content = content,
                            ChannelName = chName
                        };
                        session.Summaries.Add(summary);
                        OnSummary?.Invoke(chName, summary);
                    }

                    state.HasPendingSummary = false;
                    state.PendingSummarySince = default;
                    state.LastSummaryAt = DateTime.UtcNow;
                    PersistHistory();
                    _log.LogInformation("Summary for {Channel}: {Action} after {IdleSeconds}s silence", chName, isUpdate ? "updated" : "new", SummaryIdleDelay.TotalSeconds);
                }
                finally
                {
                    state.IsSummaryInProgress = false;
                }
            }
        }
        finally
        {
            _summaryRunGate.Release();
        }
    }

    private static List<ChatMessage> GetMessagesForLastSummary(GameSession session, AiSummary lastSummary)
    {
        var previousSummary = session.Summaries.Count > 1 ? session.Summaries[^2] : null;
        var candidates = session.Messages
            .Where(m => m.Timestamp <= lastSummary.Timestamp
                && (previousSummary is null
                    ? m.Timestamp >= lastSummary.Timestamp.AddMinutes(-3)
                    : m.Timestamp > previousSummary.Timestamp))
            .ToList();

        return candidates.Count > 0
            ? candidates
            : session.Messages.TakeLast(8).ToList();
    }

    private static bool ShouldApplySummaryUpdate(GameSession session, List<ChatMessage> recentMessages)
    {
        if (session.Summaries.Count == 0)
            return false;

        var last = session.Summaries[^1];
        if (last.WasUpdated)
            return false;

        if ((DateTime.UtcNow - last.Timestamp) > TimeSpan.FromMinutes(5))
            return false;

        if (recentMessages.Count > 3)
            return false;

        var firstNewMessageAt = recentMessages.Min(m => m.Timestamp);
        return firstNewMessageAt <= last.Timestamp.AddMinutes(2);
    }

    private async Task RefreshNameMappingsAsync()
    {
        foreach (var (_, state) in _channels)
        {
            var allMessages = state.CurrentSession.Messages.ToList();
            await _names.RefreshAsync(allMessages);
        }
    }

    private void LoadPersistedHistory()
    {
        var persisted = _historyStore.LoadAsync().GetAwaiter().GetResult();
        foreach (var (channelKey, saved) in persisted)
        {
            var normalizedChannelName = string.IsNullOrWhiteSpace(saved.ChannelName) ? channelKey : saved.ChannelName;
            var normalizedSession = NormalizeSession(saved.CurrentSession, normalizedChannelName);
            var state = new ChannelState
            {
                ChannelName = normalizedChannelName,
                CurrentSession = normalizedSession,
                HasPendingSummary = false,
                LastSpeechAt = normalizedSession.Messages.LastOrDefault()?.Timestamp ?? default,
                PendingSummarySince = default,
                LastSummaryAt = normalizedSession.Summaries.LastOrDefault()?.Timestamp ?? default
            };

            state.PastSessions.AddRange((saved.PastSessions ?? []).Select(s => NormalizeSession(s, state.ChannelName)));
            _channels[channelKey] = state;
        }

        if (persisted.Count > 0)
            _log.LogInformation("Loaded persisted chat history for {Count} channels", persisted.Count);
    }

    private void PersistHistory()
    {
        _ = Task.Run(async () => await _historyStore.SaveAsync(_channels));
    }

    private static GameSession NormalizeSession(GameSession? session, string channelName)
    {
        if (session is null)
            return new GameSession { ChannelName = channelName };

        if (!string.IsNullOrWhiteSpace(session.ChannelName))
            return session;

        var normalized = new GameSession
        {
            Id = session.Id,
            StartTime = session.StartTime,
            EndTime = session.EndTime,
            ChannelName = channelName
        };
        normalized.Messages.AddRange(session.Messages);
        normalized.Summaries.AddRange(session.Summaries);
        return normalized;
    }

    public void Dispose()
    {
        _summaryTimer?.Dispose();
        _nameGuessTimer?.Dispose();
    }
}

public class ChannelState
{
    public string ChannelName { get; init; } = "";
    public GameSession CurrentSession { get; set; } = new();
    public List<GameSession> PastSessions { get; } = new();
    public bool HadUsersLastPoll { get; set; }
    public bool HasPendingSummary { get; set; }
    public DateTime LastSpeechAt { get; set; }
    public DateTime PendingSummarySince { get; set; }
    public DateTime LastSummaryAt { get; set; }
    public bool IsSummaryInProgress { get; set; }
}
