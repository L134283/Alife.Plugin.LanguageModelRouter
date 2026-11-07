using System;
using System.Collections.Generic;
using Alife.Framework;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 媒体"该不该随请求发送"的判定层。
/// 设计原则（重要）：**只读判定，绝不改写历史**。
/// 记忆压缩会用一批与真实上下文共享消息对象的快照调用语言模型，任何就地改写都会伤到真实对话与记忆，
/// 因此"过期媒体降级为占位文本"放在序列化阶段完成（见 <see cref="NativeMultimodalProtocol"/>）。
/// </summary>
public static class MediaHygiene
{
    /// <summary>常驻标记的元数据键。</summary>
    public const string PersistentKey = "lmsr.persistent";

    /// <summary>常驻媒体的元数据（构造内容时通过 Metadata 初始化器写入，插件重载后依然有效）。</summary>
    public static readonly IReadOnlyDictionary<string, object?> PersistentMetadata =
        new Dictionary<string, object?>(StringComparer.Ordinal) { [PersistentKey] = true };

    /// <summary>临时媒体的元数据（空）。</summary>
    public static readonly IReadOnlyDictionary<string, object?> TemporaryMetadata =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>该内容是否被标记为常驻上下文。</summary>
    public static bool IsPersistent(KernelContent content)
    {
        return content?.Metadata is { } metadata
            && metadata.TryGetValue(PersistentKey, out object? value)
            && value is bool persistent && persistent;
    }

    /// <summary>该内容是否为媒体（图片/视频/音频），文本内容返回 false。</summary>
    public static bool IsMedia(KernelContent content)
    {
        if (content == null)
            return false;

        IAlifeContentType? handler = AlifeContentRegistry.Find(content);
        return handler != null
            && string.Equals(handler.ProtocolTypeName, "text", StringComparison.OrdinalIgnoreCase) == false;
    }

    /// <summary>媒体类型对应的可读占位名。</summary>
    public static string PlaceholderFor(KernelContent content)
    {
        return content switch
        {
            ImageContent => "[图片]",
            VideoUrlContent => "[视频]",
            AudioContent => "[音频]",
            _ => "（媒体内容）"
        };
    }

    /// <summary>统计历史里仍携带媒体的消息条数（仅用于日志/诊断）。</summary>
    public static int CountMediaMessages(ChatHistory history)
    {
        int count = 0;
        foreach (ChatMessageContent message in history)
        {
            foreach (KernelContent item in message.Items)
            {
                if (IsMedia(item))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }
}
