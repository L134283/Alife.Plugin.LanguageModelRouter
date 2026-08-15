using System;
using System.Collections.Generic;
using System.Linq;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>单个渠道组配置</summary>
public class GroupChannel
{
    public string GroupName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? ReasoningEffort { get; set; }
    public string? ExtraHeaders { get; set; }
    public string? ExtraBody { get; set; }
    public string? ExtraBodyNotThinking { get; set; } // 非思考模式请求体（对齐官方 extraBodyNotThinking，默认 {"thinking":{"type":"disabled"}}）

    /// <summary>是否具备可用渠道（Endpoint 与 ApiKey 均非空）</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}

public class LanguageModelRouterConfig
{
    public const int MinGroups = 1;
    public const int MaxGroups = 12;

    /// <summary>动态渠道组列表（列表顺序即容灾顺序，第 1 个即主组）</summary>
    public List<GroupChannel> Groups { get; set; } = new();

    // === 旧版固定字段（v4.0.0 及之前，仅用于迁移旧配置，新配置不再写入）===
    public string GroupName1 { get; set; } = "";
    public string Endpoint1 { get; set; } = "";
    public string ModelId1 { get; set; } = "";
    public string ApiKey1 { get; set; } = "";
    public string? ReasoningEffort1 { get; set; }
    public string? ExtraHeaders1 { get; set; }
    public string? ExtraBody1 { get; set; }

    public string GroupName2 { get; set; } = "";
    public string? Endpoint2 { get; set; }
    public string? ModelId2 { get; set; }
    public string? ApiKey2 { get; set; }
    public string? ReasoningEffort2 { get; set; }
    public string? ExtraHeaders2 { get; set; }
    public string? ExtraBody2 { get; set; }

    public string GroupName3 { get; set; } = "";
    public string? Endpoint3 { get; set; }
    public string? ModelId3 { get; set; }
    public string? ApiKey3 { get; set; }
    public string? ReasoningEffort3 { get; set; }
    public string? ExtraHeaders3 { get; set; }
    public string? ExtraBody3 { get; set; }

    public string GroupName4 { get; set; } = "";
    public string? Endpoint4 { get; set; }
    public string? ModelId4 { get; set; }
    public string? ApiKey4 { get; set; }
    public string? ReasoningEffort4 { get; set; }
    public string? ExtraHeaders4 { get; set; }
    public string? ExtraBody4 { get; set; }

    // === 旧版排序（仅迁移用）===
    public int[] GroupOrder { get; set; } = new[] { 0, 1, 2, 3 };

    // === 容灾设置 ===
    public string? ErrorKeywords { get; set; } // 逗号分隔
    public int RetryDelayMs { get; set; } = 1000;
    public bool PriorityMainChannel { get; set; } = false; // 优先主渠道：每次请求先试主渠道，全程静默容灾
    public bool ShowThinkingChain { get; set; } = true; // 是否将 reasoning/thinking 转为可见思维链

    // === 智能思考/非思考切换（对齐官方 OpenAILanguageModel 机制）===
    public bool SmartThinkingEnabled { get; set; } = false; // 独立开关：启用后默认非思考，AI 需要深度思考时自动切回思考
    public bool DefaultThinking { get; set; } = false;      // 开关开启时：默认是否思考（true=恒思考，false=默认非思考）

    // === 运行时状态（每个桌宠独立，随配置持久化）===
    public int ForcedGroupIndex { get; set; } = -1; // -1=自动容灾, >=0=强制使用 Groups 索引
    public bool AutoFailoverEnabled { get; set; } = true; // 是否启用自动容灾切换

