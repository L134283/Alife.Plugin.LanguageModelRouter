using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>文件内容：协议序列化为 <c>file</c>。AI 通过 <c>LookFile</c> 查看，用 keep 参数选择临时/保留。
/// 写法对齐官方 Alife.Function.Language.OpenAI 插件的 FileUrlContentType。</summary>
public sealed class FileUrlContent(Uri url) : KernelContent
{
    public Uri Url { get; } = url;
}

public sealed class FileUrlContentType : AlifeContentHandlerBase
{
    public override Type ContentType => typeof(FileUrlContent);
    public override string DisplayName => "文件输入";
    public override string ProtocolTypeName => "file";

    public override JsonObject SerializeContent(KernelContent content)
    {
        FileUrlContent file = (FileUrlContent)content;
        return new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["file_url"] = file.Url.ToString() } };
    }

    public override XmlFunction? CreateXmlFunction(ChatBot chatBot, LanguageModelRouterConfig config, IMultimodalExecutor executor)
    {
        return BuildXmlFunction(
            "LookFile",
            "一般仅支持部分文件类型，如PDF等常见文本文件。",
            [
                ("url", "可直链访问的网络地址", "String"),
                ("keep", "是否常驻上下文以便连续分析，默认 true", "bool"),
            ],
            async (context, ct) => {
                FileUrlContent file = new(RequireHttpUrl(context.Parameters["url"], "url"));
                bool persistent = IsPersistentRequested(context);
                if (persistent)
                {
                    if (executor.IsPersistentAllowed(RegistrationKey!) == false)
                    {
                        chatBot.Poke("保留模式未授权，仅可使用 keep=false 临时查看。");
                        return;
                    }
                    await QueueContentAsync(chatBot, file, "将文件加入对话上下文");
                    chatBot.Poke("已上传");
                    return;
                }
                if (executor.CanUseNativeMultimodalNow() == false)
                {
                    chatBot.Poke("当前渠道组未开启「原生多模态」，无法临时查看文件。请在对应组的配置中开启，或改用 keep=true 加入上下文。");
                    return;
                }
                try
                {
                    string result = await executor.CompleteWithContentAsync(chatBot, file, ct);
                    chatBot.Poke(string.IsNullOrWhiteSpace(result) ? "未能获取文件内容。" : result);
                }
                catch (Exception e)
                {
                    chatBot.Poke($"文件查看失败：{e.Message}");
                }
            });
    }
}
