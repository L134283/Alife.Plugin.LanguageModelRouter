using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 命令执行提示：桌面走对话气泡，QQ 环境由插件直接发一条 QQ 消息
/// （私聊发私聊，群聊发触发命令的那个群）。
/// </summary>
/// <remarks>
/// QQ 发送通过反射调用 Alife.Function.QChat 的 OneBot 客户端完成：
/// ① 不引入对 QQ 聊天插件的编译期依赖，未安装该插件时自动降级为仅桌面提示；
/// ② 任何失败都只写日志，绝不影响命令本身的效果。
/// </remarks>
public partial class LanguageModelRouter
{
    /// <summary>提示目标：非 QQ / 私聊 / 群聊 + 目标号。</summary>
    internal readonly record struct CommandNotice(bool IsQq, bool IsGroup, long TargetId);

    /// <summary>QQ 插件配置里的提示词属性名（追加在消息末尾，用户可自定义任意形状）。</summary>
    static readonly string[] QqPromptPropertyNames =
    [
        "AppendPrivateChatPrompt",
        "AppendGroupChatPrompt",
        "AppendDocumentPrompt"
    ];

    /// <summary>QQ 提示词原文缓存（配置改了也能在 1 分钟内跟上）。</summary>
    string[]? qqWrapperTextsCache;
    DateTime qqWrapperTextsCacheTime;

    /// <summary>待发送的命令提示目标（命令轮串行执行，单字段即可）。</summary>
    CommandNotice? pendingCommandNotice;

    /// <summary>待发送提示对应的动作。</summary>
    CommandAction pendingNoticeAction;

    /// <summary>从原始消息（改写前）解析提示目标：QQ 私聊 / QQ 群聊 / 桌面。</summary>
    static CommandNotice ParseCommandNotice(string? rawMessage)
    {
        CommandParser.SourceTarget target = CommandParser.ParseSourceTarget(rawMessage);
        return new CommandNotice(target.IsQq, target.IsGroup, target.Id);
    }

    void SetCommandNotice(CommandNotice notice, CommandAction action)
    {
        pendingCommandNotice = notice;
        pendingNoticeAction = action;
    }

    CommandNotice? TakeCommandNotice(out CommandAction action)
    {
        action = pendingNoticeAction;
        CommandNotice? notice = pendingCommandNotice;
        pendingCommandNotice = null;
        return notice;
    }

    void ClearCommandNotice() => pendingCommandNotice = null;

    /// <summary>提示文案。</summary>
    static string GetCommandNoticeText(CommandAction action)
        => action == CommandAction.Regenerate ? "✅已重新生成" : "✅已删除最新一轮对话";

    /// <summary>
    /// 发射提示：桌面（对话气泡，仅界面提示、不进上下文）+ QQ（插件直接发送）。
    /// </summary>
    Task EmitCommandNoticeAsync(CommandNotice notice, CommandAction action, Action<string>? textReceived)
    {
        LanguageModelRouterConfig? cfg = Configuration;
        if (cfg != null && cfg.CommandEcho == false)
            return Task.CompletedTask;

        string text = GetCommandNoticeText(action);
        Console.WriteLine($"[灵枢] 命令提示：{text}（{(notice.IsQq ? (notice.IsGroup ? "QQ 群聊" : "QQ 私聊") : "桌面")}）");

        // 桌面：作为本轮输出的一部分显示（不会写入对话历史）
        textReceived?.Invoke(action == CommandAction.Regenerate ? text + "\n" : text);

        // QQ：私聊发私聊，群聊发当前群聊。不 await，避免网络写入拖住本轮对话
        if (notice.IsQq)
            _ = TrySendQqNoticeAsync(notice, text);

        return Task.CompletedTask;
    }

    /// <summary>反射找到 Alife.Function.QChat 的 QChatService 实例（未安装返回 null）。</summary>
    object? FindQqService()
    {
        try
        {
            return ChatActivity?.Container?.Instances
                .FirstOrDefault(instance => instance.GetType().FullName == "Alife.Function.QChat.QChatService");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 取 QQ 插件当前配置的提示词原文（私聊/群聊/文档）。
    /// 这些提示词会被 QQ 插件原样追加到消息末尾，用户可写成任意形状（括号、引号、【】或纯文本），
    /// 因此按"原文精确剔除"处理，不依赖形状推断。
    /// </summary>
    IReadOnlyList<string> GetQqWrapperTexts()
    {
        if (qqWrapperTextsCache != null && (DateTime.Now - qqWrapperTextsCacheTime).TotalSeconds < 60)
            return qqWrapperTextsCache;

        List<string> texts = new();
        try
        {
            object? service = FindQqService();
            object? config = service?.GetType().GetProperty("Configuration")?.GetValue(service);
            if (config != null)
            {
                foreach (string name in QqPromptPropertyNames)
                {
                    if (config.GetType().GetProperty(name)?.GetValue(config) is string text
                        && string.IsNullOrWhiteSpace(text) == false)
                    {
                        texts.Add(text);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[灵枢] 读取 QQ 提示词失败（不影响命令判定）：{e.Message}");
        }

        qqWrapperTextsCache = texts.ToArray();
        qqWrapperTextsCacheTime = DateTime.Now;
        return qqWrapperTextsCache;
    }

    /// <summary>
    /// 通过 Alife.Function.QChat 的 OneBot 客户端发送一条文本消息。
    /// 反射查找，找不到/失败都只记日志（不影响命令效果）。
    /// </summary>
    async Task TrySendQqNoticeAsync(CommandNotice notice, string text)
    {
        if (notice.TargetId <= 0)
            return;

        try
        {
            object? service = FindQqService();
            if (service == null)
            {
                Console.WriteLine("[灵枢] 未找到「QQ聊天」模块，命令提示只显示在对话看板");
                return;
            }

            object? client = null;
            foreach (FieldInfo field in service.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.Name == "OneBotClient")
                {
                    client = field.GetValue(service);
                    break;
                }
            }

            if (client == null)
            {
                Console.WriteLine("[灵枢] 未找到 QQ 客户端实例，命令提示只显示在对话看板");
                return;
            }

            MethodInfo? send = client.GetType().GetMethod(
                "SendActionAsync", [typeof(string), typeof(object), typeof(string)]);
            if (send == null)
            {
                Console.WriteLine("[灵枢] QQ 客户端接口不匹配，命令提示只显示在对话看板");
                return;
            }

            Dictionary<string, object?> parameters = notice.IsGroup
                ? new Dictionary<string, object?> { ["group_id"] = notice.TargetId, ["message"] = text }
                : new Dictionary<string, object?> { ["user_id"] = notice.TargetId, ["message"] = text };

            string action = notice.IsGroup ? "send_group_msg" : "send_private_msg";

            if (send.Invoke(client, [action, parameters, null]) is Task task)
                await task;

            Console.WriteLine($"[灵枢] 已发送命令提示到{(notice.IsGroup ? "群" : "私聊")} {notice.TargetId}：{text}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[灵枢] QQ 命令提示发送失败（不影响命令执行）：{e.Message}");
        }
    }
}
