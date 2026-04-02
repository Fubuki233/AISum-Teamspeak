# AISum-Teamspeak

一个基于 **TeamSpeak 3 客户端插件 + .NET 9 Blazor 后端** 的语音转写与 AI 总结工具。

它不依赖自定义 TS 服务器，直接通过 `ts_plugin` 在 **标准 TS3 客户端** 内截获语音 PCM，并将文本与总结展示在网页中。

English README: [README.en.md](README.en.md)

---

## ✨ 功能特性

- **兼容标准 TeamSpeak 3 服务器**，不需要改服务端
- **实时语音转写**（当前主路径：DashScope STT）
- **频道内 AI 总结**，支持在页面中直接查看
- **AI 可切换**：`自动 / DashScope / Grok / Doubao`
- **支持频道树、空频道显示、子频道层级**
- **自动识别 TS 用户 ID / UID / 昵称**
- **聊天记录与总结持久化**，刷新页面或重启容器后仍可恢复
- **Docker 相关文件统一放在 `backend/` 目录下**

---

## 📁 项目结构

```text
AISum-Teamspeak/
├── README.md                 # 当前说明文档
├── backend/                  # .NET 9 Blazor 后端 + Docker 文件
│   ├── docker-compose.yml
│   ├── Dockerfile.tsai
│   ├── .env / .env.example
│   ├── data/                 # 持久化数据目录
│   │   ├── chat-history.json
│   │   └── keys/
│   ├── Components/
│   ├── Models/
│   ├── Services/
│   └── dashscope_stt.py
└── ts_plugin/                # TeamSpeak 3 客户端插件源码与构建脚本
```

---

## 🚀 快速开始

### 1) 启动后端

所有 Docker 相关文件都在 `backend/` 中：

```bash
cd backend
cp .env.example .env   # 首次运行时可先复制
docker compose up -d --build
```

请直接在 `backend/` 目录执行上述命令，不要在仓库根目录执行其它 `docker-compose.yml`，否则可能因为重复的 `tsai` 容器名发生冲突。

如果宿主机可以访问外网，但 Docker 构建阶段卡在 `apt-get update` 或 `dotnet restore`，当前 compose 已配置构建阶段使用宿主机网络 `build.network: host`，优先绕过 Docker 默认桥接网络的 DNS / IPv6 / MTU 问题。

查看日志：

```bash
cd backend
docker compose logs -f tsai
```

启动成功后访问：

```text
http://localhost:5002
```

---

### 2) 编译并安装 TS3 插件

```bash
cd ts_plugin
bash build.sh
```

输出文件：

```text
ts_plugin/out/ts_voice_forwarder.dll
```

Windows 下如果你已经拿到安装包，可直接运行：

```text
ts_plugin/install/install_plugin.bat
```

脚本会把 DLL 和默认配置复制到 TS3 插件目录，并且只输出两种结果之一：

```text
安装成功
```

或：

```text
安装失败
```

如果你是在源码目录中手动安装，按下面方式处理。

将它复制到 TS3 插件目录，例如：

```text
C:\Users\<你的用户名>\AppData\Roaming\TS3Client\plugins\
```

然后把 `ts_voice_forwarder.cfg.example` 复制/改名为：

```text
ts_voice_forwarder.cfg
```

示例内容：

```ini
HOST=127.0.0.1
PORT=9988
```

> 如果后端不在本机，请把 `HOST` 改成后端机器 IP。

重启 TeamSpeak 3，并在 `Plugins` 菜单中启用 `ts_voice_forwarder`。

---

### 3) 进入网页使用

1. 打开 `http://localhost:5002`
2. 选择你的 TS 身份
3. 选择频道
4. 顶部可选择 AI：`自动 / DashScope / Grok / Doubao`
5. 等待语音转写与 AI 总结出现

> 当前转写策略偏向“**完整性优先**”，因此会**稍晚出字**，但句子会更完整。

---

## 🔑 环境变量说明

编辑 `backend/.env`：

| 变量 | 作用 | 是否必需 |
|---|---|---:|
| `DASHSCOPE_API_KEY` | DashScope STT / Chat | **推荐** |
| `DOUBAO_API_KEY` | Doubao 总结 | 可选 |
| `DOUBAO_ENDPOINT_ID` | Doubao 模型 ID | 可选 |
| `XAI_API_KEY` | Grok / xAI 总结 | 可选 |
| `XAI_MODEL` | 默认 Grok 模型，默认 `grok-4-1-fast-non-reasoning` | 可选 |

默认情况下：

- **STT** 优先使用 DashScope
- **总结** 可在前端下拉框中切换模型
- 如果某个提供商不可用，会自动尝试回退到其它可用链路

---

## 💾 持久化数据

后端运行过程中产生的数据都在：

```text
backend/data/
```

其中最重要的是：

- `backend/data/chat-history.json`：聊天记录与 AI 总结
- `backend/data/keys/`：ASP.NET Data Protection keys

这意味着：

- 刷新网页不会丢历史
- 重启 Docker 容器也不会丢历史

---

## 🧩 常用命令

### 启动
```bash
cd backend
docker compose up -d --build
```

### 查看日志
```bash
cd backend
docker compose logs -f tsai
```

### 重启
```bash
cd backend
docker compose restart tsai
```

### 停止
```bash
cd backend
docker compose down
```


---

## 📄 License

See `LICENSE`.
