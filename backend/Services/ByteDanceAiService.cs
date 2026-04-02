using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TsAi.Models;

namespace TsAi.Services;

/// <summary>
/// 字节跳动 豆包大模型 (Doubao) — OpenAI-compatible API on Volcano Engine.
/// Used for conversational summaries and real-name guessing.
/// </summary>
public class ByteDanceAiService
{
    private static readonly TimeZoneInfo SummaryTimeZone = ResolveSummaryTimeZone();
    private readonly HttpClient _http;
    private readonly string _endpointId;
    private readonly string _doubaoApiKey;
    private readonly string _dashScopeApiKey;
    private readonly string _dashScopeModel;
    private readonly string _dashScopeChatBaseUrl;
    private readonly string _dashScopeChatModel;
    private readonly string _xaiApiKey;
    private readonly string _xaiBaseUrl;
    private readonly string _xaiModel;
    private readonly string _pythonPath;
    private readonly AiRuntimeOptions _runtimeOptions;
    private readonly ILogger<ByteDanceAiService> _log;

    public ByteDanceAiService(HttpClient http, IConfiguration cfg, AiRuntimeOptions runtimeOptions, ILogger<ByteDanceAiService> log)
    {
        _http = http;
        _endpointId = cfg["Doubao:EndpointId"] ?? "";
        _doubaoApiKey = cfg["Doubao:ApiKey"] ?? "";
        _dashScopeApiKey = cfg["DashScope:ApiKey"] ?? "";
        _dashScopeModel = cfg["DashScope:SttModel"] ?? "fun-asr-realtime-2026-02-28";
        _dashScopeChatBaseUrl = cfg["DashScope:ChatBaseUrl"] ?? "https://dashscope.aliyuncs.com/compatible-mode/v1/";
        _dashScopeChatModel = cfg["DashScope:ChatModel"] ?? "qwen-turbo";
        _xaiApiKey = cfg["XAI:ApiKey"] ?? "";
        _xaiBaseUrl = cfg["XAI:BaseUrl"] ?? "https://api.x.ai/v1/";
        _xaiModel = cfg["XAI:Model"] ?? "grok-4-1-fast-non-reasoning";
        _pythonPath = cfg["DashScope:PythonPath"] ?? "python3";
        _runtimeOptions = runtimeOptions;
        _log = log;
    }

