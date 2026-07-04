using System.Text.Json.Serialization;

namespace Alife.Plugin.LanguageModelRouter;

public class LanguageModelRouterConfig
{
    // 第1组（必填）
    public string GroupName1 { get; set; } = "";
    public string Endpoint1 { get; set; } = "";
    public string ModelId1 { get; set; } = "";
    public string ApiKey1 { get; set; } = "";
    public string? ReasoningEffort1 { get; set; }
    public string? ExtraHeaders1 { get; set; }
    public string? ExtraBody1 { get; set; }

    // 第2组（可选）
    public string GroupName2 { get; set; } = "";
    public string? Endpoint2 { get; set; }
    public string? ModelId2 { get; set; }
    public string? ApiKey2 { get; set; }
    public string? ReasoningEffort2 { get; set; }
    public string? ExtraHeaders2 { get; set; }
    public string? ExtraBody2 { get; set; }

    // 第3组（可选）
    public string GroupName3 { get; set; } = "";
    public string? Endpoint3 { get; set; }
    public string? ModelId3 { get; set; }
    public string? ApiKey3 { get; set; }
    public string? ReasoningEffort3 { get; set; }
    public string? ExtraHeaders3 { get; set; }
    public string? ExtraBody3 { get; set; }

    // 第4组（可选）
    public string GroupName4 { get; set; } = "";
    public string? Endpoint4 { get; set; }
    public string? ModelId4 { get; set; }
    public string? ApiKey4 { get; set; }
    public string? ReasoningEffort4 { get; set; }
    public string? ExtraHeaders4 { get; set; }
    public string? ExtraBody4 { get; set; }

    // 容灾设置
    public string? ErrorKeywords { get; set; } // 逗号分隔
    public int RetryDelayMs { get; set; } = 1000;
}