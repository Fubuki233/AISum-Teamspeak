/*
 * ts_voice_forwarder — TeamSpeak 3 Plugin
 *
 * Intercepts decoded voice PCM via onEditPlaybackVoiceDataEvent and forwards
 * each frame to the TsAi service via UDP using the same packet layout as the
 * SDK server's onVoiceDataEvent:
 *
 *   [1 byte   addrLen   – length of server address string (0-255)]
 *   [L bytes  serverAddr – TS3 server hostname, no null terminator]
 *   [2 bytes  clientID  – uint16  LE]
 *   [4 bytes  frequency – uint32  LE]  (always 48000)
 *   [4 bytes  dataSize  – int32   LE]  (bytes of PCM data)
 *   [N bytes  PCM 16-bit LE samples, interleaved channels]
 *
 * Configuration (read on init, in priority order):
 *   1. %APPDATA%\TS3Client\plugins\ts_voice_forwarder.cfg
 *      - format:  HOST=<ip>\nPORT=<port>
 *   2. Environment variables  TS_VOICE_FWD_HOST / TS_VOICE_FWD_PORT
 *   3. Defaults: 127.0.0.1 : 9988
 *
 * Build: see build.sh (MinGW cross-compile) or CMakeLists.txt (CMake).
 */

#ifdef _WIN32
#ifdef _MSC_VER
#pragma warning(disable : 4100)   /* unreferenced formal parameter */
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#define PLUGINS_EXPORTDLL __declspec(dllexport)
typedef SOCKET ts_socket_t;
#define TS_INVALID_SOCKET INVALID_SOCKET
#define ts_closesocket    closesocket
#else
/* Linux stub – lets the code compile for testing, not a real plugin target */
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#define PLUGINS_EXPORTDLL __attribute__((visibility("default")))
typedef int ts_socket_t;
#define TS_INVALID_SOCKET (-1)
#define ts_closesocket close
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "teamspeak/public_definitions.h"
#include "ts3_functions.h"
#include "plugin_definitions.h"

/* ── plugin metadata ─────────────────────────────────────────────────────── */
#define PLUGIN_API_VERSION  26
#define PLUGIN_NAME         "TS Voice Forwarder"
#define PLUGIN_VERSION      "1.0"
#define PLUGIN_AUTHOR       "ts_sdk project"
#define PLUGIN_DESCRIPTION  "Captures TS3 voice PCM and forwards frames to AI service via UDP."

/* ── build-time constants ───────────────────────────────────────────────── */
#define DEFAULT_HOST        "127.0.0.1"
#define DEFAULT_PORT        9988
#define UDP_MAX_PAYLOAD     65507   /* max safe UDP payload */
#define HEADER_SIZE         12      /* 2 reporter + 2 speaker + 4 freq + 4 size */

/* Packet type identifiers (first byte of every UDP datagram) */
#define PKTTYPE_VOICE       0x01   /* voice PCM frame           */
#define PKTTYPE_REGISTER    0x02   /* client registration/hello */
#define PKTTYPE_CHANNEL     0x03   /* channel metadata/tree     */

/* ── module state ────────────────────────────────────────────────────────── */
static struct TS3Functions g_ts3;               /* SDK function table (unused but required) */
static ts_socket_t         g_sock  = TS_INVALID_SOCKET;
static struct sockaddr_in  g_dest;
static int                 g_ready = 0;
static unsigned long       g_last_reg_ms = 0;
static unsigned long       g_last_channel_sync_ms = 0;

/* ── config helpers ─────────────────────────────────────────────────────── */

static void trim_newline(char *s) {
    size_t n = strlen(s);
    while (n > 0 && (s[n-1] == '\n' || s[n-1] == '\r' || s[n-1] == ' '))
        s[--n] = '\0';
}

/*
 * read_config – populate host (max hostLen chars) and *port.
 * Priority: cfg file → env vars → defaults.
 */
