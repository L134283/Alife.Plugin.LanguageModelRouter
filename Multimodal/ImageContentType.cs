using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>图片内容：协议序列化为 <c>image_url</c>。AI 通过 <c>LoadImage</c> 查看，用 temp 参数选择临时/保留。
/// 写法对齐官方 Alife.Function.Language.OpenAI 插件的 ImageContentType。</summary>
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
        return BuildXmlFunction(
            "LoadImage",
            null,
            [
                ("path", "图片本机路径或 http(s) 地址", "String"),
                ("temp", "临时分析并直接获取结果，默认false", "bool"),
            ],
            async (context, ct) => {
                ImageContent image = await LoadImageAsync(context.Parameters["path"]);
                bool persistent = IsPersistentRequested(context);
                if (persistent)
                {
                    // 保留模式：媒体常驻上下文，后续请求由实际服务的渠道组按其"原生多模态"开关决定是否原生携带
                    if (executor.IsPersistentAllowed(RegistrationKey!) == false)
                    {
                        chatBot.Poke("保留模式未授权，仅可使用 temp=true 临时查看。");
                        return;
                    }
                    await QueueContentAsync(chatBot, image, "将图片加入对话上下文");
                    chatBot.Poke("已上传");
                    return;
                }
                // 临时模式：单次补全必须由当前渠道组原生携带图片，否则模型看不到内容
                if (executor.CanUseNativeMultimodalNow() == false)
                {
                    chatBot.Poke("当前渠道组未开启「原生多模态」，无法临时查看图片。请在对应组的配置中开启，或改用 temp=false 加入上下文。");
                    return;
                }
                try
                {
                    string result = await executor.CompleteWithContentAsync(chatBot, image, ct);
                    chatBot.Poke(string.IsNullOrWhiteSpace(result) ? "未能获取图片内容。" : result);
                }
                catch (Exception e)
                {
                    chatBot.Poke($"图片查看失败：{e.Message}");
                }
            });
    }

    static async Task<ImageContent> LoadImageAsync(string pathOrUrl)
    {
        ImageContent image;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            image = new ImageContent(uri);
        }
        else
        {
            if (File.Exists(pathOrUrl) == false)
                throw new FileNotFoundException("图片不存在", pathOrUrl);

            image = new ImageContent(File.ReadAllBytes(pathOrUrl), GetMimeType(pathOrUrl));
        }
        return image;
    }

    static string GetImageUrl(ImageContent image)
    {
        if (image.Uri is not null)
            return image.Uri.ToString();
        if (image.DataUri is not null)
            return image.DataUri;
        if (image.Data is { } data && data.IsEmpty == false)
            return $"data:{image.MimeType ?? "application/octet-stream"};base64,{Convert.ToBase64String(data.Span)}";
        throw new NotSupportedException("ImageContent 必须包含 URL、data URI 或二进制数据。");
    }

    static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
