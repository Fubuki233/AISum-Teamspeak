using System.Net;
using System.Net.Sockets;
using TsAi.Models;

namespace TsAi.Services;

/// <summary>
/// Background service: receives UDP packets from the TS voice-forwarder plugin.
///
/// Packet type (first byte):
///   0x01  VOICE FRAME
///         [1B type][1B addrLen][L serverAddr][2B reporterId][2B speakerId][4B freq][4B dataSize][N PCM16]
///
///   0x02  CLIENT REGISTRATION
///         [1B type][1B addrLen][L serverAddr][2B clientId][8B channelId][1B channelNameLen][C channelName][1B uidLen][U uid][1B nickLen][K nick]
/// </summary>
public class VoiceReceiver : BackgroundService
{
    private const byte PKTTYPE_VOICE    = 0x01;
    private const byte PKTTYPE_REGISTER = 0x02;
    private const byte PKTTYPE_CHANNEL  = 0x03;

    private readonly ILogger<VoiceReceiver> _log;
    private readonly AiSttService _stt;
    private readonly ClientRegistry _registry;
    private readonly int _port;

    /// Hostnames that resolve to the same physical server.
    /// The first entry in each group is the canonical name used as the session key.
    private static readonly string[][] ServerAliasGroups =
    [
        ["oversea.zyh111.icu", "www.zyh111.icu"],
    ];

    public VoiceReceiver(ILogger<VoiceReceiver> log, AiSttService stt,
                         ClientRegistry registry, IConfiguration cfg)
    {
        _log      = log;
        _stt      = stt;
        _registry = registry;
        _port     = cfg.GetValue("Voice:UdpPort", 9988);
    }

