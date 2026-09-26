using System;
using System.IO;
using System.Text.Json.Nodes;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>视频内容：协议序列化为 <c>video_url</c>。AI 通过 <c>LoadVideo</c> 查看，用 persistent 参数选择常驻/临时。
/// 写法对齐官方 Alife.Function.Language.OpenAI 4.5.0 的 VideoContentRegistrar。</summary>
public sealed class VideoUrlContent(string url) : KernelContent
{
    /// <summary>network地址在上传前会先下载并固化为 data URI（base64）。</summary>
    public string Url { get; } = url;
}

public sealed class VideoUrlContentType : AlifeContentHandlerBase
{
    public override Type ContentType => typeof(VideoUrlContent);
    public override string DisplayName => "视频输入";
    public override string ProtocolTypeName => "video_url";

    public override JsonObject SerializeContent(KernelContent content)
    {
        VideoUrlContent video = (VideoUrlContent)content;
        return new JsonObject { ["type"] = "video_url", ["video_url"] = new JsonObject { ["url"] = video.Url } };
    }

    public override XmlFunction? CreateXmlFunction(ChatBot chatBot, LanguageModelRouterConfig config, IMultimodalExecutor executor)
    {
        return AlifeContentUtility.BuildXmlFunction("LoadVideo", ProtocolTypeName, VideoFileToContent, chatBot, executor);
    }

    static KernelContent VideoFileToContent(string file)
    {
        byte[] data = File.ReadAllBytes(file);
        return new VideoUrlContent($"data:{GetMimeType(file)};base64,{Convert.ToBase64String(data)}");
    }

    static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        _ => "application/octet-stream"
    };
}