static void read_config(char *host, size_t hostLen, int *port) {
    /* set defaults first */
    strncpy(host, DEFAULT_HOST, hostLen - 1);
    host[hostLen - 1] = '\0';
    *port = DEFAULT_PORT;

#ifdef _WIN32
    /* ── 1. try %APPDATA%\TS3Client\plugins\ts_voice_forwarder.cfg ──── */
    char cfgPath[MAX_PATH];
    DWORD ret = GetEnvironmentVariableA("APPDATA", cfgPath, sizeof(cfgPath));
    if (ret > 0 && ret < sizeof(cfgPath)) {
        strncat(cfgPath, "\\TS3Client\\plugins\\ts_voice_forwarder.cfg",
                sizeof(cfgPath) - strlen(cfgPath) - 1);
        FILE *f = fopen(cfgPath, "r");
        if (f) {
            char line[256];
            while (fgets(line, sizeof(line), f)) {
                trim_newline(line);
                if (strncmp(line, "HOST=", 5) == 0) {
                    strncpy(host, line + 5, hostLen - 1);
                    host[hostLen - 1] = '\0';
                } else if (strncmp(line, "PORT=", 5) == 0) {
                    int p = atoi(line + 5);
                    if (p > 0 && p <= 65535) *port = p;
                }
            }
            fclose(f);
            return; /* cfg file wins */
        }
    }

    /* ── 2. try environment variables ────────────────────────────────── */
    char envVal[256];
    if (GetEnvironmentVariableA("TS_VOICE_FWD_HOST", envVal, sizeof(envVal)) > 0) {
        snprintf(host, hostLen, "%s", envVal);
    }
    if (GetEnvironmentVariableA("TS_VOICE_FWD_PORT", envVal, sizeof(envVal)) > 0) {
        int p = atoi(envVal);
        if (p > 0 && p <= 65535) *port = p;
    }
#endif
    /* (defaults already set for Linux / no-cfg / no-env cases) */
}

/* ═══════════════════════════════════════════════════════════════════════════
 * REQUIRED PLUGIN EXPORTS
 * ═══════════════════════════════════════════════════════════════════════════ */

PLUGINS_EXPORTDLL const char *ts3plugin_name()        { return PLUGIN_NAME; }
PLUGINS_EXPORTDLL const char *ts3plugin_version()     { return PLUGIN_VERSION; }
PLUGINS_EXPORTDLL int         ts3plugin_apiVersion()  { return PLUGIN_API_VERSION; }
PLUGINS_EXPORTDLL const char *ts3plugin_author()      { return PLUGIN_AUTHOR; }
PLUGINS_EXPORTDLL const char *ts3plugin_description() { return PLUGIN_DESCRIPTION; }

PLUGINS_EXPORTDLL void ts3plugin_setFunctionPointers(const struct TS3Functions funcs) {
    g_ts3 = funcs;
}

/* ts3plugin_init – called once after the plugin DLL is loaded.
 * Returns 0 on success, non-zero to abort loading. */
PLUGINS_EXPORTDLL int ts3plugin_init(void) {
    char host[256];
    int  port;
    read_config(host, sizeof(host), &port);

#ifdef _WIN32
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 1;
#endif

    g_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (g_sock == TS_INVALID_SOCKET) {
#ifdef _WIN32
        WSACleanup();
#endif
        return 1;
    }

    /* Resolve hostname (works for both plain IPs and domain names like oversea.zyh111.icu) */
    struct addrinfo hints, *res = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family   = AF_INET;       /* IPv4 only */
    hints.ai_socktype = SOCK_DGRAM;
    char portStr[8];
    snprintf(portStr, sizeof(portStr), "%d", port);
    if (getaddrinfo(host, portStr, &hints, &res) != 0 || !res) {
        ts_closesocket(g_sock);
        g_sock = TS_INVALID_SOCKET;
#ifdef _WIN32
        WSACleanup();
#endif
        return 1;
    }
    memcpy(&g_dest, res->ai_addr, sizeof(g_dest));
    freeaddrinfo(res);

    g_ready = 1;
    /* Log via TS3 SDK so the user can confirm the plugin started */
    g_ts3.logMessage("TS Voice Forwarder: started, forwarding to UDP target",
                     LogLevel_INFO, "VoiceFwd", 0);
    return 0;
}

