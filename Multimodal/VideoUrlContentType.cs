using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>视频内容：协议序列化为 <c>video_url</c>。AI 通过 <c>LoadVideo</c> 查看，用 temp 参数选择临时/保留。
/// 写法对齐官方 Alife.Function.Language.OpenAI 插件的 VideoUrlContentType。</summary>
public sealed class VideoUrlContent(string url) : KernelContent
{
    /// <summary>网络地址或 data URI（本地文件读取后转base64）。</summary>
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
        return BuildXmlFunction(
            "LoadVideo",
            null,
            [
                ("path", "视频本机路径或可直链访问的网络地址", "String"),
                ("temp", "临时分析并直接获取结果，默认false", "bool"),
            ],
            async (context, ct) => {
                // 兼容旧参数名 url（新版已对齐官方改名 path），避免 AI 沿用旧写法时报 key not present
                string? pathOrUrl = GetParameter(context, "path", "url");
                if (string.IsNullOrWhiteSpace(pathOrUrl))
                {
                    chatBot.Poke("请提供视频的本机路径或可直链访问的网络地址。");
                    return;
                }
                VideoUrlContent video = new(await LoadVideoUrlAsync(pathOrUrl));
                bool persistent = IsPersistentRequested(context);
                if (persistent)
                {
                    if (executor.IsPersistentAllowed(RegistrationKey!) == false)
                    {
                        chatBot.Poke("保留模式未授权，仅可使用 temp=true 临时查看。");
                        return;
                    }
                    await QueueContentAsync(chatBot, video, "将视频加入对话上下文");
                    chatBot.Poke("已上传");
                    return;
                }
                if (executor.CanUseNativeMultimodalNow() == false)
                {
                    chatBot.Poke("当前渠道组未开启「原生多模态」，无法临时查看视频。请在对应组的配置中开启，或改用 temp=false 加入上下文。");
                    return;
                }
                try
                {
                    string result = await executor.CompleteWithContentAsync(chatBot, video, ct);
                    chatBot.Poke(string.IsNullOrWhiteSpace(result) ? "未能获取视频内容。" : result);
                }
                catch (Exception e)
                {
                    chatBot.Poke($"视频查看失败：{e.Message}");
                }
            });
    }

    /// <summary>把参数解析为视频地址：http(s) 直链原样返回；本地路径读取后转 base64 data URI。</summary>
    static async Task<string> LoadVideoUrlAsync(string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.ToString();

        if (File.Exists(pathOrUrl) == false)
            throw new FileNotFoundException($"本地视频文件不存在：{pathOrUrl}。若为远端视频，请改传可直链访问的 http(s) 网络地址。", pathOrUrl);

        byte[] data = await File.ReadAllBytesAsync(pathOrUrl);
        return $"data:{GetMimeType(pathOrUrl)};base64,{Convert.ToBase64String(data)}";
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
