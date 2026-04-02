using System.Collections.Concurrent;

namespace TsAi.Services;

/// <summary>
/// Singleton: tracks all TS3 clients that have registered via the plugin's
/// PKTTYPE_REGISTER (0x02) UDP packets.  Used by the login page so users
/// can pick themselves from a list instead of typing their UID manually.
/// </summary>
public class ClientRegistry
{
    public record ClientInfo(
        string Server,
        ulong ChannelId,
        string ChannelName,
        ushort ClientId,
        string Uid,
        string Nickname,
        DateTime FirstSeen,
        DateTime LastSeen);

    private readonly ConcurrentDictionary<(string Server, ushort ClientId), ClientInfo> _clients = new();
    private readonly ConcurrentDictionary<(string Server, ulong ChannelId), KnownChannel> _channels = new();

    public void Register(string server, ulong channelId, string channelName, ushort clientId, string uid, string nickname)
    {
        if (channelId > 0)
            UpsertChannel(server, channelId, 0, channelName);

        var key = (server, clientId);
        _clients.AddOrUpdate(
            key,
            _ => new ClientInfo(server, channelId, channelName, clientId, uid, nickname, DateTime.UtcNow, DateTime.UtcNow),
            (_, old) => old with
            {
                ChannelId = channelId,
                ChannelName = string.IsNullOrWhiteSpace(channelName) ? old.ChannelName : channelName,
                Uid = string.IsNullOrEmpty(uid) ? old.Uid : uid,
                Nickname = string.IsNullOrEmpty(nickname) ? old.Nickname : nickname,
                LastSeen = DateTime.UtcNow
            });
    }

    public void UpsertChannel(string server, ulong channelId, ulong parentChannelId, string channelName)
    {
        if (string.IsNullOrWhiteSpace(server) || channelId == 0) return;

        var key = (server, channelId);
        _channels.AddOrUpdate(
            key,
            _ => new KnownChannel(server, channelId, parentChannelId, channelName, DateTime.UtcNow),
            (_, old) => old with
            {
                ParentChannelId = parentChannelId != 0 ? parentChannelId : old.ParentChannelId,
                ChannelName = string.IsNullOrWhiteSpace(channelName) ? old.ChannelName : channelName,
                LastSeen = DateTime.UtcNow
            });
    }

    /// <summary>All clients seen in the last 10 minutes, newest first.</summary>
    public record ChannelInfo(string Key, string Server, ulong ChannelId, ulong ParentChannelId, string ChannelName, string DisplayName, int Depth, int UserCount);
    private record KnownChannel(string Server, ulong ChannelId, ulong ParentChannelId, string ChannelName, DateTime LastSeen);

    public IReadOnlyList<ClientInfo> GetActive()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        return _clients.Values
            .Where(c => c.LastSeen >= cutoff)
            .OrderByDescending(c => c.LastSeen)
            .ToList();
    }

    public IReadOnlyList<ChannelInfo> GetActiveChannels()
    {
        var activeCutoff = DateTime.UtcNow.AddMinutes(-10);
        var activeUsers = _clients.Values
            .Where(c => c.LastSeen >= activeCutoff)
            .GroupBy(c => (c.Server, c.ChannelId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => !string.IsNullOrWhiteSpace(x.Uid)
                        ? $"uid:{x.Uid}"
                        : $"client:{x.ClientId}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());

        foreach (var c in _clients.Values.Where(c => c.ChannelId > 0))
            UpsertChannel(c.Server, c.ChannelId, 0, c.ChannelName);

        var result = new List<ChannelInfo>();
        foreach (var serverGroup in _channels.Values.GroupBy(c => c.Server).OrderBy(g => g.Key))
        {
            var byId = serverGroup.ToDictionary(c => c.ChannelId, c => c);
            var added = new HashSet<ulong>();

            void AddNode(KnownChannel ch, int depth)
            {
                if (!added.Add(ch.ChannelId)) return;

                var path = BuildPath(byId, ch.ChannelId);
                activeUsers.TryGetValue((ch.Server, ch.ChannelId), out var userCount);
                result.Add(new ChannelInfo(
                    $"{ch.Server}#ch-{ch.ChannelId}",
                    ch.Server,
                    ch.ChannelId,
                    ch.ParentChannelId,
                    ch.ChannelName,
                    path,
                    depth,
                    userCount));

                foreach (var child in serverGroup.Where(x => x.ParentChannelId == ch.ChannelId).OrderBy(x => x.ChannelName))
                    AddNode(child, depth + 1);
            }

            foreach (var root in serverGroup.Where(x => x.ParentChannelId == 0 || !byId.ContainsKey(x.ParentChannelId)).OrderBy(x => x.ChannelName))
                AddNode(root, 0);

            foreach (var orphan in serverGroup.Where(x => !added.Contains(x.ChannelId)).OrderBy(x => x.ChannelName))
                AddNode(orphan, 0);
        }

        return result;
    }

    private static string BuildPath(Dictionary<ulong, KnownChannel> byId, ulong channelId)
    {
        var parts = new List<string>();
        var seen = new HashSet<ulong>();
        var current = channelId;

        while (current != 0 && byId.TryGetValue(current, out var ch) && seen.Add(current))
        {
            if (!string.IsNullOrWhiteSpace(ch.ChannelName))
                parts.Add(ch.ChannelName);
            current = ch.ParentChannelId;
        }

        parts.Reverse();
        return parts.Count > 0 ? string.Join(" / ", parts) : $"频道 {channelId}";
    }

    public ClientInfo? Find(string server, ushort clientId)
    {
        _clients.TryGetValue((server, clientId), out var c);
        return c;
    }

    public bool IsHost(string server, ulong channelId, ushort candidateClientId)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        var host = _clients.Values
            .Where(c => c.Server == server && c.ChannelId == channelId && c.LastSeen >= cutoff)
            .OrderBy(c => c.FirstSeen)
            .ThenBy(c => c.ClientId)
            .FirstOrDefault();

        return host is not null && host.ClientId == candidateClientId;
    }

    public ClientInfo? FindByUid(string uid) =>
        _clients.Values
            .Where(c => c.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LastSeen)
            .ThenByDescending(c => c.ClientId)
            .FirstOrDefault();
}