/* ts3plugin_shutdown – called when the plugin is unloaded or TS3 exits. */
PLUGINS_EXPORTDLL void ts3plugin_shutdown(void) {
    g_ready = 0;
    if (g_sock != TS_INVALID_SOCKET) {
        ts_closesocket(g_sock);
        g_sock = TS_INVALID_SOCKET;
    }
#ifdef _WIN32
    WSACleanup();
#endif
}

/* ═══════════════════════════════════════════════════════════════════════════
 * INTERNAL HELPER – resolve server hostname for a connection handler
 * ═══════════════════════════════════════════════════════════════════════════ */
static void get_server_host(uint64 scHandlerID, char *buf, size_t bufLen)
{
    unsigned short port = 0;
    char pass[4] = {0};
    if (g_ts3.getServerConnectInfo(scHandlerID, buf, &port, pass, (int)bufLen) != 0)
        buf[0] = '\0';
}

static void send_channel_packet(const char *serverHost, uint64 channelId, uint64 parentId, const char *channelName)
{
    if (!g_ready || g_sock == TS_INVALID_SOCKET) return;

    const char *safeServer = serverHost ? serverHost : "";
    const char *safeName = channelName ? channelName : "";
    unsigned char addrLen = (unsigned char)(strlen(safeServer) & 0xFF);
    unsigned char nameLen = (unsigned char)(strlen(safeName) & 0xFF);

    int totalLen = 1 + 1 + (int)addrLen + 8 + 8 + 1 + (int)nameLen;
    unsigned char *pkt = (unsigned char *)malloc((size_t)totalLen);
    if (!pkt) return;

    int off = 0;
    pkt[off++] = PKTTYPE_CHANNEL;
    pkt[off++] = addrLen;
    if (addrLen > 0) { memcpy(pkt + off, safeServer, addrLen); off += addrLen; }

    pkt[off++] = (unsigned char)( channelId        & 0xFF);
    pkt[off++] = (unsigned char)((channelId >>  8) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 16) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 24) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 32) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 40) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 48) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 56) & 0xFF);

    pkt[off++] = (unsigned char)( parentId        & 0xFF);
    pkt[off++] = (unsigned char)((parentId >>  8) & 0xFF);
    pkt[off++] = (unsigned char)((parentId >> 16) & 0xFF);
    pkt[off++] = (unsigned char)((parentId >> 24) & 0xFF);
    pkt[off++] = (unsigned char)((parentId >> 32) & 0xFF);
    pkt[off++] = (unsigned char)((parentId >> 40) & 0xFF);
    pkt[off++] = (unsigned char)((parentId >> 48) & 0xFF);
    pkt[off++] = (unsigned char)((parentId >> 56) & 0xFF);

    pkt[off++] = nameLen;
    if (nameLen > 0) { memcpy(pkt + off, safeName, nameLen); off += nameLen; }

    sendto(g_sock, (const char *)pkt, (size_t)totalLen, 0,
           (struct sockaddr *)&g_dest, (int)sizeof(g_dest));
    free(pkt);
}

