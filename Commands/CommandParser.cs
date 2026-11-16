using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 斜杠命令的纯解析层（不依赖 Alife 框架，便于单测）。
/// 负责：在一整条消息里找出"单独成行的命令"，并带出该行说话人的 QQ 号。
/// </summary>
/// <remarks>
/// 消息形态参考（命令判定必须能容忍下面这些框架/插件追加的外壳）：
/// ① 桌面：[源(ChatWindow)]/重新生成
/// ② 桌面带时间戳：[时间(2026-11-06 08:00:00)]\n[源(ChatWindow)]/重新生成
/// ③ 桌面带通用提示词：[源(ChatWindow)]/重新生成\n(回复时请保持发言简洁…)
/// ④ 桌面带系统推送（Poke）：[源(ChatWindow)]/重新生成\n\n[系统推送消息]\n[源(Xxx)]事件提醒
/// ⑤ 桌宠输入框（InteractiveModule/Interactor 的发言方标记）：消息来源:[DeskPetService]/r
///    也可能是 `消息来源:[DeskPetService]\n/r`，两种都要能认
/// ⑥ QQ 私聊：[私聊消息(123,昵称)]\n[123(昵称)]:/重新生成\n(回复请保持1-20字)
/// ⑦ QQ 群聊（一轮多条）：[群聊消息(456,群名)]\n[111(甲)]:/重新生成\n[222(乙)]:哈哈哈\n(回复保持简短)
/// </remarks>
public static class CommandParser
{
    /// <summary>解析结果。</summary>
    public readonly record struct Result(bool Success, string? Trigger, string? SenderQq, bool IsQqContext);

    /// <summary>一条解析出来的正文行：Qq=说话人（QQ 环境），Content=去掉外壳后的正文。</summary>
    readonly record struct ContentLine(string? Qq, string Content);

    /// <summary>消息来源（用于把提示发回原处）：非 QQ / 私聊 / 群聊 + 目标号。</summary>
    public readonly record struct SourceTarget(bool IsQq, bool IsGroup, long Id);

    /// <summary>
    /// 框架系统推送块标记（对齐 ChatBot.PokeMessageTag）。
    /// ChatBot 会把积压的 Poke 以 "\n\n[系统推送消息]\n……" 追加到用户消息末尾，
    /// 这些内容不是用户说的话，命令判定必须先把它们整块砍掉。
    /// </summary>
    const string PokeBlockTag = "[系统推送消息]";

    /// <summary>QQ 说话人行：[123456(昵称)]:内容 / [123456]:内容。</summary>
    static readonly Regex SenderLineRegex = new(
        @"^\[(?<qq>\d{5,15})(\((?<name>[^\)]*)\))?\]\s*[:：]\s*(?<content>.*)$",
        RegexOptions.Compiled);

