using System;
using System.Text;
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
    /// <summary>历史里媒体消息自带的占位文本前缀（由 Load 系函数写入，不能当成正文发给上游）。</summary>
    const string MediaPlaceholderTag = "[多模态内容(";

    /// <summary>
    /// 尝试把一条消息序列化为"含媒体"的 content parts 数组。
    /// 仅当消息确实携带（且本组允许的）媒体时返回数组；否则返回 null，调用方回退到纯文本通道。
    /// 文本部分会清理旧版思维链前缀与媒体占位文本，避免污染上游上下文。
    /// <para>
    /// 媒体卫生规则（只读判定，不改写历史）：只有**最后一条消息**（本轮消息）或**带常驻标记**的媒体
    /// 才会随请求发送。这样即便某次临时媒体没能及时清理，也绝不会在之后每一轮里反复发送、反复被上游拒绝。
    /// </para>
    /// </summary>
    public static JsonArray? TrySerializeWithMedia(
        ChatMessageContent message, bool allowImageVideo, bool allowAudio, bool allowPersistentMedia, bool isLastMessage)
    {
        JsonArray parts = new();
        bool hasMedia = false;
        bool hasTextPart = false;

        foreach (KernelContent item in message.Items)
        {
            if (item is TextContent text)
            {
                string value = CleanText(text.Text);
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

            // 非本轮的历史媒体：只有"常驻"且面板允许常驻时才继续随请求发送
            if (isLastMessage == false
                && (allowPersistentMedia == false || MediaHygiene.IsPersistent(item) == false))
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

    /// <summary>清理文本：去旧版思维链前缀、去媒体占位行（如 [多模态内容(ImageContent)]）。</summary>
    static string CleanText(string? text)
    {
        return StripMediaPlaceholder(OpenAIChatProtocol.StripThinkPrefix(text)).Trim();
    }

    /// <summary>剔除媒体占位行后的真实文本（没有则返回 null）。</summary>
    static string? TryGetRealText(string? text)
    {
        string value = CleanText(text);
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
                if (string.IsNullOrWhiteSpace(CleanText(text.Text)) == false)
                    return null; // 已有真实文本，无需占位
                continue;
            }

            if (MediaHygiene.IsMedia(item))
                placeholder ??= MediaHygiene.PlaceholderFor(item);
        }

        if (placeholder == null)
            return null; // 没有媒体，维持原文本逻辑

        // Items 无文本，但消息显式 Content 若为真实文本，应保留而非用占位覆盖
        if (string.IsNullOrWhiteSpace(CleanText(plainText)) == false)
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

    /// <summary>
    /// 移除文本中的媒体占位行。
    /// Load 系函数会把消息 Content 写成 "[多模态内容(ImageContent)]"，且可能被时间戳等过滤器加了前缀行，
    /// 这些都不是给模型看的正文。
    /// </summary>
    public static string StripMediaPlaceholder(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        if (text.Contains(MediaPlaceholderTag, StringComparison.Ordinal) == false)
            return text;

        StringBuilder builder = new();
        foreach (string line in text.Split('\n'))
        {
            if (line.Contains(MediaPlaceholderTag, StringComparison.Ordinal))
                continue;
            builder.AppendLine(line);
        }
        return builder.ToString().TrimEnd('\r', '\n', ' ', '\t');
    }
}
