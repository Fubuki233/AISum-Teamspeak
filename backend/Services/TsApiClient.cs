using System.Net.Http.Json;
using System.Text.Json;
using TsAi.Models;

namespace TsAi.Services;

public class TsApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TsApiClient> _log;

    public TsApiClient(HttpClient http, ILogger<TsApiClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<TsClient>> GetClientsAsync(ulong serverId = 1)
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonElement>(
                $"api/v1/servers/{serverId}/clients");

            var list = new List<TsClient>();
            if (json.TryGetProperty("clients", out var arr))
            {
                foreach (var c in arr.EnumerateArray())
                {
                    list.Add(new TsClient
                    {
                        ClientId = c.GetProperty("clientId").GetUInt16(),
                        ChannelId = c.GetProperty("channelId").GetUInt64(),
                        Nickname = c.GetProperty("nickname").GetString() ?? "",
                        Uid = c.GetProperty("uid").GetString() ?? ""
                    });
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch clients from TS API");
            return new List<TsClient>();
        }
    }

    public async Task<List<TsChannel>> GetChannelsAsync(ulong serverId = 1)
    {
        try
        {
            var json = await _http.GetFromJsonAsync<JsonElement>(
                $"api/v1/servers/{serverId}/channels");

            var list = new List<TsChannel>();
            if (json.TryGetProperty("channels", out var arr))
            {
                foreach (var c in arr.EnumerateArray())
                {
                    list.Add(new TsChannel
                    {
                        ChannelId = c.GetProperty("channelId").GetUInt64(),
                        Name = c.GetProperty("name").GetString() ?? "",
                        ParentId = c.TryGetProperty("parentId", out var p) ? p.GetUInt64() : 0,
                        Order = c.TryGetProperty("order", out var o) ? o.GetInt32() : 0
                    });
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch channels from TS API");
            return new List<TsChannel>();
        }
    }

    /// <summary>Verify a UID exists in the connected client list.</summary>
    public async Task<TsClient?> FindClientByUidAsync(string uid, ulong serverId = 1)
    {
        var clients = await GetClientsAsync(serverId);
        return clients.FirstOrDefault(c =>
            c.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase));
    }
}
