using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 自定义斜杠命令：/重新生成、/删除最新一轮对话。
/// 实现要点：
/// ① 拦截点是 ChatBot.ChatSend（消息进历史之前、且在历史锁之外），因此在里面同步改历史不会打乱框架的索引；
/// ② 命令轮不经过模型、不消耗 token，命令文本也不会留在上下文里；
/// ③ 长期记忆保护：只删普通对话消息，遇到 [记忆存档(...)] / System / Tool 一律整体放弃，宁可漏删不可误删。
/// </summary>
public partial class LanguageModelRouter
{
    /// <summary>单条命令最多允许删除的消息条数（硬上限，防止误删大段上下文）。</summary>
    const int MaxCommandDeleteMessages = 8;

    /// <summary>
    /// 命令轮的"占位输出"：只在 ChatResult 里返回，不写历史、不发给上游。
    /// 官方「消息过滤」插件的「调用丢失验证」规则要求输出里含 &lt;...&gt;，
    /// 用空的 &lt;!----&gt; 表示"本次不调用任何函数"，避免被判定格式错误后 Poke 触发多余的一轮对话。
    /// </summary>
    const string NoOpAIMessage = "<!---->";

    bool commandInterceptorSubscribed;

    /// <summary>待处理命令（0=无；CommandAction+1），由 ChatSend 设置、ChatStreamingAsync 取走。</summary>
    int pendingCommandRaw;

    // ──── 订阅 ────

    void SubscribeCommandInterceptor()
    {
        if (commandInterceptorSubscribed)
            return;

        ChatBot.ChatSend += OnChatSendCommandFilter;
        commandInterceptorSubscribed = true;
    }

    void UnsubscribeCommandInterceptor()
    {
        if (commandInterceptorSubscribed == false)
            return;

        try { ChatBot.ChatSend -= OnChatSendCommandFilter; } catch { }
        commandInterceptorSubscribed = false;
    }

    void SetPendingCommand(CommandAction action) => Interlocked.Exchange(ref pendingCommandRaw, (int)action + 1);

    CommandAction? TakePendingCommand()
    {
        int value = Interlocked.Exchange(ref pendingCommandRaw, 0);
        return value == 0 ? null : (CommandAction)(value - 1);
    }

    void ClearPendingCommand() => Interlocked.Exchange(ref pendingCommandRaw, 0);

    // ──── 拦截 ────