    /// <summary>
    /// 确保 Groups 可用：迁移旧扁平字段、保证至少 1 组、截断上限。
    /// 旧配置（Endpoint1~4 等）在首次加载时按旧 GroupOrder 顺序迁移为 Groups。
    /// </summary>
    public void EnsureGroups()
    {
        Groups ??= new List<GroupChannel>();

        if (Groups.Count == 0 && HasLegacyConfig())
            Groups = MigrateFromLegacy();

        if (Groups.Count == 0)
            Groups.Add(new GroupChannel());

        if (Groups.Count > MaxGroups)
            Groups = Groups.Take(MaxGroups).ToList();

        // 修正强制锁定索引：越界、指向已删除组、或指向未配置组时复位为自动容灾，
        // 避免 UI 显示"强制锁定某组"但实际请求仍从主组开始的不一致
        if (ForcedGroupIndex >= Groups.Count || (ForcedGroupIndex >= 0 && !Groups[ForcedGroupIndex].IsConfigured))
            ForcedGroupIndex = -1;
    }

    bool HasLegacyConfig()
        => !string.IsNullOrWhiteSpace(Endpoint1)
        || !string.IsNullOrWhiteSpace(Endpoint2)
        || !string.IsNullOrWhiteSpace(Endpoint3)
        || !string.IsNullOrWhiteSpace(Endpoint4)
        || !string.IsNullOrWhiteSpace(ApiKey1)
        || !string.IsNullOrWhiteSpace(ApiKey2)
        || !string.IsNullOrWhiteSpace(ApiKey3)
        || !string.IsNullOrWhiteSpace(ApiKey4);

    List<GroupChannel> MigrateFromLegacy()
    {
        var legacy = new List<GroupChannel>
        {
            new() { GroupName = GroupName1, Endpoint = Endpoint1, ModelId = ModelId1, ApiKey = ApiKey1, ReasoningEffort = ReasoningEffort1, ExtraHeaders = ExtraHeaders1, ExtraBody = ExtraBody1 },
            new() { GroupName = GroupName2, Endpoint = Endpoint2 ?? "", ModelId = ModelId2 ?? "", ApiKey = ApiKey2 ?? "", ReasoningEffort = ReasoningEffort2, ExtraHeaders = ExtraHeaders2, ExtraBody = ExtraBody2 },
            new() { GroupName = GroupName3, Endpoint = Endpoint3 ?? "", ModelId = ModelId3 ?? "", ApiKey = ApiKey3 ?? "", ReasoningEffort = ReasoningEffort3, ExtraHeaders = ExtraHeaders3, ExtraBody = ExtraBody3 },
            new() { GroupName = GroupName4, Endpoint = Endpoint4 ?? "", ModelId = ModelId4 ?? "", ApiKey = ApiKey4 ?? "", ReasoningEffort = ReasoningEffort4, ExtraHeaders = ExtraHeaders4, ExtraBody = ExtraBody4 },
        };

        var order = GroupOrder ?? new[] { 0, 1, 2, 3 };
        var used = new HashSet<int>();
        var ordered = new List<GroupChannel>();
        foreach (int v in order)
        {
            if (v >= 0 && v < legacy.Count && used.Add(v))
                ordered.Add(legacy[v]);
        }
        for (int i = 0; i < legacy.Count; i++)
        {
            if (used.Add(i))
                ordered.Add(legacy[i]);
        }
        return ordered;
    }

    /// <summary>新增一个空组，返回是否成功（达到上限返回 false）</summary>
    public bool AddGroup()
    {
        EnsureGroups();
        if (Groups.Count >= MaxGroups)
            return false;
        Groups.Add(new GroupChannel());
        return true;
    }

    /// <summary>删除指定索引的组（主组不可删，至少保留 1 组），并修正强制锁定索引</summary>
    public bool RemoveGroup(int zeroBasedIndex)
    {
        EnsureGroups();
        if (Groups.Count <= MinGroups)
            return false;
        if (zeroBasedIndex < 0 || zeroBasedIndex >= Groups.Count)
            return false;
        // 不允许删主组（第 1 组）
        if (zeroBasedIndex == 0)
            return false;

        Groups.RemoveAt(zeroBasedIndex);
        if (ForcedGroupIndex == zeroBasedIndex)
            ForcedGroupIndex = -1;
        else if (ForcedGroupIndex > zeroBasedIndex)
            ForcedGroupIndex--;
        return true;
    }
}
