using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 多模态的"失败自愈"部分：识别上游拒绝媒体的错误、同组去图重试、以及该组短时冷却。
/// 目标是：任何一张图出问题，都只影响它自己那一轮，绝不能污染后续全部对话。
/// </summary>
public partial class LanguageModelRouter
{
    /// <summary>上游把"媒体不被接受"表达出来的常见特征（仅在请求确实带了媒体时才会用到）。</summary>
    static readonly string[] MediaRejectMarkers =
    [
        "image_url", "invalid image", "unsupported image", "not support image", "image is not supported",
        "vision", "multimodal", "input_audio", "video_url", "content type", "media type",
        "图片", "图像", "多模态", "音频输入", "音频格式", "不支持", "重开对话"
    ];

    /// <summary>上游"200 但正文其实是错误"的强特征（宽松匹配会误杀正常回复，所以只认这些固定说法）。</summary>
    static readonly string[] UpstreamErrorReplyMarkers =
    [
        "请重开对话", "请重新开对话", "重开对话后重试", "重开对话再试",
        "invalid image", "image_url is not supported", "does not support image",
        "不支持图片", "不支持图像", "不支持该图片", "图片格式不支持", "无法解析图片",
        "invalid_request_error"
    ];

    /// <summary>本轮请求体里是否真的带了媒体（用于识别"错误被当正文"的情况）。</summary>
    bool lastRequestIncludedMedia;

    /// <summary>各渠道组的媒体冷却截止时间（瞬态，随实例销毁复位，不写入配置）。</summary>
    readonly Dictionary<int, DateTime> mediaCooldownUntil = new();

    /// <summary>该组当前是否可以随请求发送媒体（开关 + 冷却）。</summary>
    bool IsMediaAllowedForGroup(FallbackGroup group)
    {
        if (group.EnableNativeMultimodal == false && group.EnableAudioMultimodal == false)
            return false;

        return IsMediaCoolingDown(group.Slot, out _) == false;
    }

    /// <summary>该组是否处于"刚被上游拒绝过媒体"的冷却期。</summary>
    bool IsMediaCoolingDown(int slot, out int remainMinutes)
    {
        remainMinutes = 0;
        if (mediaCooldownUntil.TryGetValue(slot, out DateTime until) == false)
            return false;

        TimeSpan remain = until - DateTime.Now;
        if (remain <= TimeSpan.Zero)
        {
            mediaCooldownUntil.Remove(slot);
            return false;
        }

        remainMinutes = Math.Max(1, (int)Math.Ceiling(remain.TotalMinutes));
        return true;
    }

    /// <summary>标记某组进入媒体冷却期。</summary>
    void MarkMediaCooldown(int slot, int minutes)
    {
        if (slot < 0 || minutes <= 0)
            return;

        mediaCooldownUntil[slot] = DateTime.Now.AddMinutes(Math.Min(minutes, 24 * 60));
    }

    /// <summary>该响应是否是"媒体被上游拒绝"。</summary>
    static bool IsMediaRejection(HttpStatusCode status, string? body)
    {
        int code = (int)status;
        if (code != 400 && code != 413 && code != 415 && code != 422 && code != 500)
            return false;

        if (string.IsNullOrEmpty(body))
            return false;

        foreach (string marker in MediaRejectMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 正文是否像上游的错误提示（用于"200 但正文是错误文案"）。
    /// 只在"本轮确实带了媒体"且文本很短时才会调用，避免误杀正常回复。
    /// </summary>
    static bool LooksLikeUpstreamErrorReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300)
            return false;

        foreach (string marker in UpstreamErrorReplyMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 媒体被拒后的本地处置：该组进入冷却期（之后的请求不再向它发送媒体）。
    /// 注意：完全不改写历史——常驻图片只是暂时不发，冷却结束后仍可继续使用，也绝不删除任何对话消息。
    /// </summary>
    void HandleMediaRejectedLocally(ChatHistory history, string reason)
    {
        int slot = LastServedGroupIndex;
        if (slot >= 0)
            MarkMediaCooldown(slot, Configuration?.MediaCooldownMinutes ?? 10);

        Console.WriteLine($"[灵枢] {reason}：已短时停用该组媒体输入（历史内容不改动，常驻图片冷却后仍可用）。");
    }

    /// <summary>媒体相关的日志统一前缀，便于在 alife.log 里检索。</summary>
    static void LogMedia(string message)
    {
        Console.WriteLine($"[灵枢] {message}");
    }

    /// <summary>调试日志：打印本次请求的媒体摘要（不打印 base64 本体，避免日志爆炸）。</summary>
    void LogPayloadSummary(FallbackGroup group, ChatHistory history, bool mediaIncluded, bool allowImageVideo, bool allowAudio)
    {
        StringBuilder sb = new();
        sb.Append($"[灵枢][诊断] 请求 → 第{group.Slot + 1}组 {group.Endpoint.Host} / {group.ModelId}");
        sb.Append($"; 消息 {history.Count} 条; 媒体 {(mediaIncluded ? "含" : "无")}");
        sb.Append($"(image_url={(allowImageVideo ? "开" : "关")}, input_audio={(allowAudio ? "开" : "关")})");

        if (mediaIncluded)
        {
            foreach (ChatMessageContent message in history)
            {
                foreach (KernelContent item in message.Items)
                {
                    if (MediaHygiene.IsMedia(item) == false)
                        continue;
                    sb.Append($"\n  · {MediaHygiene.PlaceholderFor(item)} {DescribeMedia(item)}");
                }
            }
        }

        Console.WriteLine(sb.ToString());
    }

    /// <summary>媒体的人类可读摘要（类型/大小/是否常驻），绝不输出 base64。</summary>
    static string DescribeMedia(KernelContent item)
    {
        return item switch
        {
            ImageContent image => $"{image.MimeType ?? "unknown"} {image.Data?.Length ?? 0}B" + (MediaHygiene.IsPersistent(image) ? "（常驻）" : ""),
            AudioContent audio => $"{audio.Format} {audio.Data.Length}B" + (MediaHygiene.IsPersistent(audio) ? "（常驻）" : ""),
            VideoUrlContent video => $"{video.Url.Length} chars" + (MediaHygiene.IsPersistent(video) ? "（常驻）" : ""),
            _ => item.GetType().Name
        };
    }
}
