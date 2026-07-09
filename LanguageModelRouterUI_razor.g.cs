using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using AntDesign;

namespace Alife.Plugin.LanguageModelRouter;

public partial class LanguageModelRouterUI : ModuleUIBase<LanguageModelRouter, LanguageModelRouterConfig>
{
    string?[] _detectResults = new string?[4];
    List<string>?[] _detectedModels = new List<string>?[4];
    int _seq;

    protected override void OnInitialized()
    {
        LanguageModelRouter.OnGroupChanged = () => InvokeAsync(StateHasChanged);
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        _seq = 0;

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style",
            "background:#fafafa;padding:24px;border-radius:12px;border:1px solid #f0f0f0;max-width:680px;");

        Panel(b);

        b.CloseElement();
    }

    void Panel(RenderTreeBuilder b)
    {
        // 标题
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "font-size:18px;font-weight:bold;margin-bottom:4px;");
        b.AddContent(_seq++, "\u7075\u67a2 \u00b7 OpenAI\u8bed\u8a00\u6a21\u578b\u62a5\u9519\u81ea\u52a8\u5207\u6362");
        b.CloseElement();

        // 说明
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style",
            "font-size:12px;color:#555;background:#e6f7ff;padding:10px 12px;border-radius:8px;margin-bottom:16px;line-height:1.7;white-space:pre-line;");
        b.AddContent(_seq++,
            "\U0001f4cc \u66ff\u6362\u6846\u67b6\u5185\u7f6e\u7684 OpenAILanguageModel\uff0c\u5b9e\u73b0\u591a\u8def\u6587\u672c\u6a21\u578b\u81ea\u52a8\u5bb9\u707e\u5207\u6362\n\U0001f4cc \u9047\u5230 HTTP 429/402/5xx \u9519\u8bef\u6216\u54cd\u5e94\u4f53\u5305\u542b\u6307\u5b9a\u5173\u952e\u5b57\u65f6\uff0c\u81ea\u52a8\u5207\u6362\u5230\u4e0b\u4e00\u7ec4\u6e20\u9053\u91cd\u8bd5\n\U0001f4cc \u540c\u65f6\u652f\u6301 reasoning_content \u7b49 SSE \u601d\u7ef4\u94fe\u6d41\u7684\u81ea\u52a8\u8f6c\u6362\n\u26a0\ufe0f \u4f7f\u7528\u524d\u8bf7\u5728\u89d2\u8272\u914d\u7f6e\u4e2d\u7981\u7528 OpenAILanguageModel\uff0c\u542f\u7528\u672c\u6a21\u5757");
        b.CloseElement();

        // === 第1组 ===
        GroupConfig(b, 0, true);

        // === 第2组 ===
        AddCollapsibleGroup(b, "\u5907\u7528\u6e20\u9053 1\uff08\u7b2c2\u7ec4\uff09", () => GroupConfig(b, 1, false));

        // === 第3组 ===
        AddCollapsibleGroup(b, "\u5907\u7528\u6e20\u9053 2\uff08\u7b2c3\u7ec4\uff09", () => GroupConfig(b, 2, false));

        // === 第4组 ===
        AddCollapsibleGroup(b, "\u5907\u7528\u6e20\u9053 3\uff08\u7b2c4\u7ec4\uff09", () => GroupConfig(b, 3, false));

        // === 容灾设置 ===
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "margin-top:20px;");
        SectionTitle(b, "\u2699\ufe0f \u5bb9\u707e\u8bbe\u7f6e");

        AutoFailoverToggle(b);

        AddInput(b, "\u9519\u8bef\u5173\u952e\u5b57\uff08\u9017\u53f7\u5206\u9694\uff09", Configuration.ErrorKeywords ?? "", v => Configuration.ErrorKeywords = string.IsNullOrEmpty(v) ? null : v);
        AddHint(b, "\u54cd\u5e94\u4f53\u4e2d\u5305\u542b\u8fd9\u4e9b\u5173\u952e\u5b57\u65f6\u89e6\u53d1\u5207\u6362\uff0c\u5982 rate_limit,insufficient_quota,billing_hard_limit\n\u7559\u7a7a\u5219\u4ec5\u6309 HTTP \u72b6\u6001\u7801\u5224\u65ad");

        AddInput(b, "\u91cd\u8bd5\u95f4\u9694\uff08\u6beb\u79d2\uff09", Configuration.RetryDelayMs.ToString(), v =>
        {
            if (int.TryParse(v, out var n))
                Configuration.RetryDelayMs = Math.Clamp(n, 0, 30000);
        });
        AddHint(b, "\u5207\u6362\u5230\u4e0b\u4e00\u7ec4\u524d\u7684\u7b49\u5f85\u65f6\u95f4\uff0c\u9ed8\u8ba4 1000ms\uff0c\u8bbe\u4e3a 0 \u5219\u7acb\u5373\u91cd\u8bd5");

        b.CloseElement();

        // === 手动切换 ===
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "margin-top:20px;");
        SectionTitle(b, "\U0001f504 \u624b\u52a8\u5207\u6362");

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "font-size:12px;color:#555;margin-bottom:8px;");
        b.AddContent(_seq++, $"\u5f53\u524d\u72b6\u6001\uff1a{GetActiveGroupLabel()}");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "display:flex;gap:6px;flex-wrap:wrap;");

        int forcedIdx = LanguageModelRouter.ForcedGroupIndex;
        for (int g = 0; g < 4; g++)
        {
            int group = g;
            string name = LanguageModelRouter.GetGroupName(g, Configuration);
            string btnLabel = string.IsNullOrWhiteSpace(name) ? $"\u7b2c{group + 1}\u7ec4" : $"\u7b2c{group + 1}\u7ec4({name})";
            AddSwitchBtn(b, btnLabel, forcedIdx == group, () => SwitchTo(group));
        }

        b.CloseElement();
        AddHint(b, "\u70b9\u51fb\u6309\u94ae\u5207\u6362\u6e20\u9053\uff0c\u4e5f\u53ef\u5728\u804a\u5929\u4e2d\u544a\u8bc9\u684c\u5ba0\u300c\u5207\u6362\u5230\u7b2c\u4e8c\u7ec4\u300d\u6216\u6309\u540d\u79f0\u300c\u5207\u6362\u5230 deepseek\u300d\uff0cAI \u4f1a\u81ea\u52a8\u5207\u6362\u3002\u914d\u7f6e\u4fdd\u5b58\u540e\u5373\u523b\u751f\u6548\uff0c\u65e0\u9700\u91cd\u65b0\u52a0\u8f7d\u6a21\u5757\u3002");
        b.CloseElement();

        // === 使用说明 ===
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "margin-top:20px;");
        SectionTitle(b, "\U0001f4d6 \u4f7f\u7528\u8bf4\u660e");
        AddHint(b, "1. \u5728\u89d2\u8272\u914d\u7f6e\u4e2d\u7981\u7528\u300cOpenAI\u8bed\u8a00\u6a21\u578b\u300d\uff0c\u542f\u7528\u300c\u7075\u67a2 - OpenAI\u8bed\u8a00\u6a21\u578b\u62a5\u9519\u81ea\u52a8\u5207\u6362\u300d\n2. \u7b2c1\u7ec4\u4e3a\u4e3b\u6e20\u9053\uff0c\u5fc5\u987b\u586b\u5199 Endpoint\u3001Model ID \u548c API Key\n3. \u7b2c2~4\u7ec4\u4e3a\u5907\u7528\u6e20\u9053\uff0c\u9047\u5230 429/402/5xx \u9519\u8bef\u65f6\u81ea\u52a8\u5207\u6362\n4. \u7ec4\u540d\u79f0\u53ef\u7528\u4e8e AI \u8bc6\u522b\u6e20\u9053\uff0c\u5982\u5bf9\u684c\u5ba0\u8bf4\u300c\u5207\u6362\u5230 deepseek\u300d\u5373\u53ef\u5bf9\u5e94\u5207\u6362\n5. \u914d\u7f6e\u4fdd\u5b58\u540e\u5373\u523b\u751f\u6548\uff0c\u65e0\u9700\u91cd\u65b0\u52a0\u8f7d\u6a21\u5757");
        b.CloseElement();
    }

    // ==================== Group Config ====================

    void GroupConfig(RenderTreeBuilder b, int g, bool isFirst)
    {
        if (isFirst)
            SectionTitle(b, "\U0001f511 \u4e3b\u6e20\u9053\uff08\u7b2c1\u7ec4\uff0c\u5fc5\u586b\uff09");

        var cfg = Configuration!;

        string groupName = g switch
        {
            0 => cfg.GroupName1 ?? "",
            1 => cfg.GroupName2 ?? "",
            2 => cfg.GroupName3 ?? "",
            3 => cfg.GroupName4 ?? "",
            _ => ""
        };
        string endpoint = g switch
        {
            0 => cfg.Endpoint1,
            1 => cfg.Endpoint2 ?? "",
            2 => cfg.Endpoint3 ?? "",
            3 => cfg.Endpoint4 ?? "",
            _ => ""
        };
        string modelId = g switch
        {
            0 => cfg.ModelId1,
            1 => cfg.ModelId2 ?? "",
            2 => cfg.ModelId3 ?? "",
            3 => cfg.ModelId4 ?? "",
            _ => ""
        };
        string apiKey = g switch
        {
            0 => cfg.ApiKey1,
            1 => cfg.ApiKey2 ?? "",
            2 => cfg.ApiKey3 ?? "",
            3 => cfg.ApiKey4 ?? "",
            _ => ""
        };
        string reasoning = g switch
        {
            0 => cfg.ReasoningEffort1 ?? "",
            1 => cfg.ReasoningEffort2 ?? "",
            2 => cfg.ReasoningEffort3 ?? "",
            3 => cfg.ReasoningEffort4 ?? "",
            _ => ""
        };
        string extraH = g switch
        {
            0 => cfg.ExtraHeaders1 ?? "",
            1 => cfg.ExtraHeaders2 ?? "",
            2 => cfg.ExtraHeaders3 ?? "",
            3 => cfg.ExtraHeaders4 ?? "",
            _ => ""
        };
        string extraB = g switch
        {
            0 => cfg.ExtraBody1 ?? "",
            1 => cfg.ExtraBody2 ?? "",
            2 => cfg.ExtraBody3 ?? "",
            3 => cfg.ExtraBody4 ?? "",
            _ => ""
        };

        AddInput(b, "\u7ec4\u540d\u79f0\uff08\u53ef\u9009\uff0c\u4f9b AI \u8bc6\u522b\uff09", groupName, v => SetGroupName(g, v));
        AddInput(b, "Endpoint", endpoint, v => SetGroupEndpoint(g, v));
        AddHint(b, "API \u7aef\u70b9 URL\uff0c\u5982 https://api.openai.com/v1");
        AddInput(b, "Model ID", modelId, v => SetGroupModelId(g, v));
        AddHint(b, "\u6a21\u578b\u6807\u8bc6\uff0c\u5982 gpt-4o\u3001deepseek-chat");

        ProbeSection(b, g);

        AddPassword(b, "API Key", apiKey, v => SetGroupApiKey(g, v));

        AddInput(b, "Reasoning Effort", reasoning, v => SetGroupReasoning(g, v));
        AddHint(b, "\u63a8\u7406\u5f3a\u5ea6\uff0c\u5982 low / medium / high\uff0c\u7559\u7a7a\u5219\u4e0d\u8bbe\u7f6e");
        AddInput(b, "Extra Headers (JSON)", extraH, v => SetGroupExtraHeaders(g, v));
        AddHint(b, "\u989d\u5916\u8bf7\u6c42\u5934\uff0cJSON \u683c\u5f0f\uff0c\u5982 {\"X-Custom\":\"value\"}");
        AddInput(b, "Extra Body (JSON)", extraB, v => SetGroupExtraBody(g, v));
        AddHint(b, "\u989d\u5916\u8bf7\u6c42\u4f53\uff0cJSON \u683c\u5f0f\uff0c\u5982 {\"temperature\":0.7}");
    }

    void SetGroupName(int g, string v)
    {
        if (g == 0) Configuration!.GroupName1 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 1) Configuration!.GroupName2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.GroupName3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.GroupName4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupEndpoint(int g, string v)
    {
        if (g == 0) Configuration!.Endpoint1 = v;
        else if (g == 1) Configuration!.Endpoint2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.Endpoint3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.Endpoint4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupModelId(int g, string v)
    {
        if (g == 0) Configuration!.ModelId1 = v;
        else if (g == 1) Configuration!.ModelId2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.ModelId3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.ModelId4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupApiKey(int g, string v)
    {
        if (g == 0) Configuration!.ApiKey1 = v;
        else if (g == 1) Configuration!.ApiKey2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.ApiKey3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.ApiKey4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupReasoning(int g, string v)
    {
        string? val = string.IsNullOrWhiteSpace(v) ? null : v;
        if (g == 0) Configuration!.ReasoningEffort1 = val;
        else if (g == 1) Configuration!.ReasoningEffort2 = val;
        else if (g == 2) Configuration!.ReasoningEffort3 = val;
        else if (g == 3) Configuration!.ReasoningEffort4 = val;
    }

    void SetGroupExtraHeaders(int g, string v)
    {
        string? val = string.IsNullOrWhiteSpace(v) ? null : v;
        if (g == 0) Configuration!.ExtraHeaders1 = val;
        else if (g == 1) Configuration!.ExtraHeaders2 = val;
        else if (g == 2) Configuration!.ExtraHeaders3 = val;
        else if (g == 3) Configuration!.ExtraHeaders4 = val;
    }

    void SetGroupExtraBody(int g, string v)
    {
        string? val = string.IsNullOrWhiteSpace(v) ? null : v;
        if (g == 0) Configuration!.ExtraBody1 = val;
        else if (g == 1) Configuration!.ExtraBody2 = val;
        else if (g == 2) Configuration!.ExtraBody3 = val;
        else if (g == 3) Configuration!.ExtraBody4 = val;
    }

    // ==================== Probe & Dropdown ====================

    void ProbeSection(RenderTreeBuilder b, int groupIndex)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "margin-bottom:10px;");

        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "style",
            "padding:3px 12px;border-radius:4px;border:1px solid #52c41a;background:#f6ffed;color:#52c41a;cursor:pointer;font-size:11px;");
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, async () =>
        {
            await ProbeGroup(groupIndex);
        }));
        b.AddContent(_seq++, $"\u24d8 \u63a2\u6d4b\u7b2c{groupIndex + 1}\u7ec4");
        b.CloseElement();

        var result = _detectResults[groupIndex];
        if (result != null)
        {
            b.OpenElement(_seq++, "span");
            bool ok = result.Contains("连接成功") || result.Contains("个模型");
            b.AddAttribute(_seq++, "style", $"font-size:11px;margin-left:8px;color:{(ok ? "#52c41a" : "#ff4d4f")};");
            b.AddContent(_seq++, result);
            b.CloseElement();
        }

        var models = _detectedModels[groupIndex];
        if (models != null && models.Count > 0)
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "style", "margin-top:6px;display:flex;align-items:center;gap:6px;");

            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "style", "font-size:11px;color:#666;");
            b.AddContent(_seq++, "\u9009\u62e9\u6a21\u578b\uff1a");
            b.CloseElement();

            b.OpenElement(_seq++, "select");
            b.AddAttribute(_seq++, "style",
                "padding:2px 6px;border-radius:4px;border:1px solid #d9d9d9;font-size:12px;");
            b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                if (e.Value is string val && !string.IsNullOrWhiteSpace(val))
                {
                    SetGroupModelId(groupIndex, val);
                    StateHasChanged();
                }
            }));
            b.OpenElement(_seq++, "option");
            b.AddAttribute(_seq++, "value", "");
            string cur = groupIndex switch
            {
                0 => Configuration!.ModelId1,
                1 => Configuration!.ModelId2,
                2 => Configuration!.ModelId3,
                _ => Configuration!.ModelId4,
            };
            b.AddContent(_seq++, $"\u2014 \u5f53\u524d: {cur ?? "\u672a\u8bbe\u7f6e"} \u2014");
            b.CloseElement();
            foreach (var m in models)
            {
                b.OpenElement(_seq++, "option");
                b.AddAttribute(_seq++, "value", m);
                b.AddContent(_seq++, m);
                b.CloseElement();
            }
            b.CloseElement();

            b.CloseElement();
        }

        b.CloseElement();
    }

    async Task ProbeGroup(int groupIndex)
    {
        _detectResults[groupIndex] = "\u63a2\u6d4b\u4e2d\u2026";
        _detectedModels[groupIndex] = null;
        StateHasChanged();

        var (display, models) = await LanguageModelRouter.FetchModels(groupIndex, Configuration!);
        _detectResults[groupIndex] = display;
        _detectedModels[groupIndex] = models;
        StateHasChanged();
    }

    // ==================== Auto Failover Toggle ====================

    void AutoFailoverToggle(RenderTreeBuilder b)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "display:flex;align-items:center;gap:8px;margin-bottom:12px;");

        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "id", "autoFailoverCheck");
        b.AddAttribute(_seq++, "checked", LanguageModelRouter.AutoFailoverEnabled);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            LanguageModelRouter.AutoFailoverEnabled = e.Value is bool b ? b : !LanguageModelRouter.AutoFailoverEnabled;
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenElement(_seq++, "label");
        b.AddAttribute(_seq++, "for", "autoFailoverCheck");
        b.AddAttribute(_seq++, "style", "font-size:13px;color:#333;");
        b.AddContent(_seq++, "\u542f\u7528\u81ea\u52a8\u5bb9\u707e\uff08\u5f00\u542f\u540e\u65e0\u8bba\u5207\u6362\u5230\u54ea\u4e2a\u7ec4\uff0c\u9047\u9519\u8bef\u65f6\u81ea\u52a8\u5207\u6362\u5907\u7528\u6e20\u9053\uff09");
        b.CloseElement();

        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "display:flex;align-items:center;gap:8px;margin-bottom:12px;");

        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "id", "priorityMainCheck");
        b.AddAttribute(_seq++, "checked", LanguageModelRouter.PriorityMainChannel);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            LanguageModelRouter.PriorityMainChannel = e.Value is bool b ? b : !LanguageModelRouter.PriorityMainChannel;
            Configuration!.PriorityMainChannel = LanguageModelRouter.PriorityMainChannel;
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenElement(_seq++, "label");
        b.AddAttribute(_seq++, "for", "priorityMainCheck");
        b.AddAttribute(_seq++, "style", "font-size:13px;color:#333;");
        b.AddContent(_seq++, "\u4f18\u5148\u4e3b\u6e20\u9053\uff08\u6bcf\u6b21\u5bf9\u8bdd\u4f18\u5148\u5c1d\u8bd5\u4e3b\u6e20\u9053\uff0c\u5bb9\u707e\u5168\u7a0b\u9759\u9ed8\uff0c\u4ec5\u65e5\u5fd7\u53ef\u89c1\uff09");
        b.CloseElement();

        b.CloseElement();
    }

    // ==================== Switch ====================

    void SwitchTo(int groupIndex)
    {
        LanguageModelRouter.ForcedGroupIndex = groupIndex;
        LanguageModelRouter.OnGroupChanged?.Invoke();
        StateHasChanged();
    }

    void AddSwitchBtn(RenderTreeBuilder b, string text, bool active, Action onClick)
    {
        b.OpenElement(_seq++, "button");
        string bg = active ? "#1677ff" : "#fff";
        string fg = active ? "#fff" : "#333";
        string bd = active ? "#1677ff" : "#d9d9d9";
        b.AddAttribute(_seq++, "style",
            $"padding:4px 14px;border-radius:6px;border:1px solid {bd};background:{bg};color:{fg};cursor:pointer;font-size:12px;");
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, onClick));
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    // ==================== Shared Helpers ====================

    string GetActiveGroupLabel()
    {
        int idx = LanguageModelRouter.ForcedGroupIndex;
        if (idx < 0) return "\u81ea\u52a8\u5bb9\u707e";
        return LanguageModelRouter.GetGroupLabel(idx, Configuration);
    }

    void SectionTitle(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style",
            "font-size:14px;font-weight:bold;color:#555;margin:0 0 8px;" +
            "border-bottom:1px solid #e0e0e0;padding-bottom:4px;");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    void AddHint(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style",
            "font-size:11px;color:#999;margin:0 0 10px 2px;line-height:1.5;white-space:pre-line;");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    void AddLabel(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "font-weight:bold;margin-bottom:3px;font-size:13px;color:#444;");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    void AddInput(RenderTreeBuilder b, string label, string value, Action<string> setter)
    {
        AddLabel(b, label);
        b.OpenComponent<Input<string>>(_seq++);
        b.AddAttribute(_seq++, "Value", value);
        b.AddAttribute(_seq++, "ValueChanged",
            EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
    }

    void AddPassword(RenderTreeBuilder b, string label, string value, Action<string> setter)
    {
        AddLabel(b, label);
        b.OpenComponent<InputPassword>(_seq++);
        b.AddAttribute(_seq++, "Value", value);
        b.AddAttribute(_seq++, "ValueChanged",
            EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
    }

    void AddCollapsibleGroup(RenderTreeBuilder b, string title, Action renderContent)
    {
        b.OpenElement(_seq++, "details");
        b.AddAttribute(_seq++, "style",
            "margin:4px 0;border:1px solid #e8e8e8;border-radius:6px;padding:6px 10px;background:#fff;");
        b.OpenElement(_seq++, "summary");
        b.AddAttribute(_seq++, "style",
            "cursor:pointer;font-weight:bold;font-size:13px;color:#666;padding:2px 0;user-select:none;");
        b.AddContent(_seq++, $"\u2b07\ufe0f {title}");
        b.CloseElement();
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "padding:8px 0 4px;");
        renderContent();
        b.CloseElement();
        b.CloseElement();
    }
}
