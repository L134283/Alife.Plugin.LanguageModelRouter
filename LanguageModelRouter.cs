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
using Alife.Function.TokenStatistics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

[Module("灵枢 - 实时切换语言模型/报错自动切换容灾",
    "替换框架内置语言模型，支持动态数量的渠道组（可自由增删）。一句话让 AI 切换渠道，遭遇 429/5xx 自动容灾重试，可探测可用模型列表。",
    editorUI: typeof(LanguageModelRouterUI),
    defaultCategory: "Doro的妙妙工具")]
public class LanguageModelRouter(
    ILogger<LanguageModelRouter> logger,
    ConfigurationSystem configurationSystem
) : ChatBehaviour,
    ILanguageModel,
    ITokenUser,
    IConfigurable<LanguageModelRouterConfig>,
    IMultimodalExecutor
{
    /// <summary>
    /// 旧版思维链内容前缀：历史版本曾把 reasoning 塞进 content 并在前面打上该标记。
    /// 5.x 起思考走 <see cref="ILanguageModel.ChatStreamingAsync"/> 的 thinkReceived 独立通道，
    /// 该常量仅用于清理旧对话历史中的残留前缀。
    /// </summary>
    public const string ThinkContentPrefix = "__THINK__";

    /// <summary>调用用量（对齐官方 OpenAILanguageModel）：每轮请求结束后发布一次，供 TokenStatistics 等模块订阅。</summary>
    public event Action<TokenUsageContext>? TokenUsed;

    public LanguageModelRouterConfig Configuration { get; set; } = null!;

    /// <summary>下一次请求模拟 429 容灾（一次性，自动复位）。</summary>
    internal volatile bool TestForceFailover;

    /// <summary>
    /// 最近一次成功服务的渠道组 slot（Groups 索引，-1=尚无请求）。
    /// 容灾、手动锁定、「优先主渠道」静默绕行下均为本轮实际命中的组；
    /// 供统计类插件（如 1chuxin.TokenStats）在 TokenUsed 时经 ChatBot.LanguageModel 反射读取，
    /// 做真实渠道归因。瞬态运行状态：随实例销毁复位，不写入配置文件。
    /// </summary>
    public int LastServedGroupIndex { get; private set; } = -1;

    /// <summary>渠道切换通知（静态委托，UI 订阅后刷新显示）。</summary>
    internal static Action? OnGroupChanged;

    /// <summary>思考请求记事本：XmlFunctionCaller/QChat/音频监听等框架模块通过 <see cref="GetThinkingRequester"/>
    /// 租用/归还"需要思考"的标记。智能思考切换开启后，本模块据此决定走思考还是非思考模式。</summary>
    readonly OccupationNotepad thinkingRequester = new();

    /// <summary>交互器：Prompt / Poke / ChatAsync。模块在 ChatBot 构造期间被创建，必须在 OnAwake 惰性获取。</summary>
    Interactor<LanguageModelRouter>? interactor;

    /// <summary>Xml 函数执行器（同样在 OnAwake 惰性获取，避免构造期循环依赖）。</summary>
    XmlFunctionCaller? functionService;

    /// <summary>本模块注册的 XmlHandler（热重载销毁时注销，避免旧 handler 残留在函数表中被重复执行）。</summary>
    XmlHandler? registeredHandler;

    /// <summary>多模态输入 XmlHandler（LoadImage/LoadVideo/LoadAudio）。</summary>
    XmlHandler? registeredMultimodalHandler;

    /// <summary>HTTP 管道：首次对话前按需构建（HTTP/1.1 强制，规避部分渠道流式 HttpIOException）。</summary>
    HttpClient? httpClient;

    readonly object httpClientLock = new();

    /// <summary>当前请求的最后一条用户消息（用于自定义关键词触发思考匹配）。请求由 ChatBot 串行分发，无需加锁。</summary>
    string? currentUserMessage;

    /// <summary>OnStart 是否已执行（框架在热重载后可能重复调用 AwakeAsync/UpdateAsync，避免重复注册函数）。</summary>
    bool initialized;

    /// <summary>内容安全检查类错误标记：部分渠道会对输入做安全审查（如阿里云百炼 data_inspection_failed、
    /// OpenAI content_filter），命中时即使 HTTP 4xx 也触发容灾——换个渠道往往能放行。</summary>
    static readonly string[] BuiltinFallbackMarkers =
    [
        "data_inspection_failed",       // 阿里云百炼：输入内容安全检查失败
        "content_filter",               // OpenAI / 中转渠道：内容过滤拒绝
        "content_policy_violation",     // 内容策略违规
        "inappropriate content",        // 输入内容不合规
        "unsafe_content",               // 不安全内容
        "sensitive_content",            // 敏感内容
        "moderation_failed",            // 内容审核失败
        "insufficient_quota",           // OpenAI：账户额度不足
        "exceeded your current quota",  // OpenAI 原文
        "billing_hard_limit_reached",   // 账单硬上限
        "积分不足",                      // 中转站：积分耗尽
        "余额不足",                      // 中转站：余额耗尽
        "额度不足"                       // 中转站：额度耗尽
    ];

    /// <summary>暴露思考请求记事本，供框架生态（XmlFunctionCaller 等）请求"本次应使用思考模式"。</summary>
    public OccupationNotepad GetThinkingRequester() => thinkingRequester;

    // ──── IMultimodalExecutor（原生多模态，对齐官方 OpenAI 语言模型）────

    /// <summary>
    /// 当前即将服务请求的渠道组是否开启了该内容类型对应的原生多模态
    /// （input_audio 看"音频输入"开关，其余看"原生多模态"总开关）。
    /// </summary>
    bool IMultimodalExecutor.CanUseContentType(string protocolTypeName) => IsCurrentGroupContentTypeEnabled(protocolTypeName);

    /// <summary>当前即将服务请求的渠道组是否开启了指定内容类型（= 容灾起始组）。</summary>
    internal bool IsCurrentGroupContentTypeEnabled(string protocolTypeName)
    {
        LanguageModelRouterConfig cfg = Configuration;
        if (cfg == null) return false;
        cfg.EnsureGroups();

        List<FallbackGroup> groups = BuildFallbackGroups(cfg);
        if (groups.Count == 0) return false;

        int startIndex = GetForcedDisplayIndex(cfg);
        if (startIndex < 0 || startIndex >= groups.Count)
            startIndex = 0;

        FallbackGroup group = groups[startIndex];
        return NativeMultimodalProtocol.IsAllowed(
            protocolTypeName, group.EnableNativeMultimodal, group.EnableAudioMultimodal);
    }

    // ──── 生命周期 ────

    protected override async Task OnAwake()
    {
        // 模块构造发生在 ChatBot 构造期间，凡间接依赖 ChatBot 的服务都需在 Awake 后经容器获取
        try
        {
            interactor = (Interactor<LanguageModelRouter>)await ChatActivity.Container
                .RequireInstance(typeof(Interactor<LanguageModelRouter>));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[灵枢] 获取交互器失败，AI 主动提示将不可用");
        }

        LanguageModelRouterConfig cfg = Configuration;
        if (cfg == null)
        {
            logger.LogError("[灵枢] 配置注入失败，跳过初始化");
            return;
        }
        cfg.EnsureGroups();

        try
        {
            EnsureHttpClient();
        }
        catch (Exception ex)
        {
            // 未配置渠道组时延后到首次对话再提示，避免插件面板无法打开
            Console.WriteLine($"[灵枢] 初始化跳过：{ex.Message}");
        }

        // 提示词只描述本插件能力，不依赖其它模块，放在 Awake 里尽早注入
        try
        {
            interactor?.Prompt(BuildPrompt(cfg));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[灵枢] 注入提示词失败");
        }
    }

    /// <summary>
    /// 注册 AI 可调用函数。刻意放在 OnStart（所有模块都已 Awake 完）而不是 OnAwake：
    /// ① 只取容器里"已经存在"的 XmlFunctionCaller，绝不在插件热重载的重建窗口里新建一个半成品调用器
    ///   （那样会把它变成函数表的另一份副本，导致桌宠的 Speak/Expression 等标签被判定为"环境中没有"）；
    /// ② 用户没启用 Xml 函数调用器时直接跳过——没有执行器，注册了也没有任何意义。
    /// </summary>
    protected override Task OnStart()
    {
        if (initialized)
            return Task.CompletedTask;
        initialized = true;

        LanguageModelRouterConfig cfg = Configuration;
        if (cfg == null)
            return Task.CompletedTask;
        cfg.EnsureGroups();

        functionService = ChatActivity.Container.Instances.OfType<XmlFunctionCaller>().FirstOrDefault();
        if (functionService == null)
        {
            Console.WriteLine("[灵枢] 未检测到 Xml函数调用器，跳过渠道切换函数注册（在角色配置中启用「Xml函数调用器」后重载本插件即可）");
            return Task.CompletedTask;
        }

        try
        {
            XmlHandler handler = new(this)
            {
                Description = "此服务管理语言模型的渠道切换，支持在多个 API 渠道之间手动切换。"
            };
            functionService.RegisterHandler(handler, DocumentMode.Explicit, DestroyCancellationToken);
            registeredHandler = handler;

            // 原生多模态：至少一组开启任意多模态时，注册 LoadImage/LoadVideo/LoadAudio 查看函数
            if (cfg.HasAnyMultimodalGroup())
            {
                XmlHandler multimodalHandler = AlifeContentRegistry.BuildHandler(ChatBot, cfg, this);
                if (multimodalHandler.Functions.Count > 0)
                {
                    functionService.RegisterHandler(multimodalHandler, DocumentMode.Explicit, DestroyCancellationToken);
                    registeredMultimodalHandler = multimodalHandler;
                }
            }
        }
        catch (Exception ex)
        {
            // 注册失败不应拖垮插件（面板与对话仍需可用）
            logger.LogError(ex, "[灵枢] 注册 AI 函数失败");
        }

        return Task.CompletedTask;
    }

    /// <summary>注销本模块注册的 XmlHandler 并释放 HTTP 连接池（对齐官方 OnDestroy 的 Dispose 行为）。</summary>
    protected override Task OnDestroy()
    {
        if (registeredHandler != null)
        {
            functionService?.UnregisterHandler(registeredHandler);
            registeredHandler = null;
        }
        if (registeredMultimodalHandler != null)
        {
            functionService?.UnregisterHandler(registeredMultimodalHandler);
            registeredMultimodalHandler = null;
        }

        try { httpClient?.Dispose(); } catch { }
        httpClient = null;
        return Task.CompletedTask;
    }

    // ──── 对话主流程（ILanguageModel）────

    /// <summary>
    /// 对话入口：逐组尝试（容灾）→ 组装 OpenAI 兼容请求 → 解析 SSE/JSON → 写回历史 → 发布用量。
    /// 与官方 OpenAILanguageModel 的契约完全一致（返回 ChatResult / 思考走 thinkReceived / 用量走 ITokenUser）。
    /// </summary>
    public async Task<ChatResult> ChatStreamingAsync(
        ChatHistoryAgentThread chatHistoryAgentThread,
        Action<string>? textReceived = null,
        Action<string>? thinkReceived = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder messageBuilder = new();
        StringBuilder thinkingBuilder = new();
        Exception? exception = null;
        TokenUsage tokenUsage = new();

        ChatHistory history = chatHistoryAgentThread.ChatHistory;
        string userMessage = OpenAIChatProtocol.GetUserMessage(history);
        currentUserMessage = OpenAIChatProtocol.GetLastUserMessage(history);


        try
        {
            await RequestAsync(
                history,
                messageBuilder,
                thinkingBuilder,
                textReceived,
                thinkReceived,
                usage => tokenUsage = usage,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动停止生成：保留已输出的部分内容，不当作错误上报
        }
        catch (ObjectDisposedException)
        {
            // 角色活动正在销毁（热重载/切换角色）：静默结束本轮，不上报为错误
        }
        catch (Exception e)
        {
            exception = e;
            logger.LogError(e, "[灵枢] 语言模型请求失败");
        }

        string aiMessage = messageBuilder.ToString();
        string aiThinking = thinkingBuilder.ToString();

        // 所有渠道都"成功"但没有任何正文：给出明确提示，避免用户面对空白回复
        if (exception == null
            && string.IsNullOrWhiteSpace(aiMessage)
            && string.IsNullOrWhiteSpace(aiThinking)
            && cancellationToken.IsCancellationRequested == false)
        {
            exception = new Exception("灵枢：渠道未返回任何内容，请检查渠道状态或切换到其他渠道组。");
        }

        WriteHistory(history, aiMessage);

        // 用量发布（对齐官方：无论成功/失败/取消都记一笔，用量可能为 0）
        TokenUsed?.Invoke(new TokenUsageContext {
            AIMessage = aiMessage,
            AIThinking = aiThinking,
            UserMessage = userMessage,
            TokenUsage = tokenUsage
        });

        return new ChatResult {
            AIMessage = aiMessage,
            AIThinking = aiThinking,
            Exception = exception
        };
    }

    /// <summary>
    /// 按容灾策略逐组尝试并把响应流写入 builder。
    /// 与旧版 FallbackHandler 管道相比：每个渠道组独立序列化整条消息（含该组的模型、温度、
    /// 思考参数、多模态能力），不再需要克隆请求 + 事后重写请求体，行为更可预测。
    /// </summary>
    async Task RequestAsync(
        ChatHistory history,
        StringBuilder messageBuilder,
        StringBuilder thinkingBuilder,
        Action<string>? textReceived,
        Action<string>? thinkReceived,
        Action<TokenUsage> usageReceived,
        CancellationToken cancellationToken)
    {
        LanguageModelRouterConfig cfg = Configuration;
        if (cfg == null)
            throw new Exception("灵枢：模块配置未注入，请重载此插件或重新激活角色");

        cfg.EnsureGroups();

        EnsureHttpClient();
        // 取一次本地引用：角色活动销毁时 OnDestroy 会把字段置空并 Dispose，
        // 用本地引用可避免并发拆除过程中读到 null 的竞态
        HttpClient? client = httpClient;
        if (client == null)
            throw new Exception("灵枢：语言模型未初始化，请检查插件配置（至少配置一组 Endpoint / API Key）");

        List<FallbackGroup> groups = BuildFallbackGroups(cfg);
        if (groups.Count == 0)
            throw new Exception("灵枢：未配置任何渠道组，请检查插件配置");

        int startIndex = GetForcedDisplayIndex(cfg);
        if (startIndex < 0 || startIndex >= groups.Count)
            startIndex = 0;

        int maxAttempts = cfg.AutoFailoverEnabled ? groups.Count : 1;
        HttpResponseMessage? successResponse = null;
        Exception? lastError = null;
        bool thinkingUsed = true;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int index = (startIndex + attempt) % groups.Count;
            FallbackGroup group = groups[index];
            bool thinking = ComputeThinkingMode(cfg, group.SmartThinkingEnabled);

            if (attempt > 0 && cfg.RetryDelayMs > 0)
                await Task.Delay(cfg.RetryDelayMs, cancellationToken);

            // 容灾自测：模拟第 1 组返回 429，验证切换链路（一次性）
            if (attempt == 0 && ConsumeFailoverTestFlag())
            {
                lastError = new Exception("模拟 429（容灾测试）");
                Console.WriteLine($"[灵枢] 第 {group.Slot + 1} 组 {group.Endpoint.Host} ✗ 模拟 429 → 容灾切换");
                continue;
            }

            using HttpRequestMessage request = BuildRequest(history, group, thinking);
            HttpResponseMessage? response;
            try
            {
                response = await SendAsync(client, request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 用户主动停止：直接向上抛，由主流程静默结束
            }
            catch (TimeoutException ex)
            {
                lastError = ex;
                Console.WriteLine($"[灵枢] 第 {group.Slot + 1} 组 {group.Endpoint.Host} ✗ 请求超时" +
                    (attempt < maxAttempts - 1 ? " → 容灾切换" : "，无备用渠道可切换"));
                continue;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                Console.WriteLine($"[灵枢] 第 {group.Slot + 1} 组 {group.Endpoint.Host} ✗ 网络错误：{ex.Message}" +
                    (attempt < maxAttempts - 1 ? " → 容灾切换" : "，无备用渠道可切换"));
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                successResponse = response;
                thinkingUsed = thinking;
                MarkServed(group, attempt > 0);
                break;
            }

            string errorBody = await SafeReadBodyAsync(response, cancellationToken);
            bool shouldFallback = cfg.AutoFailoverEnabled && ShouldFallback(response.StatusCode, errorBody, cfg);

            if (shouldFallback && attempt < maxAttempts - 1)
            {
                Console.WriteLine($"[灵枢] 第 {group.Slot + 1} 组 {group.Endpoint.Host} ✗{(int)response.StatusCode} → 容灾切换");
                response.Dispose();
                continue;
            }

            Console.WriteLine(attempt < maxAttempts - 1
                ? $"[灵枢] 第 {group.Slot + 1} 组 {group.Endpoint.Host} ✗{(int)response.StatusCode}"
                : $"[灵枢] 第 {group.Slot + 1} 组 {group.Endpoint.Host} ✗{(int)response.StatusCode}，无备用渠道可切换");

            response.Dispose();
            throw new Exception($"灵枢：HTTP {(int)response.StatusCode} {TrimBody(errorBody)}");
        }

        if (successResponse == null)
        {
            Console.WriteLine("[灵枢] 所有渠道均请求失败（超时或网络错误）");
            throw new Exception($"灵枢：所有渠道均请求失败（最后错误：{lastError?.Message ?? "未知"}）");
        }

        using (successResponse)
        {
            await ReadResponseAsync(
                successResponse, thinkingUsed, messageBuilder, thinkingBuilder, textReceived, thinkReceived, usageReceived, cancellationToken);
        }
    }

    /// <summary>发送单个渠道请求：按 RequestTimeoutMs 限制"等待响应头"的时长，正文流不受该超时限制。</summary>
    async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int configured = Configuration.RequestTimeoutMs;
        // 有效超时：<=0 视为关闭但保留 100s 兜底；>0 时下限 1s、上限 300s
        int timeoutMs = configured > 0 ? Math.Clamp(configured, 1000, 300000) : 100000;

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMs);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
        {
            throw new TimeoutException($"灵枢：渠道请求超时（{timeoutMs}ms 未收到响应头）");
        }
    }

    /// <summary>读取响应：SSE 走逐帧解析，非流式（部分渠道忽略 stream）走整包 JSON 兼容解析。</summary>
    async Task ReadResponseAsync(
        HttpResponseMessage response,
        bool thinking,
        StringBuilder messageBuilder,
        StringBuilder thinkingBuilder,
        Action<string>? textReceived,
        Action<string>? thinkReceived,
        Action<TokenUsage> usageReceived,
        CancellationToken cancellationToken)
    {
        int idleTimeoutMs = Configuration.StreamIdleTimeoutMs;
        bool showThinking = Configuration.ShowThinkingChain;

        void OnReasoning(string text)
        {
            thinkingBuilder.Append(text);
            if (showThinking)
                thinkReceived?.Invoke(text);
        }

        void OnContent(string text)
        {
            messageBuilder.Append(text);
            textReceived?.Invoke(text);
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        bool isJson = mediaType != null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

        if (isJson)
        {
            string body = await ReadAllAsync(response, idleTimeoutMs, cancellationToken);
            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch (JsonException) { node = null; }
            if (node is not JsonObject root)
                throw new Exception("灵枢：无法解析渠道返回的响应体");

            if (root["error"] is JsonNode error)
                throw new Exception($"灵枢：渠道返回错误：{OpenAISseParser.ErrorText(error)}");

            JsonObject? message = null;
            if (root["choices"] is JsonArray choices && choices.Count > 0)
                message = choices[0]?["message"] as JsonObject;

            string? content = OpenAISseParser.ExtractContent(message?["content"]);
            if (content != null)
                OnContent(content);

            string? reasoning = OpenAISseParser.ExtractReasoning(message);
            if (reasoning != null)
                OnReasoning(reasoning);

            if (root["usage"] is JsonObject usage)
                usageReceived(OpenAIChatProtocol.ParseUsage(usage));
            return;
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        // 用量在流式过程中可能被多个 chunk 重复携带，框架会累加回调值，因此这里只保留最后一次
        TokenUsage? lastUsage = null;

        await foreach (OpenAISseChunk chunk in OpenAISseParser.ParseAsync(stream, idleTimeoutMs, cancellationToken))
        {
            if (chunk.Error != null)
                throw new Exception($"灵枢：渠道返回错误：{chunk.Error}");

            if (chunk.Reasoning != null)
                OnReasoning(chunk.Reasoning);

            if (chunk.Content != null)
                OnContent(chunk.Content);

            if (chunk.Usage != null)
                lastUsage = OpenAIChatProtocol.ParseUsage(chunk.Usage);
        }

        if (lastUsage != null)
            usageReceived(lastUsage.Value);
    }

    /// <summary>读取非流式响应体（受流空闲超时约束）。</summary>
    static async Task<string> ReadAllAsync(HttpResponseMessage response, int idleTimeoutMs, CancellationToken cancellationToken)
    {
        if (idleTimeoutMs <= 0)
            return await response.Content.ReadAsStringAsync(cancellationToken);

        using CancellationTokenSource idleSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleSource.CancelAfter(idleTimeoutMs);
        try
        {
            return await response.Content.ReadAsStringAsync(idleSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
        {
            throw new TimeoutException($"灵枢：读取响应超时（{idleTimeoutMs}ms 未收到完整响应）");
        }
    }

    // ──── 请求组装 ────

    /// <summary>按目标渠道组组装 OpenAI 兼容请求体（模型/温度/思考参数/多模态能力均取该组配置）。</summary>
    HttpRequestMessage BuildRequest(ChatHistory history, FallbackGroup group, bool thinking)
    {
        JsonObject payload = new()
        {
            ["model"] = group.ModelId,
            ["messages"] = OpenAIChatProtocol.SerializeHistory(
                history, group.EnableNativeMultimodal, group.EnableAudioMultimodal),
            ["stream"] = true,
            // 主动索取用量：多数兼容端点支持，官方插件未设置导致统计长期为 0
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };

        if (group.Temperature.HasValue)
            payload["temperature"] = group.Temperature.Value;

        if (thinking)
        {
            if (string.IsNullOrWhiteSpace(group.ReasoningEffort) == false)
                payload["reasoning_effort"] = group.ReasoningEffort;
        }
        else
        {
            // 非思考模式：先清掉思考相关参数，稍后由 ExtraBodyNotThinking 显式禁用
            payload.Remove("reasoning_effort");
            payload.Remove("thinking");
        }

        ApplyBody(payload, group.ExtraBody, skipThinkingKeys: thinking == false);
        if (thinking == false)
            ApplyNonThinkingBody(payload, group.ExtraBodyNotThinking);

        HttpRequestMessage request = new(HttpMethod.Post, OpenAIChatProtocol.CreateChatCompletionsUri(group.Endpoint))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", group.ApiKey);

        if (group.ExtraHeaders != null)
        {
            foreach (KeyValuePair<string, string> header in group.ExtraHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    /// <summary>把组配置的额外请求体写入 payload（非思考模式下跳过思考相关键，避免覆盖禁用策略）。</summary>
    static void ApplyBody(JsonObject payload, IReadOnlyDictionary<string, JsonNode?>? body, bool skipThinkingKeys)
    {
        if (body == null)
            return;

        foreach (KeyValuePair<string, JsonNode?> pair in body)
        {
            if (skipThinkingKeys
                && (pair.Key.Equals("thinking", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("reasoning_effort", StringComparison.OrdinalIgnoreCase)))
                continue;

            payload[pair.Key] = pair.Value?.DeepClone();
        }
    }

    /// <summary>
    /// 非思考模式请求体：组内自定义优先；未配置时默认发送 {"thinking":{"type":"disabled"}}
    /// （对齐官方 extraBodyNotThinking，解决个别渠道不传该参数仍输出思维链的问题）。
    /// </summary>
    static void ApplyNonThinkingBody(JsonObject payload, IReadOnlyDictionary<string, JsonNode?>? body)
    {
        if (body is { Count: > 0 })
        {
            ApplyBody(payload, body, skipThinkingKeys: false);
            return;
        }

        payload["thinking"] = new JsonObject { ["type"] = "disabled" };
    }

    /// <summary>把 AI 回复写回对话历史（不含思考内容）。与官方一致：仅当有输出时入史。</summary>
    static void WriteHistory(ChatHistory history, string aiMessage)
    {
        if (string.IsNullOrWhiteSpace(aiMessage))
            return;

        // 旧版本曾把思考内容带 __THINK__ 前缀写入最后一条 assistant 消息，这里就地修正
        if (history.Count > 0)
        {
            ChatMessageContent last = history[^1];
            if (last.Role == AuthorRole.Assistant
                && (last.Content?.Contains(ThinkContentPrefix, StringComparison.Ordinal) ?? false))
            {
                last.Content = aiMessage;
                return;
            }
        }

        history.AddAssistantMessage(aiMessage);
    }

    // ──── 渠道服务标记 / 容灾联动 ────

    /// <summary>记录本轮实际服务的组并按需持久化容灾锁定（供 UI/提示词/统计插件使用）。</summary>
    void MarkServed(FallbackGroup group, bool failovered)
    {
        LastServedGroupIndex = group.Slot;

        if (failovered == false)
            return;

        string label = GetGroupLabel(group.Slot, Configuration);
        Console.WriteLine($"[灵枢] 已容灾切换 → {label}");

        if (Configuration.PriorityMainChannel)
            return; // 优先主渠道模式：容灾全程静默，不锁定、不打扰用户

        Configuration.ForcedGroupIndex = group.Slot;
        SaveConfig();
        interactor?.Poke($"灵枢已触发容灾，请告知用户，当前切换到了{label}");
        OnGroupChanged?.Invoke();
    }

    bool ConsumeFailoverTestFlag()
    {
        if (TestForceFailover == false)
            return false;
        TestForceFailover = false;
        return true;
    }

    /// <summary>
    /// 是否应触发容灾：429/402/401/403/5xx，或响应体命中内置内容审查、额度不足标记 / 用户自定义错误关键字。
    /// 401/403 通常是 Key 失效或额度耗尽，多渠道路由下换组往往能立刻恢复，因此也纳入容灾范围。
    /// </summary>
    static bool ShouldFallback(HttpStatusCode status, string? body, LanguageModelRouterConfig cfg)
    {
        int code = (int)status;
        if (code == 429 || code == 402 || code == 401 || code == 403 || code >= 500)
            return true;

        if (string.IsNullOrEmpty(body))
            return false;

        foreach (string marker in BuiltinFallbackMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (string keyword in ParseErrorKeywords(cfg.ErrorKeywords))
        {
            if (body.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>错误体裁剪：日志/错误提示里避免整页 HTML 或超长 JSON。</summary>
    static string TrimBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        body = body.Trim();
        return body.Length <= 600 ? body : body[..600] + "…";
    }

    // ──── HTTP 管道 ────

    /// <summary>构建专用 HttpClient（一次）：强制 HTTP/1.1、连接池 5 分钟回收、超时交由单次尝试控制。</summary>
    void EnsureHttpClient()
    {
        if (httpClient != null)
            return;

        lock (httpClientLock)
        {
            if (httpClient != null)
                return;

            LanguageModelRouterConfig cfg = Configuration;
            if (cfg == null)
                throw new Exception("灵枢：模块配置未注入，请重载此插件或重新激活角色");
            cfg.EnsureGroups();

            List<FallbackGroup> groups = BuildFallbackGroups(cfg);
            if (groups.Count == 0)
                throw new Exception("灵枢：未配置任何渠道组，请在插件配置中填写至少一组 Endpoint / API Key");

            SocketsHttpHandler handler = new()
            {
                // 默认忽略证书校验错误以兼容自签名/证书异常的渠道；关闭后走系统证书校验
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = cfg.IgnoreSslCertificate
                        ? delegate { return true; }
                        : null
                },
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            httpClient = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                // 超时交由 SendAsync 按"单次渠道尝试"控制，避免 HttpClient 内置 100s 超时与之冲突
                Timeout = Timeout.InfiniteTimeSpan
            };

            Console.WriteLine($"[灵枢] 已就绪，主渠道：{GetGroupLabel(groups[0].Slot, cfg)}");
        }
    }

    // ──── 智能思考 ────

    /// <summary>
    /// 智能思考/非思考切换判断（对齐官方 OpenAILanguageModel 的 GetThinkingRequester().IsOccupied 逻辑）：
    /// - 组开关关闭 → 恒思考（保持该组原配置的思考行为）
    /// - 组开关开启 → 有模块请求思考（如 AI 需调用工具/群消息/监听等）→ 思考；否则非思考
    /// - IgnorePersistentThinking 开启时：忽略 XmlFunctionCaller 的"隐式功能激活中"这类【历史判定】永久占用，
    ///   只响应当次任务的瞬时信号，让历史中调用过工具的角色日常闲聊也能走非思考
    /// - ThinkingTriggerKeywords 命中当前用户消息时，即使默认非思考也强制走思考
    /// </summary>
    internal bool ComputeThinkingMode(LanguageModelRouterConfig cfg, bool groupSmartThinkingEnabled)
    {
        if (groupSmartThinkingEnabled == false)
            return true;

        if (MatchesThinkingTrigger(cfg, currentUserMessage))
            return true;

        if (cfg.IgnorePersistentThinking == false)
            return thinkingRequester.IsOccupied;

        // 过滤"隐式功能激活中"类永久占用：仅当存在非永久性原因时才视为需要思考
        bool hasEphemeralRequest = false;
        thinkingRequester.Query(markers =>
        {
            foreach (OccupationMarker marker in markers)
            {
                string reason = marker.Reason ?? "";
                if (reason.Contains("隐式功能激活中", StringComparison.OrdinalIgnoreCase))
                    continue;
                hasEphemeralRequest = true;
                break;
            }
        });
        return hasEphemeralRequest;
    }

    /// <summary>关键词触发思考：任一关键词命中当前用户消息即返回 true（忽略大小写，支持中英文逗号/分号/竖线分隔）。</summary>
    static bool MatchesThinkingTrigger(LanguageModelRouterConfig cfg, string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(cfg.ThinkingTriggerKeywords) || string.IsNullOrWhiteSpace(userMessage))
            return false;

        foreach (string keyword in cfg.ThinkingTriggerKeywords.Split(
            [',', '，', ';', '；', '|', '｜'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (keyword.Length > 0 && userMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ──── 配置持久化 ────

    /// <summary>
    /// 将当前 Configuration 落盘（角色级配置优先，回退全局）。
    /// 模块内通过函数/容灾修改的运行时状态（如 ForcedGroupIndex）需显式保存，否则重载/重启后会丢失。
    /// </summary>
    void SaveConfig()
    {
        try
        {
            if (Configuration == null)
                return;
            configurationSystem.SetConfiguration(
                typeof(LanguageModelRouter),
                Configuration,
                Character?.StorageKey ?? "");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[灵枢] 配置保存失败");
        }
    }

    /// <summary>
    /// 供 UI 面板在「手动切换渠道」后立即应用并保存配置：把面板里的配置实例同步给运行中的模块并落盘，
    /// 使手动切换无需再点一次框架的「保存配置」即生效、重启后仍保留。
    /// </summary>
    internal void ApplyConfigAndSave(LanguageModelRouterConfig config)
    {
        Configuration = config;
        SaveConfig();
    }

    // ──── 提示词 ────

    string BuildPrompt(LanguageModelRouterConfig cfg)
    {
        List<FallbackGroup> groups = BuildFallbackGroups(cfg);

        StringBuilder sb = new();
        sb.AppendLine("## 语言模型渠道切换能力");
        sb.AppendLine("你支持多组语言模型渠道，可以在不同 API 渠道之间切换。");
        sb.AppendLine("当用户要求「切换语言模型」「换大模型」「换到某某渠道」时，使用 SwitchModelGroup 工具切换；说「切回自动」则恢复自动容灾。");

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

            sb.AppendLine();
            sb.AppendLine(cfg.AutoFailoverEnabled == false
                ? "当前已关闭自动容灾，只会使用当前选定的渠道。"
                : cfg.ForcedGroupIndex >= 0
                    ? $"当前手动锁定在第 {cfg.ForcedGroupIndex + 1} 组（该组失败时仍会自动切换备用渠道）。"
                    : "当前为自动容灾模式：主组优先，失败自动切换到下一组。");
        }

        if (cfg.HasAnyMultimodalGroup())
        {
            sb.AppendLine();
            sb.AppendLine("其中部分渠道组开启了「原生多模态」或「音频输入」。当需要分析图片/视频/音频时，你可以使用 LoadImage / LoadVideo / LoadAudio 函数查看这些内容。");
        }

        return sb.ToString();
    }

    // ──── AI 可调用函数 ────

    [XmlFunction(FunctionMode.OneShot)]
    [Description("模拟一次容灾测试：下次对话时将自动触发假 429 错误，验证容灾切换是否正常工作。使用后自动复位。")]
    public Task SimulateFailoverTest()
    {
        if (Configuration == null)
        {
            interactor?.Poke("灵枢：配置为空，无法测试。");
            return Task.CompletedTask;
        }

        if (Configuration.AutoFailoverEnabled == false)
        {
            interactor?.Poke("自动容灾当前已关闭，无法测试。请在容灾设置中开启后重试。");
            return Task.CompletedTask;
        }

        List<FallbackGroup> groups = BuildFallbackGroups(Configuration);
        if (groups.Count < 2)
        {
            interactor?.Poke($"当前仅配置了 {groups.Count} 组可用渠道，没有备用渠道可切换。请至少配置两组后再测试。");
            return Task.CompletedTask;
        }

        TestForceFailover = true;
        interactor?.Poke("容灾测试已启动，请发送下一条消息，系统将模拟第 1 组返回 429 并自动切换到备用渠道。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检查所有已配置的语言模型渠道组的连接状态和可用模型列表。当用户要求测试连接、检查有哪些模型可用、或网络不通时调用。")]
    public async Task DetectModelGroup()
    {
        LanguageModelRouterConfig cfg = Configuration;
        if (cfg == null) { interactor?.Poke("配置为空，请先填写配置"); return; }

        cfg.EnsureGroups();
        List<string> results = new();
        for (int i = 0; i < cfg.Groups.Count; i++)
        {
            (string display, _) = await FetchModels(i, cfg);
            results.Add(display);
        }

        interactor?.Poke(string.Join("\n", results));
    }

    /// <summary>一句话切换渠道（AI 通过此工具调用）：支持组编号（"切换到第2组"/"2"）或组名称（"切换到deepseek"），也可切回自动容灾。</summary>
    [XmlFunction(FunctionMode.OneShot)]
    [Description("将语言模型渠道切换到指定组。支持按组编号（如\"切换到第2组\"）或按组名称（如\"切换到deepseek\"）；说\"切回自动\"则恢复自动容灾模式。调用成功后立即生效。")]
    public Task SwitchModelGroup([Description("目标渠道：组编号（1~N）或组名称（支持部分匹配），或\"自动\"恢复自动容灾")] string target)
    {
        LanguageModelRouterConfig cfg = Configuration;
        if (cfg == null)
        {
            interactor?.Poke("灵枢：配置为空，无法切换渠道。");
            return Task.CompletedTask;
        }

        cfg.EnsureGroups();
        if (cfg.Groups.Count == 0)
        {
            interactor?.Poke("灵枢：当前没有任何渠道组，无法切换。");
            return Task.CompletedTask;
        }

        string raw = (target ?? "").Trim();

        // 切回自动容灾
        if (raw.Contains("自动", StringComparison.Ordinal)
            || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            cfg.ForcedGroupIndex = -1;
            SaveConfig();
            interactor?.Poke("已切换回自动容灾模式（主组优先）。");
            OnGroupChanged?.Invoke();
            return Task.CompletedTask;
        }

        // 1) 按编号："2" / "第2组" / "2组"
        if (TryParseGroupNumber(raw, out int oneBased))
        {
            if (oneBased < 1 || oneBased > cfg.Groups.Count)
            {
                interactor?.Poke($"灵枢：组编号 {oneBased} 超出范围（当前共 {cfg.Groups.Count} 组）。");
                return Task.CompletedTask;
            }

            int slot = oneBased - 1;
            if (cfg.Groups[slot].IsConfigured)
            {
                DoSwitch(slot);
                return Task.CompletedTask;
            }
            interactor?.Poke($"灵枢：第 {oneBased} 组未配置，无法切换。");
            return Task.CompletedTask;
        }

        // 2) 按名称匹配（先组名包含关键字，再关键字包含组名，忽略大小写）
        if (raw.Length > 0)
        {
            var matched = cfg.Groups
                .Select((ch, i) => (ch, i))
                .Where(x => string.IsNullOrWhiteSpace(x.ch.GroupName) == false
                    && x.ch.GroupName.Contains(raw, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matched.Count == 0)
            {
                matched = cfg.Groups
                    .Select((ch, i) => (ch, i))
                    .Where(x => string.IsNullOrWhiteSpace(x.ch.GroupName) == false
                        && raw.Contains(x.ch.GroupName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (matched.Count == 1)
            {
                int slot = matched[0].i;
                if (cfg.Groups[slot].IsConfigured)
                {
                    DoSwitch(slot);
                    return Task.CompletedTask;
                }
                interactor?.Poke($"灵枢：组「{cfg.Groups[slot].GroupName}」未配置，无法切换。");
                return Task.CompletedTask;
            }
            if (matched.Count > 1)
            {
                interactor?.Poke($"灵枢：名称「{raw}」匹配到多组：{string.Join("、", matched.Select(x => x.ch.GroupName))}，请使用完整名称或组编号。");
                return Task.CompletedTask;
            }
            interactor?.Poke($"灵枢：未找到名称为「{raw}」的渠道组。当前已配置：{ListConfiguredGroups()}");
            return Task.CompletedTask;
        }

        interactor?.Poke("灵枢：请提供目标组编号（如 2）或组名称（如 deepseek）。");
        return Task.CompletedTask;
    }

    /// <summary>执行切换：写入强制锁定索引并通知 UI/用户（随配置落盘，重启保留）。</summary>
    void DoSwitch(int slot)
    {
        Configuration.ForcedGroupIndex = slot;
        SaveConfig();
        string label = GetGroupLabel(slot, Configuration);
        Console.WriteLine($"[灵枢] 已切换 → {label}");
        interactor?.Poke($"已切换到{label}");
        OnGroupChanged?.Invoke();
    }

    /// <summary>中文数字映射（含"两"）</summary>
    static readonly Dictionary<char, int> ChineseNumeralMap = new()
    {
        ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
        ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10
    };

    /// <summary>解析中文数字 1~12（支持"二"、"十"、"十一"、"十二"），解析失败返回 null。</summary>
    static int? ParseChineseNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length == 1 && ChineseNumeralMap.TryGetValue(s[0], out int single))
            return single;
        if (s.Length == 2 && s[0] == '十')
        {
            int unit = ChineseNumeralMap.TryGetValue(s[1], out int u) ? u : 0;
            return 10 + unit;
        }
        return null;
    }

    /// <summary>解析组编号："2"、"第2组"、"2组"、"第二组"、"第12组" 均解析为对应编号。</summary>
    static bool TryParseGroupNumber(string raw, out int oneBased)
    {
        oneBased = 0;
        if (int.TryParse(raw, out oneBased))
            return oneBased >= 1;

        Match match = Regex.Match(raw, @"^第?\s*(\d+)\s*组?$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out oneBased) && oneBased >= 1)
            return true;

        Match chineseMatch = Regex.Match(raw, @"^第?\s*([一二两三四五六七八九十]+)\s*组?$");
        if (chineseMatch.Success)
        {
            int? number = ParseChineseNumber(chineseMatch.Groups[1].Value);
            if (number.HasValue && number.Value >= 1)
            {
                oneBased = number.Value;
                return true;
            }
        }

        oneBased = 0;
        return false;
    }

    /// <summary>列出所有已配置组（编号+名称），供提示用户。</summary>
    string ListConfiguredGroups()
    {
        return string.Join("、", Configuration.Groups
            .Select((ch, i) => (ch, i))
            .Where(x => x.ch.IsConfigured)
            .Select(x => $"第{x.i + 1}组{(string.IsNullOrWhiteSpace(x.ch.GroupName) ? "" : $"（{x.ch.GroupName}）")}"));
    }

    // ──── UI 辅助（LanguageModelRouterUI 直接调用）────

    internal static int GetPrimarySlot(LanguageModelRouterConfig cfg) => 0;

    /// <summary>获取显示顺序（0..Groups.Count-1 拷贝）。</summary>
    internal static int[] GetGroupOrder(LanguageModelRouterConfig cfg)
    {
        cfg?.EnsureGroups();
        return cfg == null ? [] : Enumerable.Range(0, cfg.Groups.Count).ToArray();
    }

    internal static string GetGroupName(int idx, LanguageModelRouterConfig? cfg)
    {
        if (cfg == null) return "";
        cfg.EnsureGroups();
        if (idx < 0 || idx >= cfg.Groups.Count) return "";
        return cfg.Groups[idx].GroupName ?? "";
    }

    internal static string GetGroupLabel(int idx, LanguageModelRouterConfig? cfg)
    {
        string name = GetGroupName(idx, cfg);
        string baseLabel = $"第 {idx + 1} 组";
        return string.IsNullOrWhiteSpace(name) ? baseLabel : $"{baseLabel}（{name}）";
    }

    /// <summary>测试一组连接，返回可用模型列表（index 为 Groups 索引）。</summary>
    internal static async Task<(string Display, List<string>? Models)> FetchModels(int index, LanguageModelRouterConfig cfg)
    {
        if (cfg == null) return ("配置为空", null);

        cfg.EnsureGroups();
        if (index < 0 || index >= cfg.Groups.Count)
            return ($"第 {index + 1} 组不存在", null);

        GroupChannel channel = cfg.Groups[index];
        if (channel.IsConfigured == false)
            return ($"第 {index + 1} 组未配置", null);

        string label = GetGroupLabel(index, cfg);

        Uri modelsUri;
        try
        {
            modelsUri = BuildModelsUri(channel.Endpoint);
        }
        catch (UriFormatException)
        {
            return ($"{label}：Endpoint 格式非法", null);
        }

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
            using HttpRequestMessage request = new(HttpMethod.Get, modelsUri);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", LanguageModelRouterConfig.UnprotectSecret(channel.ApiKey));
            ApplyConfiguredHeaders(request, channel.ExtraHeaders);

            using HttpResponseMessage response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode == false)
                return ($"{label}：连接失败（HTTP {(int)response.StatusCode}）", null);

            using JsonDocument document = JsonDocument.Parse(body);
            List<string> models = document.RootElement.TryGetProperty("data", out JsonElement data)
                ? data.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out JsonElement id) ? id.GetString() : "?")
                    .Where(m => string.IsNullOrWhiteSpace(m) == false)
                    .Select(m => m!)
                    .ToList()
                : [];

            if (models.Count == 0)
                return ($"{label}：连接成功，但未获取到模型列表", null);

            return ($"{label}（{models.Count} 个模型）：{string.Join("、", models.Take(10))}{(models.Count > 10 ? "…" : "")}", models);
        }
        catch (Exception ex)
        {
            return ($"{label}：连接异常（{ex.Message}）", null);
        }
    }

    /// <summary>Endpoint → /models 地址（保留查询参数；已以 /chat/completions 结尾时替换该段）。</summary>
    static Uri BuildModelsUri(string endpoint)
    {
        UriBuilder builder = new(new Uri(endpoint));
        string path = builder.Path.TrimEnd('/');
        const string chatPath = "/chat/completions";
        if (path.EndsWith(chatPath, StringComparison.OrdinalIgnoreCase))
            path = path[..^chatPath.Length];
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase) == false)
            path += "/models";
        builder.Path = path;
        return builder.Uri;
    }

    /// <summary>探测请求也带上组内配置的额外请求头（官方 UI 的探测未带，容易与真实请求不一致）。</summary>
    static void ApplyConfiguredHeaders(HttpRequestMessage request, string? extraHeaders)
    {
        Dictionary<string, string>? headers = ParseExtraHeaders(extraHeaders);
        if (headers == null) return;
        foreach (KeyValuePair<string, string> header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    // ──── 渠道组构建 ────

    /// <summary>按 Groups 顺序构建渠道组列表（仅含已配置组），第一项即主组。</summary>
    static List<FallbackGroup> BuildFallbackGroups(LanguageModelRouterConfig config)
    {
        List<FallbackGroup> groups = [];
        config.EnsureGroups();

        for (int i = 0; i < config.Groups.Count; i++)
        {
            GroupChannel channel = config.Groups[i];
            if (channel.IsConfigured == false) continue;

            if (Uri.TryCreate(channel.Endpoint, UriKind.Absolute, out Uri? uri) == false)
            {
                // 一组 Endpoint 格式非法不应拖垮整个插件，跳过该组并告警
                Console.WriteLine($"[灵枢] 第 {i + 1} 组 Endpoint 格式非法，已跳过：{channel.Endpoint}");
                continue;
            }

            groups.Add(new FallbackGroup(
                Slot: i,
                Endpoint: uri,
                ModelId: channel.ModelId ?? "",
                // 存储为 DPAPI 密文，进入请求管道前解密
                ApiKey: LanguageModelRouterConfig.UnprotectSecret(channel.ApiKey),
                ExtraHeaders: ParseExtraHeaders(channel.ExtraHeaders),
                ReasoningEffort: channel.ReasoningEffort,
                Temperature: channel.Temperature,
                ExtraBody: ParseExtraBody(channel.ExtraBody),
                ExtraBodyNotThinking: ParseExtraBody(channel.ExtraBodyNotThinking),
                EnableNativeMultimodal: channel.EnableNativeMultimodal,
                EnableAudioMultimodal: channel.EnableAudioMultimodal,
                SmartThinkingEnabled: channel.SmartThinkingEnabled));
        }

        return groups;
    }

    /// <summary>强制锁定渠道在组列表中的显示索引；-1 表示从主组开始。</summary>
    static int GetForcedDisplayIndex(LanguageModelRouterConfig config)
    {
        if (config.ForcedGroupIndex < 0) return -1;
        List<FallbackGroup> groups = BuildFallbackGroups(config);
        return groups.FindIndex(g => g.Slot == config.ForcedGroupIndex);
    }

    static Dictionary<string, string>? ParseExtraHeaders(string? extraHeaders)
    {
        if (string.IsNullOrWhiteSpace(extraHeaders)) return null;
        try
        {
            Dictionary<string, string> dict = [];
            using JsonDocument document = JsonDocument.Parse(extraHeaders);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && string.IsNullOrEmpty(property.Value.GetString()) == false)
                    dict[property.Name] = property.Value.GetString()!;
            }
            return dict.Count > 0 ? dict : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[灵枢] ExtraHeaders 解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>解析 Extra Body JSON 为 JsonNode 字典（保留嵌套结构；失败返回 null，不阻断请求）。</summary>
    static Dictionary<string, JsonNode?>? ParseExtraBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return null;
        try
        {
            if (JsonNode.Parse(bodyJson) is not JsonObject root)
                return null;

            Dictionary<string, JsonNode?> dict = [];
            foreach (KeyValuePair<string, JsonNode?> pair in root)
                dict[pair.Key] = pair.Value?.DeepClone();
            return dict.Count > 0 ? dict : null;
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
            return [];

        return keywords
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}

/// <summary>
/// 一次请求可落地的渠道快照（已解密 Key、已解析 JSON），
/// 每次请求按当前配置重建，UI 修改即刻生效。
/// </summary>
internal sealed record FallbackGroup(
    int Slot,
    Uri Endpoint,
    string ModelId,
    string ApiKey,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null,
    string? ReasoningEffort = null,
    double? Temperature = null,
    IReadOnlyDictionary<string, JsonNode?>? ExtraBody = null,
    IReadOnlyDictionary<string, JsonNode?>? ExtraBodyNotThinking = null,
    bool EnableNativeMultimodal = false,
    bool EnableAudioMultimodal = false,
    bool SmartThinkingEnabled = false);
