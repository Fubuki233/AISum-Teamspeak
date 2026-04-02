# ts_voice_forwarder — TS3 插件：语音 PCM 转发

## 功能

捕获 **TeamSpeak 3 客户端**中所有用户的解码后 PCM 语音数据，通过 UDP 转发给 `TsAi` 服务进行 AI 语音识别。

与 SDK 服务器方案不同，本插件运行在 **TS3 客户端进程内部**，直接拦截 `onEditPlaybackVoiceDataEvent` 回调，因此与任何标准 TS3 服务器完全兼容。

---

## UDP 数据包格式（与 SDK 服务器保持一致，VoiceReceiver.cs 无需改动）

| 字节偏移 | 长度 | 类型       | 说明            |
|---------|------|-----------|----------------|
| 0       | 2    | uint16 LE | 说话者 clientID |
| 2       | 4    | uint32 LE | 采样率 = 48000  |
| 6       | 4    | int32  LE | PCM 字节数      |
| 10      | N    | int16  LE | PCM 原始数据    |

---

## 构建（WSL2/Linux → Windows x64 DLL）

```bash
cd /home/zyh/ts_sdk_3.3.1/ts_plugin

# 一键编译
bash build.sh

# 编译 + 自动部署到 TS3 插件目录
bash build.sh --deploy

# 编译 + 部署到自定义路径
bash build.sh --deploy --ts3-dir "/mnt/c/Users/Pc/AppData/Roaming/TS3Client/plugins"
```

输出文件：`out/ts_voice_forwarder.dll`

---

## 安装步骤

### 1. 复制 DLL

将 `out/ts_voice_forwarder.dll` 复制到 TS3 插件目录：

```
C:\Users\<你的用户名>\AppData\Roaming\TS3Client\plugins\
```

或直接用 `build.sh --deploy` 自动部署。

### 2. 创建配置文件

将 `ts_voice_forwarder.cfg.example` 重命名为 `ts_voice_forwarder.cfg`，放到同一个插件目录，修改 `HOST` 为你当前的 WSL2 IP：

```ini
HOST=172.17.52.58
PORT=9988
```

查看当前 WSL2 IP（Windows cmd）：
```cmd
wsl hostname -I
```

> 提示：如果你已启用 `.wslconfig` 的 `networkingMode=mirrored`，可以用 `HOST=127.0.0.1`

### 3. 在 TS3 中启用插件

重启 TS3 → Plugins → **ts_voice_forwarder** → 打勾启用

TS3 的 TS3 Client Log（F12）应该出现：
```
TS Voice Forwarder: started, forwarding to UDP target
```

---

## 配置优先级

1. `%APPDATA%\TS3Client\plugins\ts_voice_forwarder.cfg`（推荐）
2. 环境变量 `TS_VOICE_FWD_HOST` / `TS_VOICE_FWD_PORT`
3. 默认值 `127.0.0.1:9988`

---

## 文件结构

```
ts_plugin/
├── plugin.c                     # 插件源码
├── build.sh                     # 构建脚本
├── ts_voice_forwarder.cfg.example  # 配置文件模板
├── include/                     # Plugin SDK 头文件
│   ├── ts3_functions.h
│   ├── plugin_definitions.h
│   └── teamspeak/
│       ├── public_definitions.h
│       └── ...
├── cmake/                       # CMake 备用构建
│   ├── CMakeLists.txt
│   └── toolchain-mingw64.cmake
└── out/                         # 编译输出
    └── ts_voice_forwarder.dll
```
