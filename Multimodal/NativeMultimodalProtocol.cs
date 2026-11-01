using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 灵枢侧的多模态对话协议序列化层：把 SK 消息 Items 中的媒体内容转成 OpenAI 兼容的 content parts。
/// 与官方 OpenAI 语言模型的差异：媒体是否保留由"本次请求的目标渠道组"的两个开关决定
/// （image_url / video_url 看「原生多模态」，input_audio 看「音频输入」），
/// 因此可以在同一次对话里按组切换，而不需要事后重写请求体。
/// </summary>
public static class NativeMultimodalProtocol
{
    /// <summary>
    /// 尝试把一条消息序列化为"含媒体"的 content parts 数组。
    /// 仅当消息确实携带（且本组允许的）媒体时返回数组；否则返回 null，调用方回退到纯文本通道。
    /// 文本部分会清理旧版思维链前缀，避免污染上游上下文。
    /// </summary>
    public static JsonArray? TrySerializeWithMedia(
        ChatMessageContent message, bool allowImageVideo, bool allowAudio)
    {
        JsonArray parts = new();
        bool hasMedia = false;
        bool hasTextPart = false;

        foreach (KernelContent item in message.Items)
        {
            if (item is TextContent text)
            {
                string value = OpenAIChatProtocol.StripThinkPrefix(text.Text);
                if (value.Length == 0)
                    continue;
                parts.Add(new JsonObject {
                    ["type"] = "text",
                    ["text"] = value
                });
                hasTextPart = true;
                continue;
            }

            IAlifeContentType? handler = AlifeContentRegistry.Find(item);
            if (handler == null || IsAllowed(handler.ProtocolTypeName, allowImageVideo, allowAudio) == false)
                continue;

            try
            {
                parts.Add(handler.SerializeContent(item));
                hasMedia = true;
            }
            catch
            {
                // 单条内容序列化失败（如媒体数据损坏）不应拖垮整轮请求
            }
        }

        if (hasMedia == false)
            return null;

        // 媒体常以 Items 承载、文字放在 Content 上（如 ChatMessageContent(role, [content])），
        // 此时 Items 里没有 TextContent，需要把 Content 补成文本 part，否则指令文字会被丢掉
        if (hasTextPart == false && TryGetRealText(message.Content) is { } contentText)
        {
            parts.Insert(0, new JsonObject {
                ["type"] = "text",
                ["text"] = contentText
            });
        }

        return parts;
    }

    /// <summary>剔除媒体占位标记后的真实文本（没有则返回 null）。</summary>
    static string? TryGetRealText(string? text)
    {
        string value = OpenAIChatProtocol.StripThinkPrefix(text);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value == "ImageContent" || value == "VideoUrlContent" || value == "AudioContent")
            return null;
        if (value.StartsWith("[多模态内容(", System.StringComparison.Ordinal))
            return null;
        return value;
    }

    /// <summary>
    /// 纯文本通道下的媒体占位回退：消息携带媒体但没有任何真实文本时，返回占位标记（如 [图片]），
    /// 避免把 ImageContent 之类的内部类型名发给上游。存在真实文本或没有媒体时返回 null。
    /// </summary>
    public static string? MediaTextPlaceholder(ChatMessageContent message, string? plainText)
    {
        string? placeholder = null;

        foreach (KernelContent item in message.Items)
        {
            if (item is TextContent text)
            {
                if (string.IsNullOrWhiteSpace(OpenAIChatProtocol.StripThinkPrefix(text.Text)) == false)
                    return null; // 已有真实文本，无需占位
                continue;
            }

            placeholder ??= item switch
            {
                ImageContent => "[图片]",
                VideoUrlContent => "[视频]",
                AudioContent => "[音频]",
                _ => "（媒体内容）"
            };
        }

        if (placeholder == null)
            return null; // 没有媒体，维持原文本逻辑

        // Items 无文本，但消息显式 Content 若为真实文本（非媒体内部类型名/多模态标记），应保留而非用占位覆盖
        if (string.IsNullOrWhiteSpace(plainText) == false
            && plainText != "ImageContent"
            && plainText != "VideoUrlContent"
            && plainText != "AudioContent"
            && plainText.StartsWith("[多模态内容(", System.StringComparison.Ordinal) == false)
            return null;

        return placeholder;
    }

    /// <summary>按协议名判断该媒体类型在目标组是否被允许：音频独立开关，其余看原生多模态总开关。</summary>
    public static bool IsAllowed(string protocolTypeName, bool allowImageVideo, bool allowAudio)
    {
        return string.Equals(protocolTypeName, "input_audio", System.StringComparison.OrdinalIgnoreCase)
            ? allowAudio
            : allowImageVideo;
    }
}
