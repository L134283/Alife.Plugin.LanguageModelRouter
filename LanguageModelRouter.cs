using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Alife.Plugin.LanguageModelRouter;

[Module("灵枢 - 实时切换语言模型/报错自动切换容灾",
    "替换框架内置 OpenAILanguageModel，最多 4 组渠道。一句话让 AI 切换渠道，遭遇 429/5xx 自动容灾重试，可探测可用模型列表。",
    editorUI: typeof(LanguageModelRouterUI),
    defaultCategory: "Doro的妙妙工具")]
public class LanguageModelRouter(
    ILogger<LanguageModelRouter> logger,
    XmlFunctionCaller functionService
) : InteractiveModule<LanguageModelRouter>, ILanguageModel, IConfigurable<LanguageModelRouterConfig>
{
    public LanguageModelRouterConfig? Configuration { get; set; }

    /// <summary>-1=自动容灾, 0~3=强制使用第N组</summary>
    internal static volatile int ForcedGroupIndex = -1;

    /// <summary>是否启用自动容灾切换</summary>
    internal static volatile bool AutoFailoverEnabled = true;

    internal static Action? OnGroupChanged;

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        var handler = new XmlHandler(this)
        {
            Description = "此服务管理语言模型的渠道切换，支持在多个 API 渠道之间手动切换。",
        };
        functionService.RegisterHandler(handler);

        var cfg = Configuration;
        var groups = cfg != null ? BuildFallbackGroups(cfg) : new List<FallbackGroup>();
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("## 语言模型渠道切换能力");
        sb.AppendLine("你支持多组语言模型渠道，可以在不同 API 渠道之间切换。");
        sb.AppendLine("当用户要求「切换语言模型」「换大模型」「换到某某渠道」时，使用 SwitchModelGroup 工具切换。");

        if (groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("当前已配置的渠道组（可通过编号或名称切换）：");
            for (int i = 0; i < groups.Count; i++)
            {
                string name = GetGroupName(i, cfg);
                string label = string.IsNullOrWhiteSpace(name) ? "" : $"（名称：{name}）";
                sb.AppendLine($"- 第{i + 1}组{label}：{groups[i].Endpoint} → {groups[i].ModelId}");
            }
        }
        sb.AppendLine();

        Prompt(sb.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换当前使用的语言模型渠道组。当用户要求切换语言模型、换一个大模型、换到某个渠道时调用。")]
    public Task SwitchModelGroup(
        [Description("目标组编号：1=第1组, 2=第2组, 3=第3组, 4=第4组")] int? groupIndex = null,
        [Description("目标组名称（如配置的自定义名称），与编号二选一")] string? groupName = null)
    {
        var cfg = Configuration;
        int targetIdx;

        if (groupIndex.HasValue)
        {
            if (groupIndex.Value < 1 || groupIndex.Value > 4)
            {
                Poke($"无效的组编号：{groupIndex.Value}，请使用 1 ~ 4");
                return Task.CompletedTask;
            }
            targetIdx = groupIndex.Value - 1;
        }
        else if (!string.IsNullOrWhiteSpace(groupName))
        {
            var names = new[] { cfg?.GroupName1, cfg?.GroupName2, cfg?.GroupName3, cfg?.GroupName4 };
            targetIdx = -1;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i]?.Trim(), groupName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    targetIdx = i;
                    break;
                }
            }
            if (targetIdx < 0)
            {
                Poke($"未找到名称为「{groupName}」的渠道组，请在插件配置中查看各组名称");
                return Task.CompletedTask;
            }
        }
        else
        {
            Poke("请指定要切换到的组编号（1~4）或组名称");
            return Task.CompletedTask;
        }

        ForcedGroupIndex = targetIdx;
        string label = GetGroupLabel(targetIdx, cfg);
        System.Console.WriteLine($"[灵枢] 已切换 → {label}");
        Poke($"已切换到{label}");
        OnGroupChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>测试一组连接，返回可用模型列表</summary>
    internal static async Task<(string Display, List<string>? Models)> FetchModels(int groupIndex, LanguageModelRouterConfig cfg)
    {
        if (cfg == null) return ("配置为空", null);

        var groups = BuildFallbackGroups(cfg);
        if (groupIndex < 0 || groupIndex >= groups.Count) return ($"第 {groupIndex + 1} 组未配置", null);

        var group = groups[groupIndex];
        string label = GetGroupLabel(groupIndex, cfg);
        string ep = group.Endpoint.ToString().TrimEnd('/');
        if (!ep.Contains("/models"))
            ep += "/models";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new HttpRequestMessage(HttpMethod.Get, ep);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", group.ApiKey);
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
        if (cfg == null) { Poke("配置为空，请先填写配置"); return; }

        var groups = BuildFallbackGroups(cfg);
        if (groups.Count == 0) { Poke("未配置任何渠道组"); return; }

        var results = new List<string>();
        for (int i = 0; i < groups.Count; i++)
        {
            var (display, _) = await FetchModels(i, cfg);
            results.Add(display);
        }

        Poke(string.Join("\n", results));
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
        return idx switch
        {
            0 => cfg.GroupName1,
            1 => cfg.GroupName2,
            2 => cfg.GroupName3,
            3 => cfg.GroupName4,
            _ => ""
        } ?? "";
    }

    public void RegisterChatCompletion(IKernelBuilder kernelBuilder)
    {
        var config = Configuration!;
        if (string.IsNullOrWhiteSpace(config.ApiKey1))
            throw new Exception("灵枢：第1组 API Key 为空");

        if (string.IsNullOrWhiteSpace(config.Endpoint1))
            throw new Exception("灵枢：第1组 Endpoint 为空");

        // 解析错误关键字（启动时固定，后续不改）
        var errorKeywords = ParseErrorKeywords(config.ErrorKeywords);

        // 构建 HTTP 管道：SocketsHttpHandler -> FallbackHandler
        SocketsHttpHandler handler = new()
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        // 传动态委托，每次请求从当前 Configuration 重建组列表
        FallbackHandler fallbackHandler = new(handler,
            () => BuildFallbackGroups(Configuration!),
            errorKeywords, config.RetryDelayMs,
            () => ForcedGroupIndex, () => AutoFailoverEnabled,
            onFailover: idx =>
            {
                string label = GetGroupLabel(idx, Configuration);
                Console.WriteLine($"[灵枢] 已容灾切换 → {label}");
                Poke($"灵枢已触发容灾，请告知用户，当前切换到了{label}");
            });

        HttpClient httpClient = new(fallbackHandler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        // 处理第1组的 extraHeaders
        ApplyExtraHeaders(httpClient, config.ExtraHeaders1);

        // 用第1组注册 SemanticKernel（FallbackHandler 在运行时动态切换）
        kernelBuilder.AddOpenAIChatCompletion(
            endpoint: new Uri(config.Endpoint1),
            modelId: config.ModelId1,
            apiKey: config.ApiKey1,
            httpClient: httpClient
        );

        System.Console.WriteLine("[灵枢] 已注册");
    }

    [System.Diagnostics.CodeAnalysis.Experimental("SKEXP0010")]
    public PromptExecutionSettings ProvidePromptExecutionSettings()
    {
        var config = Configuration!;
        int idx = ForcedGroupIndex >= 0 ? ForcedGroupIndex : 0;
        if (idx >= BuildFallbackGroups(config).Count) idx = 0;

        string? effort = idx switch
        {
            0 => config.ReasoningEffort1,
            1 => config.ReasoningEffort2,
            2 => config.ReasoningEffort3,
            3 => config.ReasoningEffort4,
            _ => null
        };
        string? bodyJson = idx switch
        {
            0 => config.ExtraBody1,
            1 => config.ExtraBody2,
            2 => config.ExtraBody3,
            3 => config.ExtraBody4,
            _ => null
        };

        OpenAIPromptExecutionSettings settings = new();

        if (!string.IsNullOrEmpty(effort))
            settings.ReasoningEffort = effort;

        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
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
                settings.ExtraBody = bodyObj;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[灵枢] ExtraBody 解析失败: {ex.Message}");
            }
        }

        return settings;
    }

    static List<FallbackGroup> BuildFallbackGroups(LanguageModelRouterConfig config)
    {
        var groups = new List<FallbackGroup>();

        if (!string.IsNullOrWhiteSpace(config.Endpoint1))
            groups.Add(new FallbackGroup(new Uri(config.Endpoint1), config.ModelId1, config.ApiKey1));

        // 第2~4组可选
        for (int i = 2; i <= 4; i++)
        {
            string? endpoint = i switch
            {
                2 => config.Endpoint2,
                3 => config.Endpoint3,
                4 => config.Endpoint4,
                _ => null
            };
            string? modelId = i switch
            {
                2 => config.ModelId2,
                3 => config.ModelId3,
                4 => config.ModelId4,
                _ => null
            };
            string? apiKey = i switch
            {
                2 => config.ApiKey2,
                3 => config.ApiKey3,
                4 => config.ApiKey4,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(apiKey))
                groups.Add(new FallbackGroup(new Uri(endpoint!), modelId ?? "", apiKey!));
        }

        return groups;
    }

    static List<string> ParseErrorKeywords(string? keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return new List<string>();

        return keywords!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    static void ApplyExtraHeaders(HttpClient httpClient, string? extraHeaders)
    {
        if (string.IsNullOrWhiteSpace(extraHeaders))
            return;

        try
        {
            using var doc = JsonDocument.Parse(extraHeaders!);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(prop.Name, prop.Value.GetString());
            }
        }
        catch (Exception ex)
        {
            // 静默忽略，不影响主流程
            System.Diagnostics.Debug.WriteLine($"灵枢：解析 ExtraHeaders 失败: {ex.Message}");
        }
    }
}