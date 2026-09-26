namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 供内容类型处理器使用的多模态执行器：回答"当前即将服务的渠道组是否开启该内容类型"。
/// 由 <see cref="LanguageModelRouter"/> 实现。
/// </summary>
/// <remarks>
/// 4.7.4 起对齐官方 4.5.0：不再提供"临时补全"接口——临时查看改为把媒体作为一条正常对话消息发出、
/// 由 AI 自身直接回答（见 <see cref="AlifeContentUtility"/>），避免二次转述失真并省 token。
/// </remarks>
public interface IMultimodalExecutor
{
    /// <summary>
    /// 当前即将服务请求的渠道组是否开启了该内容类型对应的原生多模态
    /// （input_audio 看"音频输入"开关，其余看"原生多模态"总开关）。
    /// </summary>
    bool CanUseContentType(string protocolTypeName);
}
