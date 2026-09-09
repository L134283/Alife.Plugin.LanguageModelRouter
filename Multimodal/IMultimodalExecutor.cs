using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 供内容类型处理器使用的多模态执行器：查询保留模式授权与当前渠道是否可用原生多模态，
/// 以及执行临时式的"带上下文单次补全"，把文本结果返回给 AI。
/// 由 <see cref="LanguageModelRouter"/> 实现。
/// 写法对齐官方 Alife.Function.Language.OpenAI 插件的 IMultimodalExecutor。
/// </summary>
public interface IMultimodalExecutor
{
    /// <summary>查询指定内容类型是否被授权使用保留模式（媒体永久加入上下文）。</summary>
    bool IsPersistentAllowed(string registrationKey);

    /// <summary>当前即将服务请求的渠道组是否开启了原生多模态（临时式补全依赖它真正把媒体发给模型）。</summary>
    bool CanUseNativeMultimodalNow();

    /// <summary>
    /// 临时式补全：在锁内临时把媒体追加到对话历史，复用 <see cref="LanguageModelRouter.ChatStreamingAsync"/>
    /// 补全后返回纯文本结果，随后移除临时消息，不污染主历史。
    /// </summary>
    Task<string> CompleteWithContentAsync(ChatBot chatBot, KernelContent content, CancellationToken cancellationToken = default);
}
