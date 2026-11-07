using System;
using System.IO;
using System.Text.Json.Nodes;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>图片内容：协议序列化为 <c>image_url</c>。AI 通过 <c>LoadImage</c> 查看，用 persistent 参数选择常驻/临时。
/// MIME 按文件头嗅探（对齐官方 ImageContentRegistrar）：QQ 等无扩展名下载链不会退化成 octet-stream 被上游拒收。</summary>
public sealed class ImageContentType : AlifeContentHandlerBase
{
    public override Type ContentType => typeof(ImageContent);
    public override string DisplayName => "图片输入";
    public override string ProtocolTypeName => "image_url";

    public override JsonObject SerializeContent(KernelContent content)
    {
        ImageContent image = (ImageContent)content;
        return new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = GetImageUrl(image) } };
    }

    public override XmlFunction? CreateXmlFunction(ChatBot chatBot, LanguageModelRouterConfig config, IMultimodalExecutor executor)
    {
        return AlifeContentUtility.BuildXmlFunction("LoadImage", ProtocolTypeName, ImageFileToContent, chatBot, executor, config);
    }

    static KernelContent ImageFileToContent(string file, bool persistent)
    {
        byte[] bytes = File.ReadAllBytes(file);
        string mimeType = MediaSniffer.ResolveImageMime(file, bytes);
        return new ImageContent(bytes, mimeType)
        {
            Metadata = persistent ? MediaHygiene.PersistentMetadata : MediaHygiene.TemporaryMetadata
        };
    }

    static string GetImageUrl(ImageContent image)
    {
        if (image.Uri is not null)
            return image.Uri.ToString();
        if (image.DataUri is not null)
            return image.DataUri;
        if (image.Data is { } data && data.IsEmpty == false)
            return $"data:{image.MimeType ?? "image/jpeg"};base64,{Convert.ToBase64String(data.Span)}";
        throw new NotSupportedException("ImageContent 必须包含 URL、data URI 或二进制数据。");
    }
}