    /// <summary>
    /// ChatSend 过滤器：命中命令就改写这条消息的内容（或返回原命令文本），从来不让命令文本进入模型上下文。
    /// 任何异常都退化为"当普通文本处理"，绝不因为命令功能影响正常对话。
    /// </summary>
    string OnChatSendCommandFilter(string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
                return message;

            LanguageModelRouterConfig cfg = Configuration;
            if (cfg == null || cfg.EnableCustomCommands == false)
                return message;

            cfg.EnsureCommandRules();

            List<string> triggers = CollectTriggers(cfg);

            // 只有消息里出现斜杠才值得去读 QQ 提示词（反射有开销，普通聊天不走这条路）
            IReadOnlyList<string>? wrapperTexts =
                message.Contains('/') || message.Contains('／') ? GetQqWrapperTexts() : null;

            CommandParser.Result match = CommandParser.Find(message, triggers, wrapperTexts);
            if (match.Success == false || match.Trigger == null)
                return message;

            CommandRule? rule = FindRule(cfg, match.Trigger);
            if (rule == null)
                return message;

            if (IsCommandSenderAllowed(cfg, match.IsQqContext, match.SenderQq) == false)
                return message; // 非主人 / 未配置主人 QQ：当普通文本交给 AI，不执行

            if (ChatBot.IsChatHistoryOccupied)
            {
                Console.WriteLine("[灵枢] 命令已跳过：对话历史正被其它环节占用，请稍后再试");
                return message;
            }

            if (rule.Action == CommandAction.DeleteLastRound)
            {
                int removed = DeleteLastRound(out string? reason);
                if (removed <= 0)
                {
                    Console.WriteLine($"[灵枢] {rule.Trigger} 未执行：{reason}");
                    return message;
                }

                SetPendingCommand(CommandAction.DeleteLastRound);
                SetCommandNotice(ParseCommandNotice(message), CommandAction.DeleteLastRound);
                Console.WriteLine($"[灵枢] {rule.Trigger} 已从上下文删除最新一轮（{removed} 条消息；记忆存档未受影响）");
                return rule.Trigger; // 仅用于对话看板显示，随后在 ChatStreamingAsync 移除
            }

            string? resend = PrepareRegenerate(out string? why);
            if (resend == null)
            {
                Console.WriteLine($"[灵枢] {rule.Trigger} 未执行：{why}");
                return message;
            }

            SetCommandNotice(ParseCommandNotice(message), CommandAction.Regenerate);
            Console.WriteLine($"[灵枢] {rule.Trigger} 已删除上一轮回复，重新发送上一次的用户消息");
            return resend;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[灵枢] 命令处理失败，已按普通消息处理：{e.Message}");
            return message;
        }
    }

    static List<string> CollectTriggers(LanguageModelRouterConfig cfg)
    {
        List<string> triggers = new();
        foreach (CommandRule rule in cfg.CommandRules)
        {
            if (rule == null || rule.Enabled == false || string.IsNullOrWhiteSpace(rule.Trigger))
                continue;
            triggers.Add(rule.Trigger);
        }
        return triggers;
    }

    static CommandRule? FindRule(LanguageModelRouterConfig cfg, string trigger)
    {
        foreach (CommandRule rule in cfg.CommandRules)
        {
            if (rule == null || rule.Enabled == false)
                continue;
            if (string.Equals(rule.Trigger, trigger, StringComparison.OrdinalIgnoreCase))
                return rule;
        }
        return null;
    }

    // ──── 历史操作 ────

    /// <summary>
    /// /重新生成：删除尾部这一轮的 AI 回复 + 那条用户消息，并把用户消息文本返回给调用方重新发送。
    /// 失败返回 null（不修改历史）。
    /// </summary>
    string? PrepareRegenerate(out string? reason)
    {
        string? resend = null;
        string? failure = null;

        ChatBot.EditChatHistory(thread =>
        {
            ChatHistory history = thread.ChatHistory;
            if (history.Count < 2)
            {
                failure = "上下文太短，没有可重新生成的内容";
                return;
            }

            int index = history.Count - 1;
            while (index >= 0 && history[index].Role == AuthorRole.Assistant && IsProtectedMessage(history[index]) == false)
                index--;

            if (index < 0)
            {
                failure = "找不到可重发的用户消息";
                return;
            }

            if (history[index].Role != AuthorRole.User)
            {
                failure = "最近一轮不是普通对话（可能是记忆存档或系统消息），已取消以保护长期记忆";
                return;
            }

            if (IsProtectedMessage(history[index]))
            {
                failure = "最近的用户消息属于受保护内容，已取消以保护长期记忆";
                return;
            }

            string? text = history[index].Content;
            if (string.IsNullOrWhiteSpace(text))
            {
                failure = "最近一条用户消息内容为空（可能是媒体占位），无法重发";
                return;
            }

            if (text.Contains("[多模态内容(", StringComparison.Ordinal))
            {
                failure = "最近一轮只是媒体占位消息，没有可重发的文字";
                return;
            }

            if (history.Count - index > MaxCommandDeleteMessages)
            {
                failure = $"最近一轮包含 {history.Count - index} 条消息，超出安全上限，已取消";
                return;
            }

            resend = text;
            history.RemoveRange(index, history.Count - index);
        }, "灵枢：重新生成");

        reason = failure;
        return resend;
    }

    /// <summary>
    /// /删除最新一轮对话：删除尾部这一轮的 AI 回复 + 那条用户消息。返回删除条数（0=未执行）。
    /// </summary>
    int DeleteLastRound(out string? reason)
    {
        int removed = 0;
        string? failure = null;

        ChatBot.EditChatHistory(thread =>
        {
            ChatHistory history = thread.ChatHistory;
            if (history.Count < 2)
            {
                failure = "上下文太短，没有可删除的对话";
                return;
            }

            int index = history.Count - 1;
            while (index >= 0 && history[index].Role == AuthorRole.Assistant && IsProtectedMessage(history[index]) == false)
                index--;

            if (index < 0)
            {
                failure = "找不到可删除的对话";
                return;
            }

            if (history[index].Role != AuthorRole.User)
            {
                failure = "最近一轮不是普通对话（可能是记忆存档或系统消息），已取消以保护长期记忆";
                return;
            }

            if (IsProtectedMessage(history[index]))
            {
                failure = "最近一轮属于受保护内容，已取消以保护长期记忆";
                return;
            }

            int count = history.Count - index;
            if (count > MaxCommandDeleteMessages)
            {
                failure = $"最近一轮包含 {count} 条消息，超出安全上限，已取消";
                return;
            }

            history.RemoveRange(index, count);
            removed = count;
        }, "灵枢：删除最新一轮对话");

        reason = failure;
        return removed;
    }

    /// <summary>
    /// 受保护消息：系统提示、工具消息、以及持久记忆写入上下文的 [记忆存档(等级-名称)] 存档消息。
    /// 这类内容一律不参与命令删除（宁可漏删，不可误删）。
    /// </summary>
    static bool IsProtectedMessage(ChatMessageContent message)
    {
        if (message == null)
            return true;

        if (message.Role == AuthorRole.System || message.Role == AuthorRole.Tool)
            return true;

        string? text = message.Content;
        if (string.IsNullOrEmpty(text))
            return false;

        // 持久记忆（Alife.Function.Memory）的存档消息格式：[记忆存档(等级-名称)] + 摘要
        return text.StartsWith("[记忆存档(", StringComparison.Ordinal);
    }

    /// <summary>
    /// 移除命令轮留下的那条消息（内容为命令触发词），确保命令文本不进入上下文。
    /// 只在 pendingCommand 已设置时调用，因此不会误删用户真正的同名消息。
    /// </summary>
    void RemoveTrailingCommandMessage(ChatHistory history)
    {
        if (history.Count == 0)
            return;

        ChatMessageContent last = history[^1];
        if (last.Role != AuthorRole.User)
            return;

        LanguageModelRouterConfig? cfg = Configuration;
        if (cfg == null)
            return;

        if (CommandParser.Find(last.Content, CollectTriggers(cfg)).Success == false)
            return;

        history.RemoveAt(history.Count - 1);
        Console.WriteLine("[灵枢] 已移除命令轮的消息，命令不会进入上下文");
    }

    // ──── 发起人校验 ────

    /// <summary>
    /// 命令发起人校验：QQ 环境下必须是主人 QQ（群聊里其他人的 /命令 一律忽略），
    /// 未配置主人 QQ 时一律拒绝（安全默认）；桌面等非 QQ 环境直接放行。
    /// </summary>
    bool IsCommandSenderAllowed(LanguageModelRouterConfig cfg, bool isQqContext, string? senderQq)
    {
        if (isQqContext == false)
            return true;

        if (cfg.RequireOwnerQq == false)
            return true;

        string owner = CommandParser.NormalizeQq(cfg.OwnerQq);
        if (owner.Length == 0)
        {
            Console.WriteLine("[灵枢] 命令未执行：消息来自 QQ，但未填写「主人 QQ」，为安全起见已忽略（可在面板「自定义命令」里填写）");
            return false;
        }

        if (string.Equals(CommandParser.NormalizeQq(senderQq), owner, StringComparison.Ordinal) == false)
        {
            Console.WriteLine($"[灵枢] 命令未执行：发起者 QQ={senderQq ?? "未知"} 不是主人 QQ（群聊防误触发）");
            return false;
        }

        return true;
    }
}
