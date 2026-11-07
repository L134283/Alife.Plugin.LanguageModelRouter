namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 供内容类型处理器使用的多模态执行器：回答"当前即将服务的渠道组是否能用该内容类型"。
/// 由 <see cref="LanguageModelRouter"/> 实现。
/// </summary>
/// <remarks>
/// 4.7.4 起对齐官方 4.5.0：不再提供"临时补全"接口——临时查看改为把媒体作为一条正常对话消息发出、
/// 由 AI 自身直接回答（见 <see cref="AlifeContentUtility"/>），避免二次转述失真并省 token。
/// </remarks>
public interface IMultimodalExecutor
{
    /// <summary>
    /// 当前渠道组能否使用该协议类型的内容（input_audio 看"音频输入"，其余看"原生多模态"）。
    /// 可用返回 null；不可用返回给用户看的原因（未开启 / 刚被上游拒绝的冷却期）。
    /// </summary>
    string? GetContentBlockReason(string protocolTypeName);
}