static void send_channel_tree(uint64 scHandlerID)
{
    uint64 *channels = NULL;
    char serverHost[256] = {0};
    get_server_host(scHandlerID, serverHost, sizeof(serverHost));

    if (g_ts3.getChannelList(scHandlerID, &channels) != 0 || !channels)
        return;

    for (size_t i = 0; channels[i] != 0; ++i) {
        uint64 channelId = channels[i];
        uint64 parentId = 0;
        char *channelNameStr = NULL;

        g_ts3.getParentChannelOfChannel(scHandlerID, channelId, &parentId);
        g_ts3.getChannelVariableAsString(scHandlerID, channelId, CHANNEL_NAME, &channelNameStr);

        send_channel_packet(serverHost, channelId, parentId, channelNameStr ? channelNameStr : "");

        if (channelNameStr)
            g_ts3.freeMemory(channelNameStr);
    }

    g_ts3.freeMemory(channels);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * INTERNAL HELPER – build and send one UDP voice frame  (type = 0x01)
 *
 * Packet layout (little-endian):
 *   [0]      type      = PKTTYPE_VOICE (0x01)
 *   [1]      addrLen   – uint8
 *   [2..L+1] serverAddr – hostname, no null terminator
 *   [L+2]    clientID  – uint16 LE
 *   [L+4]    frequency – uint32 LE (always 48000)
 *   [L+8]    dataSize  – int32  LE
 *   [L+12…]  PCM int16 LE
 * ═══════════════════════════════════════════════════════════════════════════ */
static void send_voice_frame(uint64 scHandlerID, anyID reporterID, anyID speakerID,
                             short *samples, int sampleCount, int channels)
{
    if (!g_ready || g_sock == TS_INVALID_SOCKET) return;
    if (sampleCount <= 0 || channels <= 0 || !samples) return;

    char serverHost[256] = {0};
    get_server_host(scHandlerID, serverHost, sizeof(serverHost));
    unsigned char addrLen = (unsigned char)(strlen(serverHost) & 0xFF);

    int pcmBytes = sampleCount * channels * (int)sizeof(short);
    if (pcmBytes <= 0) return;
    /* 1 (type) + 1 (addrLen) + addrLen + HEADER_SIZE */
    int headerSz = 2 + (int)addrLen + HEADER_SIZE;
    if (pcmBytes > UDP_MAX_PAYLOAD - headerSz) return;

    int totalLen = headerSz + pcmBytes;
    unsigned char *pkt = (unsigned char *)malloc((size_t)totalLen);
    if (!pkt) return;

    int off = 0;
    pkt[off++] = PKTTYPE_VOICE;
    pkt[off++] = addrLen;
    if (addrLen > 0) { memcpy(pkt + off, serverHost, addrLen); off += addrLen; }
    pkt[off++] = (unsigned char)( reporterID        & 0xFF);
    pkt[off++] = (unsigned char)((reporterID >>  8) & 0xFF);
    pkt[off++] = (unsigned char)( speakerID        & 0xFF);
    pkt[off++] = (unsigned char)((speakerID >>  8) & 0xFF);
    unsigned int freq = 48000u;
    pkt[off++] = (unsigned char)( freq        & 0xFF);
    pkt[off++] = (unsigned char)((freq >>  8) & 0xFF);
    pkt[off++] = (unsigned char)((freq >> 16) & 0xFF);
    pkt[off++] = (unsigned char)((freq >> 24) & 0xFF);
    pkt[off++] = (unsigned char)( pcmBytes        & 0xFF);
    pkt[off++] = (unsigned char)((pcmBytes >>  8) & 0xFF);
    pkt[off++] = (unsigned char)((pcmBytes >> 16) & 0xFF);
    pkt[off++] = (unsigned char)((pcmBytes >> 24) & 0xFF);
    memcpy(pkt + off, samples, (size_t)pcmBytes);

    sendto(g_sock, (const char *)pkt, (size_t)totalLen, 0,
           (struct sockaddr *)&g_dest, (int)sizeof(g_dest));
    free(pkt);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * INTERNAL HELPER – send client registration packet  (type = 0x02)
 *
 *   [0]      type       = PKTTYPE_REGISTER (0x02)
 *   [1]      addrLen    – uint8
 *   [2..L+1] serverAddr
 *   [L+2]    clientID   – uint16 LE
 *   [L+4]    channelID  – uint64 LE
 *   [L+12]   channelNameLen – uint8
 *   [..]     channelName – UTF-8
 *   [..]     uidLen     – uint8
 *   [..]     uid        – UTF-8 base64 string
 *   [..]     nickLen    – uint8
 *   [..]     nickname   – UTF-8
 * ═══════════════════════════════════════════════════════════════════════════ */
static void send_registration_for_client(uint64 scHandlerID, anyID clientId)
{
    if (!g_ready || g_sock == TS_INVALID_SOCKET || clientId == 0) return;

    uint64 channelId = 0;
    g_ts3.getChannelOfClient(scHandlerID, clientId, &channelId);

    char serverHost[256] = {0};
    get_server_host(scHandlerID, serverHost, sizeof(serverHost));

    char *uidStr  = NULL;
    char *nickStr = NULL;
    char *channelNameStr = NULL;
    g_ts3.getClientVariableAsString(scHandlerID, clientId,
                                    CLIENT_UNIQUE_IDENTIFIER, &uidStr);
    g_ts3.getClientVariableAsString(scHandlerID, clientId,
                                    CLIENT_NICKNAME, &nickStr);
    g_ts3.getChannelVariableAsString(scHandlerID, channelId,
                                     CHANNEL_NAME, &channelNameStr);

    const char *uid  = uidStr  ? uidStr  : "";
    const char *nick = nickStr ? nickStr : "";
    const char *channelName = channelNameStr ? channelNameStr : "";

    unsigned char addrLen = (unsigned char)(strlen(serverHost) & 0xFF);
    unsigned char channelNameLen = (unsigned char)(strlen(channelName) & 0xFF);
    unsigned char uidLen  = (unsigned char)(strlen(uid) & 0xFF);
    unsigned char nickLen = (unsigned char)(strlen(nick) & 0xFF);

    int totalLen = 1 + 1 + (int)addrLen + 2 + 8 + 1 + (int)channelNameLen + 1 + (int)uidLen + 1 + (int)nickLen;
    unsigned char *pkt = (unsigned char *)malloc((size_t)totalLen);
    if (!pkt) goto done;

    int off = 0;
    pkt[off++] = PKTTYPE_REGISTER;
    pkt[off++] = addrLen;
    if (addrLen > 0) { memcpy(pkt + off, serverHost, addrLen); off += addrLen; }
    pkt[off++] = (unsigned char)( clientId        & 0xFF);
    pkt[off++] = (unsigned char)((clientId >>  8) & 0xFF);
    pkt[off++] = (unsigned char)( channelId        & 0xFF);
    pkt[off++] = (unsigned char)((channelId >>  8) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 16) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 24) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 32) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 40) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 48) & 0xFF);
    pkt[off++] = (unsigned char)((channelId >> 56) & 0xFF);
    pkt[off++] = channelNameLen;
    if (channelNameLen > 0) { memcpy(pkt + off, channelName, channelNameLen); off += channelNameLen; }
    pkt[off++] = uidLen;
    if (uidLen > 0) { memcpy(pkt + off, uid, uidLen); off += uidLen; }
    pkt[off++] = nickLen;
    if (nickLen > 0) { memcpy(pkt + off, nick, nickLen); off += nickLen; }

    sendto(g_sock, (const char *)pkt, (size_t)totalLen, 0,
           (struct sockaddr *)&g_dest, (int)sizeof(g_dest));
    free(pkt);

