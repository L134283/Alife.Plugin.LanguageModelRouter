using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

[Module("灵枢 - 实时切换语言模型/报错自动切换容灾",
    "替换框架内置 OpenAI 语言模型，支持动态数量的渠道组（可自由增删）。一句话让 AI 切换渠道，遭遇 429/5xx 自动容灾重试，可探测可用模型列表。",
    editorUI: typeof(LanguageModelRouterUI),
    defaultCategory: "Doro的妙妙工具")]
public class LanguageModelRouter(
    ILogger<LanguageModelRouter> logger
) : ChatBehaviour, ILanguageModel, IConfigurable<LanguageModelRouterConfig>
{
    /// <summary>思维链内容前缀（与官方 OpenAI 兼容处理器一致，ChatStreamingAsync 据此分流 thinking）</summary>
    public const string ThinkContentPrefix = "__THINK__";

    public LanguageModelRouterConfig? Configuration { get; set; }

    /// <summary>下一次请求模拟 429 容灾（一次性，自动复位）</summary>
    internal volatile bool TestForceFailover = false;

    /// <summary>渠道切换通知（静态委托，UI 订阅后刷新显示）</summary>
    internal static Action? OnGroupChanged;

    /// <summary>交互器：Prompt / Poke / ChatAsync。因模块在 ChatBot 构造期间被创建，必须在 OnAwake 惰性获取</summary>
    IInteractor<LanguageModelRouter>? interactor;

    /// <summary>Xml 函数执行器（同样在 OnAwake 惰性获取，避免构造期循环依赖）</summary>
    XmlFunctionCaller? functionService;

    /// <summary>本模块注册的 XmlHandler（热重载销毁时必须注销，避免旧 handler 在表中累积导致函数被重复执行）</summary>
    XmlHandler? registeredHandler;

    /// <summary>HTTP 管道（含容灾/推理转换/按组重写），首次对话前按需构建</summary>
    HttpClient? httpClient;

    protected override async Task OnAwake()
    {
        // 模块构造发生在 ChatBot 构造期间，凡间接依赖 ChatBot 的服务都需在 Awake 后经容器获取
        interactor = (IInteractor<LanguageModelRouter>)await ChatActivity.Container.RequireInstance(typeof(IInteractor<LanguageModelRouter>));
        functionService = (XmlFunctionCaller)await ChatActivity.Container.RequireInstance(typeof(XmlFunctionCaller));

        var handler = new XmlHandler(this)
        {
            Description = "此服务管理语言模型的渠道切换，支持在多个 API 渠道之间手动切换。",
        };
        functionService.RegisterHandler(handler);
        registeredHandler = handler;

        try
        {
            EnsureHttpClient();
        }
        catch (Exception ex)
        {
            // 未配置渠道组时延后到首次对话再提示，避免插件面板无法打开
            Console.WriteLine($"[灵枢] 初始化跳过：{ex.Message}");
        }

        var cfg = Configuration;
        var groups = cfg != null ? BuildFallbackGroups(cfg) : new List<FallbackGroup>();
        var sb = new StringBuilder();

        sb.AppendLine("## 语言模型渠道切换能力");
        sb.AppendLine("你支持多组语言模型渠道，可以在不同 API 渠道之间切换。");
        sb.AppendLine("当用户要求「切换语言模型」「换大模型」「换到某某渠道」时，使用 SwitchModelGroup 工具切换。");

        if (groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("当前已配置的渠道组（可通过编号或名称切换）：");
            for (int i = 0; i < groups.Count; i++)
            {
                int slot = groups[i].Slot;
                string name = GetGroupName(slot, cfg);
                string label = string.IsNullOrWhiteSpace(name) ? "" : $"（名称：{name}）";
                string main = i == 0 ? "（主组）" : "";
                sb.AppendLine($"- 第{slot + 1}组{main}{label}：{groups[i].Endpoint} → {groups[i].ModelId}");
            }
        }
        sb.AppendLine();

        interactor.Prompt(sb.ToString());
    }

    /// <summary>热重载/活动销毁时注销本模块注册的 XmlHandler，避免旧 handler 残留在函数表中（否则 AI 调用会被旧实例重复执行）</summary>
    protected override Task OnDestroy()
    {
        if (registeredHandler != null)
        {
            functionService?.UnregisterHandler(registeredHandler);
            registeredHandler = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>构建 HTTP 管道：SocketsHttpHandler → FallbackHandler → HttpClient（容灾、推理转换、按组重写均在管道内完成）</summary>
    void EnsureHttpClient()
    {
        if (httpClient != null) return;

        var config = Configuration;
        if (config == null) return;

        config.EnsureGroups();

        var groups = BuildFallbackGroups(config);
        if (groups.Count == 0)
            throw new Exception("灵枢：未配置任何渠道组，请在插件配置中填写至少一组 Endpoint / API Key");

        // 错误关键字 / 重试间隔 / 组列表均通过动态委托每次请求读取，UI 修改即刻生效
        SocketsHttpHandler handler = new()
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        FallbackHandler fallbackHandler = new(handler,
            () => BuildFallbackGroups(Configuration!),
            () => ParseErrorKeywords(Configuration?.ErrorKeywords),
            () => Configuration!.RetryDelayMs,
            getForcedGroupIndex: () => GetForcedDisplayIndex(Configuration!),
            getAutoFailoverEnabled: () => Configuration!.AutoFailoverEnabled,
            onFailover: idx =>
            {
                var currentGroups = BuildFallbackGroups(Configuration!);
                if (idx >= 0 && idx < currentGroups.Count)
                {
                    int slot = currentGroups[idx].Slot;
                    string label = GetGroupLabel(slot, Configuration);
                    Console.WriteLine($"[灵枢] 已容灾切换 → {label}");
                    if (!Configuration!.PriorityMainChannel)
                    {
                        Configuration!.ForcedGroupIndex = slot;
                        interactor?.Poke($"灵枢已触发容灾，请告知用户，当前切换到了{label}");
                        OnGroupChanged?.Invoke();
                    }
                }
            },
            consumeTestFlag: () =>
            {
                if (!TestForceFailover) return false;
                TestForceFailover = false;
                return true;
            },
            getShowThinkingChain: () => Configuration!.ShowThinkingChain);

        httpClient = new HttpClient(fallbackHandler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        Console.WriteLine($"[灵枢] 已就绪，主渠道：{GetGroupLabel(groups[0].Slot, config)}");
    }

    /// <summary>对话入口：组装 OpenAI 兼容请求体，经 FallbackHandler 管道发送，逐帧解析 SSE 输出（参考官方 OpenAIVisionModel 的裸 HttpClient 写法）</summary>
    public async Task<string> ChatStreamingAsync(
        ChatHistoryAgentThread chatHistoryAgentThread,
        Action<string>? textReceived = null,
        Action<string>? thinkReceived = null,
        Action<TokenUsage>? tokenUsed = null,
        Action<Exception>? exceptionThrow = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder nonThinkingContent = new(); // 不含思考过程的最终回复

        try
        {
            EnsureHttpClient();
            if (httpClient == null)
                throw new Exception("灵枢：语言模型未初始化，请检查插件配置（至少配置一组 Endpoint / API Key）");

            var groups = BuildFallbackGroups(Configuration!);
            if (groups.Count == 0)
                throw new Exception("灵枢：未配置任何渠道组，请检查插件配置");
            FallbackGroup primary = groups[0];

            // 组装 OpenAI 兼容请求体；FallbackHandler 在管道内按当前组重写 model / reasoning_effort / extraBody 与目标地址
            var messages = new JsonArray();
            foreach (var msg in chatHistoryAgentThread.ChatHistory)
            {
                string role = msg.Role == AuthorRole.System ? "system"
                    : msg.Role == AuthorRole.Assistant ? "assistant"
                    : msg.Role == AuthorRole.Tool ? "tool"
                    : "user";
                string? content = msg.Content;
                // 上一轮思考块可能带 __THINK__ 前缀，发送前清理，避免污染上下文
                if (content != null && content.StartsWith(ThinkContentPrefix))
                    content = content.Substring(ThinkContentPrefix.Length);

                var entry = new JsonObject
                {
                    ["role"] = role,
                    ["content"] = content ?? ""
                };
                messages.Add(entry);
            }

            var requestBody = new JsonObject
            {
                ["model"] = primary.ModelId,
                ["messages"] = messages,
                ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true }
            };

            string baseUrl = primary.Endpoint.AbsoluteUri.TrimEnd('/');
            if (!baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                baseUrl += "/chat/completions";

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", primary.ApiKey);
            request.Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"灵枢：HTTP {(int)response.StatusCode} {errorBody}");
            }

            // 正常走 SSE 流式解析；个别渠道忽略 stream 参数返回完整 JSON 时走兼容解析
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                await ReadSseAsync(response, nonThinkingContent, textReceived, thinkReceived, tokenUsed, cancellationToken);
            else
                await ReadJsonAsync(response, nonThinkingContent, textReceived, thinkReceived, tokenUsed, cancellationToken);
        }
        catch (Exception e)
        {
            exceptionThrow?.Invoke(e);
        }

        // 思考内容只走事件流，写回历史时仅保留纯文本，避免 __THINK__ 前缀污染后续上下文
        string aiMessage = nonThinkingContent.ToString();
        if (chatHistoryAgentThread.ChatHistory.Count > 0)
        {
            ChatMessageContent lastMsg = chatHistoryAgentThread.ChatHistory[^1];
            if (lastMsg.Role == AuthorRole.Assistant && (lastMsg.Content?.Contains(ThinkContentPrefix) ?? false))
                lastMsg.Content = aiMessage;
        }

        return aiMessage;
    }

    /// <summary>逐行读取 SSE 流并解析内容/思考/用量（FallbackHandler 已把 reasoning 字段转为带前缀的 content）</summary>
    static async Task ReadSseAsync(
        HttpResponseMessage response,
        StringBuilder nonThinkingContent,
        Action<string>? textReceived,
        Action<string>? thinkReceived,
        Action<TokenUsage>? tokenUsed,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            string payload = line.Substring(5).Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            JsonNode? node;
            try { node = JsonNode.Parse(payload); } catch { continue; }
            if (node == null) continue;

            HandleChunk(node, nonThinkingContent, textReceived, thinkReceived, tokenUsed);
        }
    }

    /// <summary>个别渠道忽略 stream 参数时返回完整 JSON，做兼容解析</summary>
    static async Task ReadJsonAsync(
        HttpResponseMessage response,
        StringBuilder nonThinkingContent,
        Action<string>? textReceived,
        Action<string>? thinkReceived,
        Action<TokenUsage>? tokenUsed,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch { node = null; }
        if (node == null) return;

        HandleChunk(node, nonThinkingContent, textReceived, thinkReceived, tokenUsed, isJsonResponse: true);
    }

    /// <summary>解析单个数据块：content / reasoning（带前缀）/ usage</summary>
    static void HandleChunk(
        JsonNode node,
        StringBuilder nonThinkingContent,
        Action<string>? textReceived,
        Action<string>? thinkReceived,
        Action<TokenUsage>? tokenUsed,
        bool isJsonResponse = false)
    {
        // 非流式响应内容在 choices[0].message.content，流式在 choices[0].delta.content
        // choices 可能为空数组（完成帧/异常帧），直接索引 [0] 会越界，需先判空
        JsonNode? message = null;
        if (node["choices"] is JsonArray choices && choices.Count > 0)
            message = isJsonResponse ? choices[0]?["message"] : choices[0]?["delta"];
        string? content = message?["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content))
        {
            if (content.StartsWith(ThinkContentPrefix))
            {
                string reasoningPart = content.Substring(ThinkContentPrefix.Length);
                if (reasoningPart.Length > 0)
                    thinkReceived?.Invoke(reasoningPart);
            }
            else
            {
                nonThinkingContent.Append(content);
                textReceived?.Invoke(content);
            }
        }

        // Token 统计（stream_options.include_usage 时末帧携带 usage）
        JsonNode? usage = node["usage"];
        if (usage is JsonObject usageObj)
        {
            TokenUsage tokenUsage = new()
            {
                Input = usageObj["prompt_tokens"]?.GetValue<int>() ?? 0,
                Output = usageObj["completion_tokens"]?.GetValue<int>() ?? 0,
                Total = usageObj["total_tokens"]?.GetValue<int>() ?? 0,
                Cached = usageObj["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? 0
            };
            if (tokenUsage.Total > 0 || tokenUsage.Input > 0 || tokenUsage.Output > 0)
                tokenUsed?.Invoke(tokenUsage);
        }
    }

    /// <summary>主组索引（始终为 0，列表第一项即主组）</summary>
    internal static int GetPrimarySlot(LanguageModelRouterConfig cfg)
        => 0;

    /// <summary>获取显示顺序（0..Groups.Count-1，拷贝）</summary>
    internal static int[] GetGroupOrder(LanguageModelRouterConfig cfg)
    {
        cfg?.EnsureGroups();
        return cfg == null ? Array.Empty<int>() : Enumerable.Range(0, cfg.Groups.Count).ToArray();
    }

    /// <summary>FallbackHandler 起始位置：强制锁定渠道在组列表中的显示索引；-1 表示从主组开始</summary>
    static int GetForcedDisplayIndex(LanguageModelRouterConfig config)
    {
        if (config.ForcedGroupIndex < 0) return -1;
        var groups = BuildFallbackGroups(config);
        return groups.FindIndex(g => g.Slot == config.ForcedGroupIndex);
    }


    [XmlFunction(FunctionMode.OneShot)]
    [Description("模拟一次容灾测试：下次对话时将自动触发假 429 错误，验证容灾切换是否正常工作。使用后自动复位。")]
    public Task SimulateFailoverTest()
    {
        if (!Configuration!.AutoFailoverEnabled)
        {
            interactor?.Poke("自动容灾当前已关闭，无法测试。请在容灾设置中开启后重试。");
            return Task.CompletedTask;
        }

        var groups = BuildFallbackGroups(Configuration!);
        if (groups.Count < 2)
        {
            interactor?.Poke($"当前仅配置了 {groups.Count} 组渠道，没有备用渠道可切换。请至少配置两组后再测试。");
            return Task.CompletedTask;
        }

        TestForceFailover = true;
        interactor?.Poke("容灾测试已启动，请发送下一条消息，系统将模拟第 1 组返回 429 并自动切换到备用渠道。");
        return Task.CompletedTask;
    }

    /// <summary>测试一组连接，返回可用模型列表（index 为 Groups 索引）</summary>
    internal static async Task<(string Display, List<string>? Models)> FetchModels(int index, LanguageModelRouterConfig cfg)
    {
        if (cfg == null) return ("配置为空", null);

        cfg.EnsureGroups();
        if (index < 0 || index >= cfg.Groups.Count)
            return ($"第 {index + 1} 组不存在", null);
        var ch = cfg.Groups[index];
        if (!ch.IsConfigured)
            return ($"第 {index + 1} 组未配置", null);

        string label = GetGroupLabel(index, cfg);
        string ep = ch.Endpoint.TrimEnd('/');
        if (!ep.Contains("/models"))
            ep += "/models";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new HttpRequestMessage(HttpMethod.Get, ep);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ch.ApiKey);
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return ($"{label}：连接失败（HTTP {(int)resp.StatusCode}）", null);

            using var doc = JsonDocument.Parse(body);
            var models = doc.RootElement.TryGetProperty("data", out var data)
                ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : "?").ToList()
                : new List<string?>();

            if (models.Count == 0)
                return ($"{label}：连接成功，但未获取到模型列表", null);

            return ($"{label}（{models.Count} 个模型）：{string.Join("、", models.Take(10))}{(models.Count > 10 ? "…" : "")}",
                models!);
        }
        catch (Exception ex)
        {
            return ($"{label}：连接异常（{ex.Message}）", null);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检查所有已配置的语言模型渠道组的连接状态和可用模型列表。当用户要求测试连接、检查有哪些模型可用、或网络不通时调用。")]
    public async Task DetectModelGroup()
    {
        var cfg = Configuration;
        if (cfg == null) { interactor?.Poke("配置为空，请先填写配置"); return; }

        cfg.EnsureGroups();
        var results = new List<string>();
        for (int i = 0; i < cfg.Groups.Count; i++)
        {
            var (display, _) = await FetchModels(i, cfg);
            results.Add(display);
        }

        interactor?.Poke(string.Join("\n", results));
    }

    /// <summary>一句话切换渠道（AI 通过此工具调用）：支持组编号（"切换到第2组"/"2"）或组名称（"切换到deepseek"），也可切回自动容灾</summary>
    [XmlFunction(FunctionMode.OneShot)]
    [Description("将语言模型渠道切换到指定组。支持按组编号（如\"切换到第2组\"）或按组名称（如\"切换到deepseek\"）；说\"切回自动\"则恢复自动容灾模式。调用成功后立即生效。")]
    public Task SwitchModelGroup([Description("目标渠道：组编号（1~N）或组名称（支持部分匹配），或\"自动\"恢复自动容灾")] string target)
    {
        if (Configuration == null)
        {
            interactor?.Poke("灵枢：配置为空，无法切换渠道。");
            return Task.CompletedTask;
        }

        Configuration.EnsureGroups();
        if (Configuration.Groups.Count == 0)
        {
            interactor?.Poke("灵枢：当前没有任何渠道组，无法切换。");
            return Task.CompletedTask;
        }

        string raw = (target ?? "").Trim();

        // 切回自动容灾
        if (raw.Contains("自动", StringComparison.Ordinal)
            || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.ForcedGroupIndex = -1;
            interactor?.Poke("已切换回自动容灾模式（主组优先）。");
            OnGroupChanged?.Invoke();
            return Task.CompletedTask;
        }

        // 1) 按编号："2" / "第2组" / "2组"
        if (TryParseGroupNumber(raw, out int oneBased))
        {
            if (oneBased >= 1 && oneBased <= Configuration.Groups.Count)
            {
                int slot = oneBased - 1;
                if (Configuration.Groups[slot].IsConfigured)
                {
                    DoSwitch(slot);
                    return Task.CompletedTask;
                }
                interactor?.Poke($"灵枢：第 {oneBased} 组未配置，无法切换。");
                return Task.CompletedTask;
            }
            interactor?.Poke($"灵枢：组编号 {oneBased} 超出范围（当前共 {Configuration.Groups.Count} 组）。");
            return Task.CompletedTask;
        }

        // 2) 按名称匹配（先组名包含关键字，再关键字包含组名，忽略大小写）
        if (!string.IsNullOrEmpty(raw))
        {
            var matched = Configuration.Groups
                .Select((ch, i) => (ch, i))
                .Where(x => !string.IsNullOrWhiteSpace(x.ch.GroupName)
                    && x.ch.GroupName.Contains(raw, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count == 0)
            {
                matched = Configuration.Groups
                    .Select((ch, i) => (ch, i))
                    .Where(x => !string.IsNullOrWhiteSpace(x.ch.GroupName)
                        && raw.Contains(x.ch.GroupName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (matched.Count == 1)
            {
                int slot = matched[0].i;
                if (Configuration.Groups[slot].IsConfigured)
                {
                    DoSwitch(slot);
                    return Task.CompletedTask;
                }
                interactor?.Poke($"灵枢：组「{Configuration.Groups[slot].GroupName}」未配置，无法切换。");
                return Task.CompletedTask;
            }
            if (matched.Count > 1)
            {
                interactor?.Poke($"灵枢：名称「{raw}」匹配到多组：{string.Join("、", matched.Select(x => x.ch.GroupName))}，请使用完整名称或组编号。");
                return Task.CompletedTask;
            }
            interactor?.Poke($"灵枢：未找到名称为「{raw}」的渠道组。当前已配置：{ListConfiguredGroups()}。");
            return Task.CompletedTask;
        }

        interactor?.Poke("灵枢：请提供目标组编号（如 2）或组名称（如 deepseek）。");
        return Task.CompletedTask;
    }

    /// <summary>执行切换：写入强制锁定索引并通知 UI/用户</summary>
    void DoSwitch(int slot)
    {
        Configuration!.ForcedGroupIndex = slot;
        string label = GetGroupLabel(slot, Configuration);
        Console.WriteLine($"[灵枢] 已切换 → {label}");
        interactor?.Poke($"已切换到{label}");
        OnGroupChanged?.Invoke();
    }

    /// <summary>解析组编号："2"、"第2组"、"2组" 均解析为 2</summary>
    static bool TryParseGroupNumber(string raw, out int oneBased)
    {
        oneBased = 0;
        if (int.TryParse(raw, out oneBased))
            return oneBased >= 1;
        var m = Regex.Match(raw, @"^第?\s*(\d+)\s*组?$");
        return m.Success && int.TryParse(m.Groups[1].Value, out oneBased) && oneBased >= 1;
    }

    /// <summary>列出所有已配置组（编号+名称），供提示用户</summary>
    string ListConfiguredGroups()
    {
        return string.Join("、", Configuration!.Groups
            .Select((ch, i) => (ch, i))
            .Where(x => x.ch.IsConfigured)
            .Select(x => $"第{x.i + 1}组{(string.IsNullOrWhiteSpace(x.ch.GroupName) ? "" : $"（{x.ch.GroupName}）")}"));
    }

    internal static string GetGroupLabel(int idx, LanguageModelRouterConfig? cfg)
    {
        string name = GetGroupName(idx, cfg);
        string base_ = $"第 {idx + 1} 组";
        return string.IsNullOrWhiteSpace(name) ? base_ : $"{base_}（{name}）";
    }

    internal static string GetGroupName(int idx, LanguageModelRouterConfig? cfg)
    {
        if (cfg == null) return "";
        cfg.EnsureGroups();
        if (idx < 0 || idx >= cfg.Groups.Count) return "";
        return cfg.Groups[idx].GroupName ?? "";
    }

    /// <summary>按 Groups 顺序构建渠道组列表（仅含已配置组），第一项即主组</summary>
    static List<FallbackGroup> BuildFallbackGroups(LanguageModelRouterConfig config)
    {
        var groups = new List<FallbackGroup>();
        config.EnsureGroups();

        for (int i = 0; i < config.Groups.Count; i++)
        {
            var ch = config.Groups[i];
            if (!ch.IsConfigured) continue;

            groups.Add(new FallbackGroup(
                i,
                new Uri(ch.Endpoint),
                ch.ModelId ?? "",
                ch.ApiKey,
                ParseExtraHeaders(ch.ExtraHeaders),
                ch.ReasoningEffort,
                ParseExtraBody(ch.ExtraBody)));
        }

        return groups;
    }

    static Dictionary<string, string>? ParseExtraHeaders(string? extraHeaders)
    {
        if (string.IsNullOrWhiteSpace(extraHeaders)) return null;
        try
        {
            var dict = new Dictionary<string, string>();
            using var doc = JsonDocument.Parse(extraHeaders!);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(prop.Value.GetString()))
                    dict[prop.Name] = prop.Value.GetString()!;
            }
            return dict.Count > 0 ? dict : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[灵枢] ExtraHeaders 解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>解析 Extra Body JSON 为字典（失败返回 null）</summary>
    static Dictionary<string, object?>? ParseExtraBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return null;
        try
        {
            var bodyObj = new Dictionary<string, object?>();
            using var doc = JsonDocument.Parse(bodyJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                bodyObj[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.Clone()
                };
            }
            return bodyObj.Count > 0 ? bodyObj : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[灵枢] ExtraBody 解析失败: {ex.Message}");
            return null;
        }
    }


    static List<string> ParseErrorKeywords(string? keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return new List<string>();

        return keywords!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