    /// <summary>行首方括号标签，如 [源(ChatWindow)]、[时间(...)]、[系统推送消息]。</summary>
    static readonly Regex LeadingTagRegex = new(@"^\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>
    /// 行首的「消息来源」前缀：`消息来源:[DeskPetService]内容`、`消息来源:[DeskPetService]\n内容`。
    /// 老版本框架（InteractiveModule）与部分模块用它标记发言方，桌宠输入框走的就是这条路。
    /// 它既不是用户说的话，也会让命令判定失效，必须先剥掉。
    /// </summary>
    static readonly Regex SourcePrefixRegex = new(
        @"^消息来源\s*[:：]\s*(\[[^\]]*\])?", RegexOptions.Compiled);

    /// <summary>时间戳标签行：[时间(2026-11-16 10:00:00)]（重发时要去掉，避免 AI 看到过期时间）。</summary>
    const string TimestampTag = "[时间(";

    /// <summary>QQ 插件附在消息头部的来源标签：[私聊消息(123,昵称)] / [群聊消息(456,群名)]。</summary>
    static readonly Regex SourceTagRegex = new(
        @"\[(?<kind>私聊|群聊)消息\((?<id>\d{1,20})", RegexOptions.Compiled);

    /// <summary>解析消息来源（用于把命令提示发回原处：私聊发私聊、群聊发该群）。</summary>
    public static SourceTarget ParseSourceTarget(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return new SourceTarget(false, false, 0);

        Match match = SourceTagRegex.Match(message);
        if (match.Success && long.TryParse(match.Groups["id"].Value, out long id) && id > 0)
            return new SourceTarget(true, match.Groups["kind"].Value == "群聊", id);

        return new SourceTarget(false, false, 0);
    }

    /// <summary>
    /// 砍掉框架追加在末尾的「系统推送消息」块（Poke）。
    /// 块内是各个模块推给 AI 的通知，既不是用户正文，也不该参与命令的"只能单独成行"判定。
    /// </summary>
    public static string StripPokeBlock(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message ?? "";

        int index = message.IndexOf(PokeBlockTag, StringComparison.Ordinal);
        return index < 0 ? message : message[..index];
    }

    /// <summary>
    /// 在消息中查找命令。
    /// 命中条件（严格模式，避免误触发）：
    /// ① 某一行的内容**整行等于**触发词（前后可以有空白，不能有任何附加文字，允许全角斜杠）；
    /// ② 该行必须是说话人这一条消息的全部内容 —— 桌面消息里不能同时有别的正文；
    ///    群聊里同一位说话人不能在此轮还发了别的话（其他人的发言不影响，由主人校验负责）。
    /// 系统推送块、发言方标记（消息来源:\[Xxx\]）与已知的追加提示词会先被剔除，不计入正文判定。
    /// </summary>
    public static Result Find(string? message, IReadOnlyList<string>? triggers, IEnumerable<string>? wrapperTexts = null)
    {
        if (string.IsNullOrWhiteSpace(message) || triggers == null || triggers.Count == 0)
            return new Result(false, null, null, false);

        message = StripPokeBlock(message);
        message = StripKnownWrapperTexts(message, wrapperTexts);

        bool isQq = IsQqContext(message);

        // 先拆出所有"真正的正文行"（跳过来源标签、时间戳、发言方标记、QQ 附加的括号/方括号说明块等包装）
        List<ContentLine> lines = ExtractContentLines(message);

        for (int index = 0; index < lines.Count; index++)
        {
            ContentLine line = lines[index];

            string normalized = line.Content.Replace('／', '/').Trim();
            if (normalized.Length <= 1 || normalized[0] != '/')
                continue;

            string? matched = null;
            foreach (string trigger in triggers)
            {
                if (string.IsNullOrWhiteSpace(trigger))
                    continue;

                if (string.Equals(normalized, trigger.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    matched = trigger;
                    break;
                }
            }

            if (matched == null)
                continue;

            // 严格校验：这条消息里不能还有别的内容
            if (line.Qq == null)
            {
                // 桌面/其他来源：整条消息只能有这一行正文
                if (lines.Count != 1)
                    continue;
            }
            else
            {
                // QQ：同一说话人这一轮只说这一句（其他人的发言不受影响）
                bool sameSenderSpokeMore = false;
                for (int other = 0; other < lines.Count; other++)
                {
                    if (other == index)
                        continue;
                    if (string.Equals(lines[other].Qq, line.Qq, StringComparison.Ordinal))
                    {
                        sameSenderSpokeMore = true;
                        break;
                    }
                }

                if (sameSenderSpokeMore)
                    continue;
            }

            return new Result(true, matched, line.Qq, isQq);
        }

        return new Result(false, null, null, isQq);
    }

    /// <summary>
    /// 提取"要重发的用户正文"（/重新生成 用）：只剔除三样东西——系统推送块（Poke）、已知的追加提示词、
    /// 过期的时间戳标签行；**其余原样保留**（含 `[源(Xxx)]`、`消息来源:[Xxx]`、QQ 说话人标签）。
    /// 保留发言方标记很关键：DeskPet/QChat 的"必须用 Speak/QChat 标签回复"规则是靠匹配用户消息里的标记生效的，
    /// 剥掉标记会让重发那一轮不要求标签，于是桌宠没有气泡、QQ 也不发消息。
    /// 没有正文时返回 null。
    /// </summary>
    public static string? ExtractUserText(string? message, IEnumerable<string>? wrapperTexts = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        string cleaned = StripPokeBlock(message);
        cleaned = StripKnownWrapperTexts(cleaned, wrapperTexts);

        List<string> kept = new();
        bool hasContent = false;
        foreach (string rawLine in cleaned.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;

            // 过期时间戳：重发时框架会加新的，留着只会让 AI 看到旧时间
            if (line.StartsWith(TimestampTag, StringComparison.Ordinal))
                continue;

            // 整行被括号包住的追加提示词（默认 MessageAppend 就是这种形状）：不是用户正文
            if ((line[0] == '(' || line[0] == '（') && (line.EndsWith(")", StringComparison.Ordinal) || line.EndsWith("）", StringComparison.Ordinal)))
                continue;

            kept.Add(line);

            if (hasContent == false && HasContent(line))
                hasContent = true;
        }

        if (kept.Count == 0 || hasContent == false)
            return null;

        return string.Join("\n", kept);
    }

    /// <summary>这一行去掉各种外壳后是否还有正文（纯标签行、纯发言方标记行不算）。</summary>
    static bool HasContent(string line)
    {
        string value = StripSourcePrefix(line);
        value = StripLeadingTags(value);
        return value.Length > 0;
    }

    /// <summary>消息里是否出现了某个命令触发词（仅用于诊断日志：出现了却没被识别，值得记一笔）。</summary>
    public static bool ContainsTriggerText(string? message, IReadOnlyList<string>? triggers)
    {
        if (string.IsNullOrEmpty(message) || triggers == null)
            return false;

        foreach (string trigger in triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger) || trigger.Length < 2)
                continue;
            if (message.Contains(trigger, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 剔除"已知的追加文案"（QQ 插件配置里的私聊/群聊提示词原文、框架「消息过滤」的通用提示词等）。
    /// 这是最可靠的一层：不依赖用户把提示词写成括号/引号/【】等任何形状，只要是我们拿到原文就整段删掉。
    /// </summary>
    public static string StripKnownWrapperTexts(string message, IEnumerable<string>? wrapperTexts)
    {
        if (string.IsNullOrEmpty(message) || wrapperTexts == null)
            return message;

        foreach (string? text in wrapperTexts)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
                continue;

            if (message.Contains(text, StringComparison.Ordinal) == false)
                continue;

            message = message.Replace(text, "\n", StringComparison.Ordinal);
        }

        return message;
    }

    /// <summary>
    /// 拆出"真正的正文行"，把框架/QQ 插件加的外壳全部剔除：
    /// ① 行首的 [源(...)]、[时间(...)]、[系统推送消息]、[群聊消息(...)] 等方括号标签；
    /// ② 行首的「消息来源:[Xxx]」发言方标记（桌宠输入框）；
    /// ③ 以 ( 或 （ 开头的说明块（可能跨多行，直到出现 ) / ） 结尾，例如
    ///    QQ 的 "(此消息来自QQ私聊，回复需要使用消息格式，示例:&lt;QChat ...&gt; ...)"；
    /// ④ 跨行的方括号说明块。
    /// 剩下的才是"用户/说话人真正说的话"。
    /// </summary>
    static List<ContentLine> ExtractContentLines(string message)
    {
        List<ContentLine> lines = new();
        string[] rawLines = message.Split('\n');

        // QQ 私聊的消息本身不带说话人标签（说话人只在头部 [私聊消息(id,昵称)] 里），
        // 因此私聊里的无标签正文统一归属该 id；群聊每行自带 [qq(昵称)]: 标签，不做归属推断。
        SourceTarget source = ParseSourceTarget(message);
        string? privateChatSender = source is { IsQq: true, IsGroup: false, Id: > 0 }
            ? source.Id.ToString()
            : null;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;

            // 「消息来源:[Xxx]」发言方标记先剥掉（桌宠输入框、交互事件走这条路）
            line = StripSourcePrefix(line);
            if (line.Length == 0)
                continue;

            // 括号说明块（QQ 插件的"私聊/群聊提示词"，用户可自定义，可能跨行）：
            // 一直吞到以 ) / ） 结尾的那一行；但遇到下一个块开始（[ 标签行 / 说话人行）就停，
            // 避免把真正的群聊消息也吞掉。
            if (line[0] == '(' || line[0] == '（')
            {
                int guard = 0;
                while (ClosesParenthesis(line) == false && guard++ < 12 && i + 1 < rawLines.Length)
                {
                    string next = rawLines[i + 1].Trim();
                    if (next.Length > 0 && (next[0] == '[' || SenderLineRegex.IsMatch(next)))
                        break;

                    i++;
                    line = next;
                }
                continue;
            }

            // 方括号说明块：单行 [..] 由后面的 StripLeadingTags 处理；跨行的先吞掉
            if (line[0] == '[' && line.Contains(']') == false)
            {
                while (line.Contains(']') == false && i + 1 < rawLines.Length)
                {
                    i++;
                    line = rawLines[i].Trim();
                }
                continue;
            }

            string? qq = null;
            string content;

            Match sender = SenderLineRegex.Match(line);
            if (sender.Success)
            {
                qq = sender.Groups["qq"].Value;
                content = sender.Groups["content"].Value.Trim();
            }
            else
            {
                content = StripLeadingTags(line);
            }

            if (content.Length == 0)
                continue;

            lines.Add(new ContentLine(qq ?? privateChatSender, content));
        }

        return lines;

        static bool ClosesParenthesis(string text)
            => text.EndsWith(")", StringComparison.Ordinal) || text.EndsWith("）", StringComparison.Ordinal);
    }

    /// <summary>消息是否来自 QQ（私聊/群聊标签，或含 [QQ号(昵称)]: 说话人行）。</summary>
    public static bool IsQqContext(string message)
    {
        if (string.IsNullOrEmpty(message))
            return false;

        if (message.Contains("[群聊消息(", StringComparison.Ordinal)
            || message.Contains("[私聊消息(", StringComparison.Ordinal))
            return true;

        foreach (string rawLine in message.Split('\n'))
        {
            if (SenderLineRegex.IsMatch(rawLine.Trim()))
                return true;
        }
        return false;
    }

    /// <summary>只保留数字（用于比对 QQ 号，容忍空格；全角数字统一转半角）。</summary>
    public static string NormalizeQq(string? qq)
    {
        if (string.IsNullOrWhiteSpace(qq))
            return "";

        Span<char> buffer = stackalloc char[qq.Length];
        int length = 0;
        foreach (char c in qq)
        {
            if (c >= '0' && c <= '9')
                buffer[length++] = c;
            else if (c >= '０' && c <= '９')
                buffer[length++] = (char)(c - '０' + '0');
        }
        return new string(buffer[..length]);
    }

    /// <summary>剥掉行首的「消息来源:[Xxx]」前缀（含其后的方括号标签），剩余即正文。</summary>
    static string StripSourcePrefix(string line)
    {
        if (line.StartsWith("消息来源", StringComparison.Ordinal) == false)
            return line;

        return SourcePrefixRegex.Replace(line, "").Trim();
    }

    /// <summary>反复剥离行首的方括号标签。</summary>
    static string StripLeadingTags(string line)
    {
        string value = line;
        while (value.Length > 0 && value[0] == '[')
        {
            Match tag = LeadingTagRegex.Match(value);
            if (tag.Success == false)
                break;

            value = value[tag.Length..].TrimStart();
        }
        return value;
    }
}