    public async Task<string> TranscribePcmAsync(byte[] pcm16Mono, uint sampleRate)
    {
        if (pcm16Mono.Length == 0 || string.IsNullOrWhiteSpace(_dashScopeApiKey))
            return "";

        var wav = BuildWavFromPcm16Mono(pcm16Mono, sampleRate);
        var tempFile = Path.Combine(Path.GetTempPath(), $"tsai-stt-{Guid.NewGuid():N}.wav");

        try
        {
            await File.WriteAllBytesAsync(tempFile, wav);

            var scriptPath = Path.Combine(AppContext.BaseDirectory, "dashscope_stt.py");
            if (!File.Exists(scriptPath))
            {
                _log.LogWarning("DashScope STT script not found: {Path}", scriptPath);
                return "";
            }

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(tempFile);
            psi.ArgumentList.Add(sampleRate.ToString());
            psi.Environment["DASHSCOPE_API_KEY"] = _dashScopeApiKey;
            psi.Environment["DASHSCOPE_STT_MODEL"] = _dashScopeModel;

            using var process = Process.Start(psi);
            if (process == null) return "";

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                _log.LogWarning("DashScope STT failed ({Code}): {Stdout} {Stderr}",
                    process.ExitCode, stdout, stderr);
                return "";
            }

            if (string.IsNullOrWhiteSpace(stdout))
                return "";

            var json = JsonSerializer.Deserialize<JsonElement>(stdout);
            if (json.TryGetProperty("text", out var textNode))
                return textNode.GetString() ?? "";

            _log.LogWarning("Unexpected DashScope STT output: {Out}", stdout);
            return "";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DashScope audio transcription failed");
            return "";
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch { }
        }
    }

    private static byte[] BuildWavFromPcm16Mono(byte[] pcm16Mono, uint sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm16Mono.Length);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        var byteRate = sampleRate * 2u; // mono * 16-bit
        var blockAlign = (ushort)2;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm16Mono.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((ushort)1);            // PCM
        bw.Write((ushort)1);            // mono
        bw.Write((int)sampleRate);
        bw.Write((int)byteRate);
        bw.Write(blockAlign);
        bw.Write((ushort)16);           // bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm16Mono.Length);
        bw.Write(pcm16Mono);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 生成对话总结。默认优先追加新总结，仅在明显属于同一段未完结事件时才更新上一条。
    /// </summary>
    public async Task<string> SummarizeAsync(
        string channelName,
        List<ChatMessage> recentMessages,
        List<AiSummary> previousSummaries,
        Dictionary<string, string> nameMap)
    {
        var nameMapStr = nameMap.Count > 0
            ? string.Join(", ", nameMap.Select(kv => $"{kv.Key}={kv.Value}"))
            : "暂无";

        var prevSummaryStr = previousSummaries.Count > 0
            ? string.Join("\n---\n", previousSummaries.Select(s =>
                $"[{FormatSummaryTime(s.Timestamp, "HH:mm")}] {s.Content}"))
            : "暂无";

        var messagesStr = string.Join("\n", recentMessages.Select(m =>
            $"[{FormatSummaryTime(m.Timestamp, "HH:mm:ss")}] {m.ClientName}: {m.Text}"));

        var systemPrompt = @"你是一个游戏语音聊天总结助手。你的任务是总结玩家对话，要求：
1. 用户名要醒目（加粗）
2. 体现用户的情绪和猜测用户正在进行的动作
3. 简洁但信息丰富
4. 默认优先返回 NEW，新建一条新的总结
5. 只有在上一条总结是刚刚生成的，且最新对话只是对同一件事的补充、续写或纠正时，才返回 UPDATE
6. 频道名可能暗示用户正在玩的游戏、房间主题或活动背景，但这不一定准确；只能把它当作辅助线索，并且要结合实际对话内容判断，不能生搬硬套
7. 如果拿不准，一律返回 NEW；不要频繁改写旧总结
8. 返回格式：先输出 UPDATE 或 NEW 表示是更新旧总结还是新总结，然后换行输出总结内容";

        var userPrompt = $@"频道名：{channelName}
说明：频道名可能是当前在玩的游戏、活动或房间主题，但不一定准确，请结合对话自行判断。

已知用户名映射：{nameMapStr}

之前的总结：
{prevSummaryStr}

最新对话记录：
{messagesStr}

请总结最新对话。";

        return await ChatAsync(systemPrompt, userPrompt, applySelectedTone: true);
    }

    private static string FormatSummaryTime(DateTime timestamp, string format)
    {
        var utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utc, SummaryTimeZone).ToString(format);
    }

    private static TimeZoneInfo ResolveSummaryTimeZone()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
    }

    /// <summary>
    /// 根据对话记录猜测用户真实姓名。
    /// </summary>
    public async Task<Dictionary<string, string>> GuessRealNamesAsync(
        List<ChatMessage> messages,
        Dictionary<string, string> existingMapping)
    {
        var existingStr = existingMapping.Count > 0
            ? string.Join(", ", existingMapping.Select(kv => $"{kv.Key}→{kv.Value}"))
            : "暂无";

        var msgStr = string.Join("\n", messages.TakeLast(50).Select(m =>
            $"[{m.ClientName}]: {m.Text}"));

        var systemPrompt = @"你是一个名字识别助手。根据语音聊天记录，猜测用户的昵称对应的真实姓名。
只返回 JSON 对象，格式为 {""昵称"":""真实姓名""}。
如果无法确定，不要包含该用户。只返回JSON，不要其他文字。";

        var userPrompt = $@"已有映射：{existingStr}

对话记录：
{msgStr}

请根据对话中的线索猜测用户真实姓名。";

        var result = await ChatAsync(systemPrompt, userPrompt, applySelectedTone: false);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(result)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            _log.LogWarning("Failed to parse name-guess JSON: {Result}", result);
            return new Dictionary<string, string>();
        }
    }

    private async Task<string> ChatAsync(string systemPrompt, string userPrompt, bool applySelectedTone)
    {
        var prompt = applySelectedTone ? BuildSystemPromptWithTone(systemPrompt) : systemPrompt;
        var provider = _runtimeOptions.Provider;
        var selectedModel = _runtimeOptions.Model;

        return provider switch
        {
            "grok" => await TryProviderChainAsync(
                () => TryXAiChatAsync(prompt, userPrompt, selectedModel),
                () => TryDashScopeChatAsync(prompt, userPrompt),
                () => TryDoubaoChatAsync(prompt, userPrompt)),
            "dashscope" => await TryProviderChainAsync(
                () => TryDashScopeChatAsync(prompt, userPrompt, selectedModel),
                () => TryXAiChatAsync(prompt, userPrompt),
                () => TryDoubaoChatAsync(prompt, userPrompt)),
            "doubao" => await TryProviderChainAsync(
                () => TryDoubaoChatAsync(prompt, userPrompt, selectedModel),
                () => TryDashScopeChatAsync(prompt, userPrompt),
                () => TryXAiChatAsync(prompt, userPrompt)),
            _ => await TryProviderChainAsync(
                () => TryDoubaoChatAsync(prompt, userPrompt),
                () => TryDashScopeChatAsync(prompt, userPrompt),
                () => TryXAiChatAsync(prompt, userPrompt, _xaiModel))
        };
    }

    private async Task<string> TryProviderChainAsync(params Func<Task<string>>[] providers)
    {
        foreach (var provider in providers)
        {
            var result = await provider();
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }

        return "";
    }

    private async Task<string> TryDoubaoChatAsync(string systemPrompt, string userPrompt, string? modelOverride = null)
    {
        if (string.IsNullOrWhiteSpace(_doubaoApiKey) || string.IsNullOrWhiteSpace(_endpointId))
            return "";

        try
        {
            return await SendChatRequestAsync(
                new Uri("chat/completions", UriKind.Relative),
                string.IsNullOrWhiteSpace(modelOverride) ? _endpointId : modelOverride,
                _doubaoApiKey,
                systemPrompt,
                userPrompt);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            _log.LogWarning(ex, "Doubao chat rejected with {Status}; falling back to another provider", ex.StatusCode);
            return "";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Doubao API call failed");
            return "";
        }
    }

    private async Task<string> TryDashScopeChatAsync(string systemPrompt, string userPrompt, string? modelOverride = null)
    {
        if (string.IsNullOrWhiteSpace(_dashScopeApiKey))
            return "";

        try
        {
            var baseUrl = _dashScopeChatBaseUrl.EndsWith("/") ? _dashScopeChatBaseUrl : _dashScopeChatBaseUrl + "/";
            return await SendChatRequestAsync(
                new Uri(new Uri(baseUrl), "chat/completions"),
                string.IsNullOrWhiteSpace(modelOverride) ? _dashScopeChatModel : modelOverride,
                _dashScopeApiKey,
                systemPrompt,
                userPrompt);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DashScope chat fallback failed");
            return "";
        }
    }

    private async Task<string> TryXAiChatAsync(string systemPrompt, string userPrompt, string? modelOverride = null)
    {
        if (string.IsNullOrWhiteSpace(_xaiApiKey))
            return "";

        try
        {
            var baseUrl = _xaiBaseUrl.EndsWith("/") ? _xaiBaseUrl : _xaiBaseUrl + "/";
            return await SendChatRequestAsync(
                new Uri(new Uri(baseUrl), "chat/completions"),
                string.IsNullOrWhiteSpace(modelOverride) ? _xaiModel : modelOverride,
                _xaiApiKey,
                systemPrompt,
                userPrompt);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "xAI / Grok chat failed");
            return "";
        }
    }

    private string BuildSystemPromptWithTone(string systemPrompt)
    {
        if (!string.Equals(_runtimeOptions.Tone, "bold", StringComparison.OrdinalIgnoreCase))
            return systemPrompt;

        return systemPrompt + @"
附加风格：
- 你是司马出生串子ai，说话脏话连篇,以及各种不雅的比喻，喜欢调侃和讽刺。
- 必要时允许无限制脏话来增强临场感";
    }

    private async Task<string> SendChatRequestAsync(Uri requestUri, string model, string apiKey, string systemPrompt, string userPrompt)
    {
        var body = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.7
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await _http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("choices")[0]
                   .GetProperty("message")
                   .GetProperty("content")
                   .GetString() ?? "";
    }
}