    private static string NormalizeServer(string raw)
    {
        var lower = raw.ToLowerInvariant();
        foreach (var group in ServerAliasGroups)
            if (Array.Exists(group, a => a == lower)) return group[0];
        return lower;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(_port);
        _log.LogInformation("VoiceReceiver listening on UDP port {Port}", _port);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                var data   = result.Buffer;
                if (data.Length < 2) continue;

                byte first = data[0];

                if (first == PKTTYPE_VOICE || first == PKTTYPE_REGISTER || first == PKTTYPE_CHANNEL)
                {
                    // ── New format: [type][addrLen][addr]... ──────────────
                    int off = 1;
                    int addrLen = data[off++];
                    if (data.Length < off + addrLen) continue;
                    var serverAddr = addrLen > 0
                        ? System.Text.Encoding.UTF8.GetString(data, off, addrLen)
                        : string.Empty;
                    off += addrLen;
                    string server = NormalizeServer(serverAddr);

                    if (first == PKTTYPE_VOICE)         HandleVoice(data, off, server);
                    else if (first == PKTTYPE_REGISTER) HandleRegister(data, off, server);
                    else                                 HandleChannelInfo(data, off, server);
                }
                else
                {
                    // ── Legacy format (old DLL, no type byte): [addrLen][addr]... ──
                    int off = 0;
                    int addrLen = data[off++];
                    if (data.Length < off + addrLen + 10) continue;
                    var serverAddr = addrLen > 0
                        ? System.Text.Encoding.UTF8.GetString(data, off, addrLen)
                        : string.Empty;
                    off += addrLen;
                    string server = NormalizeServer(serverAddr);

                    // Parse voice frame; auto-register with numeric nickname so
                    // the login page can show "Client #N" even without a REGISTER packet.
                    var clientId = BitConverter.ToUInt16(data, off);
                    if (!_registry.GetActive().Any(c => c.Server == server && c.ClientId == clientId))
                        _registry.Register(server, 0, string.Empty, clientId, "", $"Client #{clientId}");

                    HandleVoiceLegacy(data, off, server);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "VoiceReceiver error"); }
        }
    }

    private void HandleVoice(byte[] data, int off, string server)
    {
        // need at least: 2 reporterId + 2 speakerId + 4 freq + 4 dataSize = 12 bytes
        if (data.Length - off < 12) return;

        var reporterId = BitConverter.ToUInt16(data, off); off += 2;
        var speakerId  = BitConverter.ToUInt16(data, off); off += 2;
        if (string.IsNullOrEmpty(server) || reporterId == 0 || speakerId == 0) return;

        var reporter = _registry.Find(server, reporterId);
        if (reporter is null) return;

        // Refresh reporter + speaker on every valid voice packet so .NET keeps
        // the latest server/channel membership even if the original REGISTER was old.
        // Do not gate on a synthetic "host" election here: if the plugin is installed
        // on a non-earliest client in the channel, that check drops every voice frame
        // and STT never sees any audio.
        _registry.Register(server, reporter.ChannelId, reporter.ChannelName, reporterId, reporter.Uid, reporter.Nickname);

        var speaker = _registry.Find(server, speakerId);
        _registry.Register(
            server,
            reporter.ChannelId,
            reporter.ChannelName,
            speakerId,
            speaker?.Uid ?? "",
            string.IsNullOrWhiteSpace(speaker?.Nickname) ? $"Client #{speakerId}" : speaker!.Nickname);

        var frequency  = BitConverter.ToUInt32(data, off); off += 4;
        /* dataSize */ off += 4;

        int audioBytes = data.Length - off;
        if (audioBytes <= 0) return;

        var audio = new byte[audioBytes];
        Buffer.BlockCopy(data, off, audio, 0, audioBytes);
        _stt.FeedAudio(server, speakerId, frequency, audio);
    }

    private void HandleVoiceLegacy(byte[] data, int off, string server)
    {
        // legacy: [clientId][freq][size][pcm]
        if (data.Length - off < 10) return;

        var clientId = BitConverter.ToUInt16(data, off); off += 2;
        if (string.IsNullOrEmpty(server) || clientId == 0) return;

        var client = _registry.Find(server, clientId);
        _registry.Register(server, client?.ChannelId ?? 0, client?.ChannelName ?? string.Empty, clientId, client?.Uid ?? "", client?.Nickname ?? $"Client #{clientId}");

        var frequency = BitConverter.ToUInt32(data, off); off += 4;
        /* dataSize */ off += 4;

        int audioBytes = data.Length - off;
        if (audioBytes <= 0) return;

        var audio = new byte[audioBytes];
        Buffer.BlockCopy(data, off, audio, 0, audioBytes);
        _stt.FeedAudio(server, clientId, frequency, audio);
    }

    private void HandleRegister(byte[] data, int off, string server)
    {
        // need at least: 2 clientId + 8 channelId + 1 fieldLen = 11 bytes remaining
        if (data.Length - off < 11) return;

        var clientId = BitConverter.ToUInt16(data, off); off += 2;
        if (string.IsNullOrEmpty(server) || clientId == 0) return;
        var channelId = BitConverter.ToUInt64(data, off); off += 8;

        var originalOff = off;
        string channelName = string.Empty;
        string uid = string.Empty;
        string nickname = string.Empty;

        bool parsedWithChannelName = false;
        if (data.Length - off >= 1)
        {
            int channelNameLen = data[off++];
            if (data.Length - off >= channelNameLen + 1)
            {
                channelName = channelNameLen > 0
                    ? System.Text.Encoding.UTF8.GetString(data, off, channelNameLen)
                    : string.Empty;
                off += channelNameLen;

                int uidLen = data[off++];
                if (data.Length - off >= uidLen + 1)
                {
                    uid = uidLen > 0
                        ? System.Text.Encoding.UTF8.GetString(data, off, uidLen)
                        : string.Empty;
                    off += uidLen;

                    int nickLen = data[off++];
                    if (data.Length - off >= nickLen)
                    {
                        nickname = nickLen > 0
                            ? System.Text.Encoding.UTF8.GetString(data, off, nickLen)
                            : string.Empty;
                        parsedWithChannelName = true;
                    }
                }
            }
        }

        if (!parsedWithChannelName)
        {
            off = originalOff;
            int uidLen = data[off++];
            if (data.Length - off < uidLen + 1) return;
            uid = uidLen > 0
                ? System.Text.Encoding.UTF8.GetString(data, off, uidLen)
                : string.Empty;
            off += uidLen;

            int nickLen = data[off++];
            if (data.Length - off < nickLen) return;
            nickname = nickLen > 0
                ? System.Text.Encoding.UTF8.GetString(data, off, nickLen)
                : string.Empty;
        }

        _registry.UpsertChannel(server, channelId, 0, channelName);
        _registry.Register(server, channelId, channelName, clientId, uid, nickname);
        _log.LogDebug("Registered client [{Server}] channel={Channel} name={ChannelName} nick={Nick}",
                  server, channelId, channelName, nickname);
    }

    private void HandleChannelInfo(byte[] data, int off, string server)
    {
        if (data.Length - off < 17) return; // 8 channelId + 8 parentId + 1 nameLen

        var channelId = BitConverter.ToUInt64(data, off); off += 8;
        var parentId  = BitConverter.ToUInt64(data, off); off += 8;
        var nameLen = data[off++];
        if (data.Length - off < nameLen) return;

        var channelName = nameLen > 0
            ? System.Text.Encoding.UTF8.GetString(data, off, nameLen)
            : string.Empty;

        _registry.UpsertChannel(server, channelId, parentId, channelName);
    }
}


