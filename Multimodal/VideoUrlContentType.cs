using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>视频内容：协议序列化为 <c>video_url</c>。AI 通过 <c>LookVideo</c> 查看，用 keep 参数选择临时/保留。
/// 写法对齐官方 Alife.Function.Language.OpenAI 插件的 VideoUrlContentType。</summary>
public sealed class VideoUrlContent(Uri url) : KernelContent
{
    public Uri Url { get; } = url;
}

public sealed class VideoUrlContentType : AlifeContentHandlerBase
{
    public override Type ContentType => typeof(VideoUrlContent);
    public override string DisplayName => "视频输入";
    public override string ProtocolTypeName => "video_url";

    public override JsonObject SerializeContent(KernelContent content)
    {
        VideoUrlContent video = (VideoUrlContent)content;
        return new JsonObject { ["type"] = "video_url", ["video_url"] = new JsonObject { ["url"] = video.Url.ToString() } };
    }

    public override XmlFunction? CreateXmlFunction(ChatBot chatBot, LanguageModelRouterConfig config, IMultimodalExecutor executor)
    {
        return BuildXmlFunction(
            "LookVideo",
            null,
            [
                ("url", "可直链访问的网络地址", "String"),
                ("keep", "是否常驻上下文以便连续分析，默认 true", "bool"),
            ],
            async (context, ct) => {
                VideoUrlContent video = new(RequireHttpUrl(context.Parameters["url"], "url"));
                bool persistent = IsPersistentRequested(context);
                if (persistent)
                {
                    if (executor.IsPersistentAllowed(RegistrationKey!) == false)
                    {
                        chatBot.Poke("保留模式未授权，仅可使用 keep=false 临时查看。");
                        return;
                    }
                    await QueueContentAsync(chatBot, video, "将视频加入对话上下文");
                    chatBot.Poke("已上传");
                    return;
                }
                if (executor.CanUseNativeMultimodalNow() == false)
                {
                    chatBot.Poke("当前渠道组未开启「原生多模态」，无法临时查看视频。请在对应组的配置中开启，或改用 keep=true 加入上下文。");
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
}
