using System;
using System.Text.Json.Nodes;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 内容类型处理器基类：约定"一种内容类型 = 一个实现脚本"，自带协议序列化与 AI 调用注册函数。
/// 新增内容类型只需新建脚本继承本类，即可被 <see cref="AlifeContentRegistry"/> 自动发现并参与序列化与函数注册。
/// 共享工具（网址下载、统一 Load 函数构建、参数解析）见 <see cref="AlifeContentUtility"/>。
/// </summary>
public abstract class AlifeContentHandlerBase : IAlifeContentType
{
    public abstract Type ContentType { get; }

    /// <summary>默认以协议 content type 名作为注册标识，与面板展示一致，用户按模型服务商支持范围勾选。</summary>
    public virtual string? RegistrationKey => ProtocolTypeName;

    public abstract string DisplayName { get; }
    public abstract string ProtocolTypeName { get; }
    public abstract JsonObject SerializeContent(KernelContent content);
    public abstract XmlFunction? CreateXmlFunction(ChatBot chatBot, LanguageModelRouterConfig config, IMultimodalExecutor executor);
}
