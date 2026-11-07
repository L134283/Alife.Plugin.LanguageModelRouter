using System;
using System.IO;
using System.Text.Json.Nodes;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>视频内容：协议序列化为 <c>video_url</c>。AI 通过 <c>LoadVideo</c> 查看，用 persistent 参数选择常驻/临时。
/// MIME 按文件头嗅探（无扩展名的下载链不再退化成 octet-stream）。</summary>
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
        return AlifeContentUtility.BuildXmlFunction("LoadVideo", ProtocolTypeName, VideoFileToContent, chatBot, executor, config);
    }

    static KernelContent VideoFileToContent(string file, bool persistent)
    {
        byte[] data = File.ReadAllBytes(file);
        string mimeType = MediaSniffer.ResolveVideoMime(file, data);
        return new VideoUrlContent($"data:{mimeType};base64,{Convert.ToBase64String(data)}")
        {
            Metadata = persistent ? MediaHygiene.PersistentMetadata : MediaHygiene.TemporaryMetadata
        };
    }
}