done:
    if (uidStr)         g_ts3.freeMemory(uidStr);
    if (nickStr)        g_ts3.freeMemory(nickStr);
    if (channelNameStr) g_ts3.freeMemory(channelNameStr);
}

static void send_registration(uint64 scHandlerID)
{
    anyID *clients = NULL;
    if (!g_ready || g_sock == TS_INVALID_SOCKET) return;

    if (g_ts3.getClientList(scHandlerID, &clients) != 0 || !clients)
        return;

    for (size_t i = 0; clients[i] != 0; ++i)
        send_registration_for_client(scHandlerID, clients[i]);

    g_ts3.freeMemory(clients);
}

/* ═══════════════════════════════════════════════════════════════════════════
 * VOICE CALLBACKS
 * ═══════════════════════════════════════════════════════════════════════════ */

/* Called for every decoded playback frame from OTHER users in the channel. */
PLUGINS_EXPORTDLL void ts3plugin_onEditPlaybackVoiceDataEvent(
        uint64 serverConnectionHandlerID,
        anyID  clientID,
        short *samples,
        int    sampleCount,
        int    channels)
{
    anyID myId = 0;
    g_ts3.getClientID(serverConnectionHandlerID, &myId);
    send_voice_frame(serverConnectionHandlerID, myId, clientID, samples, sampleCount, channels);

#ifdef _WIN32
    {
        unsigned long now = GetTickCount();
        if (now - g_last_reg_ms > 2000) {
            g_last_reg_ms = now;
            send_registration(serverConnectionHandlerID);
        }
        if (now - g_last_channel_sync_ms > 15000) {
            g_last_channel_sync_ms = now;
            send_channel_tree(serverConnectionHandlerID);
        }
    }
#endif
}

