using System.Collections.Generic;
using System.Text.Json.Nodes;
using Alife.Framework;
using Microsoft.SemanticKernel;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 灵枢侧的多模态对话协议序列化层（仅做媒体消息的 content parts 序列化，文本仍走原字符串通道以保持最大兼容）。
/// 逻辑对齐官方 OpenAI 语言模型的"原生 content parts"写法：把 SK 消息 Items 中的
/// ImageContent / FileUrlContent / VideoUrlContent 等转成 OpenAI 兼容的 content 数组，
/// 纯文本消息保持原有字符串序列化，避免给纯文本渠道带来不必要的行为变化。
/// </summary>
public static class NativeMultimodalProtocol
{
    /// <summary>
    /// 尝试把一条 SK 消息序列化为"含媒体"的 OpenAI 原生 content parts（JsonArray）。
    /// 仅当消息确实携带媒体内容时返回数组；纯文本消息返回 null（调用方回退原文本字符串）。
    /// 文本部分会顺带清理 __THINK__ 思维链前缀（与主对话序列化逻辑一致，避免污染上游上下文）。
    /// </summary>
    public static JsonArray? TrySerializeWithMedia(ChatMessageContent message)
    {
        JsonArray parts = new();
        bool hasMedia = false;

        foreach (KernelContent item in message.Items)
        {
            if (item is TextContent text)
            {
                string s = text.Text ?? "";
                if (s.StartsWith(LanguageModelRouter.ThinkContentPrefix, System.StringComparison.Ordinal))
                    s = s.Substring(LanguageModelRouter.ThinkContentPrefix.Length);
                if (s.Length == 0)
                    continue;
                parts.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = s
                });
                continue;
            }

            JsonObject? media = TrySerializeMedia(item);
            if (media == null)
                continue;
            parts.Add(media);
            hasMedia = true;
        }

        return hasMedia ? parts : null;
    }

    /// <summary>通过内容类型注册表序列化媒体块；未注册/不支持的类型返回 null（直接忽略，避免拖垮请求）。</summary>
    static JsonObject? TrySerializeMedia(KernelContent content)
    {
        foreach (IAlifeContentType handler in AlifeContentRegistry.Handlers)
        {
            if (handler.ContentType.IsInstanceOfType(content) == false)
                continue;
            try
            {
                return handler.SerializeContent(content);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 纯文本序列化场景下的媒体占位回退：消息携带媒体且没有任何真实文本时，返回占位标记
    /// （如 [图片]），避免把 ImageContent 之类的内部类型名发给上游；
    /// 存在真实文本或根本没有媒体时返回 null（沿用原逻辑）。
    /// plainText 为已去除 __THINK__ 前缀的 msg.Content。
    /// </summary>
    public static string? MediaTextPlaceholder(ChatMessageContent message, string? plainText)
    {
        string? placeholder = null;

        foreach (KernelContent item in message.Items)
        {
            if (item is TextContent text)
            {
                string s = text.Text ?? "";
                if (s.StartsWith(LanguageModelRouter.ThinkContentPrefix, System.StringComparison.Ordinal))
                    s = s.Substring(LanguageModelRouter.ThinkContentPrefix.Length);
                if (string.IsNullOrWhiteSpace(s) == false)
                    return null; // Items 中存在真实文本，无需占位
                continue;
            }
            if (placeholder == null)
                placeholder = item switch
                {
                    ImageContent => "[图片]",
                    FileUrlContent => "[文件]",
                    VideoUrlContent => "[视频]",
                    _ => "（媒体内容）"
                };
        }

        if (placeholder == null)
            return null; // 没有媒体，维持原文本逻辑

        // Items 无文本，但消息显式 Content 若为真实文本（非媒体内部类型名），应保留而非用占位覆盖
        if (string.IsNullOrWhiteSpace(plainText) == false
            && plainText != "ImageContent"
            && plainText != "FileUrlContent"
            && plainText != "VideoUrlContent")
            return null;

        return placeholder;
    }
}
