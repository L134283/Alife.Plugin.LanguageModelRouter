using System;
using System.Text;
using System.Text.Json.Nodes;
using Alife.Function.TokenStatistics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// OpenAI 兼容协议的纯序列化层：端点归一化、对话历史 → 请求体、usage → TokenUsage。
/// 与官方 <c>Alife.Function.Language.OpenAI</c> 的 AlifeChatProtocol 同构，区别在于：
/// ① 媒体是否随请求发送由"目标渠道组"的多模态开关决定，逐组序列化（官方为单一模型，只有全局开关）；
/// ② 纯文本消息仍序列化为字符串而非单元素数组，兼容对 content 类型要求严格的兼容端点；
/// ③ 兼容旧版本写入历史的 &lt;c&gt;__THINK__&lt;/c&gt; 思维链前缀。
/// 该类不做 IO、不写日志，便于单测。
/// </summary>
public static class OpenAIChatProtocol
{
    /// <summary>
    /// 把用户配置的 Endpoint 归一化为 chat/completions 地址：
    /// 已包含该路径则原样使用，否则追加；保留 Endpoint 自带的查询参数（如 ?api-version=xxx）。
    /// </summary>
    public static Uri CreateChatCompletionsUri(Uri endpoint)
    {
        UriBuilder builder = new(endpoint);
        string path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == false)
            path += "/chat/completions";
        builder.Path = path;
        return builder.Uri;
    }

    /// <summary>
    /// 序列化整段对话历史。allowImageVideo / allowAudio 为本次请求目标组的原生多模态开关：
    /// 未开启的类型会被剥离为占位文本（如 [图片]），避免把媒体塞给不支持的上游。
    /// allowPersistentMedia 决定历史中的"常驻媒体"是否继续随请求发送（只在最后一条消息之外生效）。
    /// mediaIncluded 返回本次请求体里是否真的带了媒体（供"被拒后去图重试"判断）。
    /// </summary>
    public static JsonArray SerializeHistory(
        ChatHistory history, bool allowImageVideo, bool allowAudio, bool allowPersistentMedia, out bool mediaIncluded)
    {
        bool hasMedia = false;
        JsonArray messages = new();
        for (int i = 0; i < history.Count; i++)
        {
            ChatMessageContent message = history[i];
            string role = message.Role == AuthorRole.System ? "system"
                : message.Role == AuthorRole.Assistant ? "assistant"
                : message.Role == AuthorRole.Tool ? "tool"
                : "user";

            // 只有用户消息可能携带媒体（图片/视频/音频），其余角色一律纯文本
            JsonNode content = role == "user"
                ? SerializeUserContent(message, allowImageVideo, allowAudio, allowPersistentMedia, i == history.Count - 1, ref hasMedia)
                : JsonValue.Create(StripThinkPrefix(message.Content))!;

            messages.Add(new JsonObject {
                ["role"] = role,
                ["content"] = content
            });
        }

        mediaIncluded = hasMedia;
        return messages;
    }

    static JsonNode SerializeUserContent(
        ChatMessageContent message, bool allowImageVideo, bool allowAudio, bool allowPersistentMedia, bool isLastMessage, ref bool mediaIncluded)
    {
        // 携带可用媒体 → OpenAI 原生 content parts 数组
        JsonArray? parts = NativeMultimodalProtocol.TrySerializeWithMedia(
            message, allowImageVideo, allowAudio, allowPersistentMedia, isLastMessage);
        if (parts != null)
        {
            mediaIncluded = true;
            return parts;
        }

        // 纯文本（或媒体全部被禁用）→ 字符串通道，保持最大兼容
        string text = StripThinkPrefix(message.Content);
        string? placeholder = NativeMultimodalProtocol.MediaTextPlaceholder(message, text);
        return JsonValue.Create(placeholder ?? text)!;
    }

    /// <summary>解析 usage 字段（兼容 int/long/double 与 input_tokens/output_tokens、prompt_cache_hit_tokens 等变体）。</summary>
    public static TokenUsage ParseUsage(JsonObject usage)
    {
        int input = TryGetInt(usage, "prompt_tokens", "input_tokens");
        int output = TryGetInt(usage, "completion_tokens", "output_tokens");
        int total = TryGetInt(usage, "total_tokens");
        if (total == 0)
            total = input + output;

        // 缓存命中：OpenAI 标准在 prompt_tokens_details.cached_tokens，部分中转渠道用 prompt_cache_hit_tokens
        int cached = usage["prompt_tokens_details"] is JsonObject details
            ? TryGetInt(details, "cached_tokens")
            : 0;
        if (cached == 0)
            cached = TryGetInt(usage, "prompt_cache_hit_tokens");

        return new TokenUsage {
            Input = input,
            Output = output,
            Total = total,
            Cached = cached
        };
    }

    /// <summary>
    /// 反推本轮用户消息（供 Token 统计）：从尾部往前跳过连续的用户消息块，拼接这些用户消息。
    /// 与官方 OpenAILanguageModel 的取法一致。
    /// </summary>
    public static string GetUserMessage(ChatHistory history)
    {
        int start = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role != AuthorRole.User)
            {
                start = i + 1;
                break;
            }
        }

        StringBuilder builder = new();
        for (int i = start; i < history.Count; i++)
            builder.AppendLine(history[i].Content);
        return builder.ToString();
    }

    /// <summary>取最后一条用户消息的纯文本（供"关键词触发思考"匹配）。</summary>
    public static string? GetLastUserMessage(ChatHistory history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == AuthorRole.User)
                return StripThinkPrefix(history[i].Content);
        }
        return null;
    }

    /// <summary>去掉旧版本写入对话历史的思维链前缀（历史可能来自旧版本，清理后再发给上游）。</summary>
    public static string StripThinkPrefix(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        return text.StartsWith(LanguageModelRouter.ThinkContentPrefix, StringComparison.Ordinal)
            ? text[LanguageModelRouter.ThinkContentPrefix.Length..]
            : text;
    }

    /// <summary>按候选键依次读取整数（缺省或类型不符返回 0）。</summary>
    internal static int TryGetInt(JsonObject obj, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (obj[key] is not JsonValue value)
                continue;
            if (value.TryGetValue<int>(out int i))
                return i;
            if (value.TryGetValue<long>(out long l))
                return (int)l;
            if (value.TryGetValue<double>(out double d))
                return (int)d;
        }
        return 0;
    }
}