/* Called for the plugin-user's OWN captured microphone audio.
 * Forward it as reporter=self and speaker=self so the host user's own speech
 * is also transcribed even when nobody else in the channel has the plugin. */
PLUGINS_EXPORTDLL void ts3plugin_onEditCapturedVoiceDataEvent(
        uint64 serverConnectionHandlerID,
        short *samples,
        int    sampleCount,
        int    channels,
        int   *edited)
{
    (void)edited;

    anyID myId = 0;
    if (g_ts3.getClientID(serverConnectionHandlerID, &myId) == 0 && myId != 0)
        send_voice_frame(serverConnectionHandlerID, myId, myId, samples, sampleCount, channels);

#ifdef _WIN32
    {
        unsigned long now = GetTickCount();
        if (now - g_last_reg_ms > 2000) {
            g_last_reg_ms = now;
            send_registration(serverConnectionHandlerID);
        }
        if (now - g_last_channel_sync_ms > 15000) {
            g_last_channel_sync_ms = now;
            send_channel_tree(serverConnectionHandlerID);
        }
    }
#endif
}

/* ═══════════════════════════════════════════════════════════════════════════
 * OPTIONAL STUBS – minimal set to keep TS3 happy
 * ═══════════════════════════════════════════════════════════════════════════ */

/* Called by TS3 to free any memory the plugin allocated (e.g. infoData). */
PLUGINS_EXPORTDLL void ts3plugin_freeMemory(void *data) {
    free(data);
}

/* Right-click context panel title (NULL = not shown). */
PLUGINS_EXPORTDLL const char *ts3plugin_infoTitle(void) {
    return NULL;
}

/* Right-click context panel content. */
PLUGINS_EXPORTDLL void ts3plugin_infoData(
        uint64 serverConnectionHandlerID, uint64 id,
        enum PluginItemType type, char **data) {
    (void)serverConnectionHandlerID; (void)id; (void)type;
    *data = NULL;
}

/* Plugin chat command keyword (NULL = no chat command). */
PLUGINS_EXPORTDLL const char *ts3plugin_commandKeyword(void) {
    return NULL;
}

/* Plugin chat command handler. */
PLUGINS_EXPORTDLL int ts3plugin_processCommand(
        uint64 serverConnectionHandlerID, const char *command) {
    (void)serverConnectionHandlerID; (void)command;
    return 0;
}

/* Called when the active server connection changes. */
PLUGINS_EXPORTDLL void ts3plugin_currentServerConnectionChanged(
        uint64 serverConnectionHandlerID) {
    g_last_channel_sync_ms = 0;
    send_channel_tree(serverConnectionHandlerID);
    send_registration(serverConnectionHandlerID);
}

/* Called when a client moves channels. If it's us, refresh our registration so
 * the .NET side immediately sees the new channel instead of waiting for speech. */
PLUGINS_EXPORTDLL void ts3plugin_onClientMoveEvent(
        uint64 serverConnectionHandlerID,
        anyID  clientID,
        uint64 oldChannelID,
        uint64 newChannelID,
        int    visibility,
        const char *moveMessage)
{
    (void)clientID; (void)oldChannelID; (void)newChannelID; (void)visibility; (void)moveMessage;
    g_last_channel_sync_ms = 0;
    g_last_reg_ms = 0;
    send_channel_tree(serverConnectionHandlerID);
    send_registration(serverConnectionHandlerID);
}

/* Called whenever a connection's status changes.
 * Send our registration once fully connected so TsAi knows who we are. */
PLUGINS_EXPORTDLL void ts3plugin_onConnectStatusChangeEvent(
        uint64 serverConnectionHandlerID,
        int    newStatus,
        unsigned int errorNumber)
{
    (void)errorNumber;
    if (newStatus == STATUS_CONNECTION_ESTABLISHED) {
        g_last_reg_ms = 0;
        g_last_channel_sync_ms = 0;
        send_channel_tree(serverConnectionHandlerID);
        send_registration(serverConnectionHandlerID);
    }
}

/* ── Windows DLL entry point ─────────────────────────────────────────────── */
#ifdef _WIN32
BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    (void)hModule; (void)reason; (void)reserved;
    return TRUE;
}
#endif
