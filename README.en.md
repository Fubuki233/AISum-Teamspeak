# AISum-Teamspeak

AISum-Teamspeak is a **TeamSpeak 3 client plugin + .NET 9 Blazor backend** for live speech transcription and AI summarization.

It works with a standard TeamSpeak 3 server. Audio is captured from the TS3 client through `ts_plugin`, then forwarded to the web backend for transcription and summary display.

---

## Features

- Compatible with standard TeamSpeak 3 servers, with no server-side modification required
- Real-time speech transcription, currently using DashScope STT as the main path
- In-channel AI summaries shown directly in the web page
- Switchable AI providers: `Auto / DashScope / Grok / Doubao`
- Supports channel tree, empty channels, and nested subchannels
- Automatically identifies TS user ID, UID, and nickname
- Chat history and AI summaries are persisted across page refreshes and container restarts
- All Docker-related files are kept under `backend/`

---

## Project Structure

```text
AISum-Teamspeak/
├── README.md                 # Chinese README
├── README.en.md              # English README
├── backend/                  # .NET 9 Blazor backend + Docker files
│   ├── docker-compose.yml
│   ├── Dockerfile.tsai
│   ├── .env / .env.example
│   ├── data/                 # Persistent data directory
│   │   ├── chat-history.json
│   │   └── keys/
│   ├── Components/
│   ├── Models/
│   ├── Services/
│   └── dashscope_stt.py
└── ts_plugin/                # TeamSpeak 3 client plugin source and build scripts
```

---

## Quick Start

### 1) Start the backend

All Docker files are under `backend/`:

```bash
cd backend
cp .env.example .env
docker compose up -d --build
```

Run Docker Compose from `backend/` only. Do not start other `docker-compose.yml` files from the repository root, or you may hit a duplicate `tsai` container name conflict.

If the host machine can access the internet but Docker builds stall at `apt-get update` or `dotnet restore`, the current compose file uses `build.network: host` so the build phase can bypass bridge-network DNS, IPv6, or MTU issues.

Check logs with:

```bash
cd backend
docker compose logs -f tsai
```

Open the web UI at:

```text
http://localhost:5002
```

### 2) Build and install the TS3 plugin

Build from source:

```bash
cd ts_plugin
bash build.sh
```

Output file:

```text
ts_plugin/out/ts_voice_forwarder.dll
```

If you already have the packaged plugin files on Windows, run:

```text
ts_plugin/install/install_plugin.bat
```

The installer prints only one of these results:

```text
Installation succeeded
```

or:

```text
Installation failed
```

For manual installation, copy the DLL to:

```text
C:\Users\<YourUserName>\AppData\Roaming\TS3Client\plugins\
```

Then copy or rename `ts_voice_forwarder.cfg.example` to `ts_voice_forwarder.cfg` in the same directory:

```ini
HOST=127.0.0.1
PORT=9988
```

If the backend is running on another machine, change `HOST` to that machine's IP address.

Restart TeamSpeak 3 and enable `ts_voice_forwarder` from the `Plugins` menu.

### 3) Use the web UI

1. Open `http://localhost:5002`
2. Select your TS identity
3. Select a channel
4. Choose an AI provider: `Auto / DashScope / Grok / Doubao`
5. Wait for transcription and summaries to appear

The current transcription strategy is tuned for completeness, so text may appear slightly later but usually in more complete sentences.

---

## Environment Variables

Edit `backend/.env`:

| Variable | Purpose | Required |
|---|---|---:|
| `DASHSCOPE_API_KEY` | DashScope STT / Chat | Recommended |
| `DOUBAO_API_KEY` | Doubao summaries | Optional |
| `DOUBAO_ENDPOINT_ID` | Doubao model ID | Optional |
| `XAI_API_KEY` | Grok / xAI summaries | Optional |
| `XAI_MODEL` | Default Grok model, defaults to `grok-4-1-fast-non-reasoning` | Optional |

By default:

- DashScope is preferred for STT
- Summary models can be switched from the frontend dropdown
- If one provider is unavailable, the backend may fall back to another available path

---

## Persistent Data

Runtime data is stored in:

```text
backend/data/
```

Important files include:

- `backend/data/chat-history.json`: chat history and AI summaries
- `backend/data/keys/`: ASP.NET Data Protection keys

This means:

- Refreshing the page does not lose history
- Restarting the Docker container does not lose history

---

## Common Commands

### Start
```bash
cd backend
docker compose up -d --build
```

### View logs
```bash
cd backend
docker compose logs -f tsai
```

### Restart
```bash
cd backend
docker compose restart tsai
```

### Stop
```bash
cd backend
docker compose down
```

---

## License

See `LICENSE`.