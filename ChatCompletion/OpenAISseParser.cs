using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>SSE 流中的一条有效载荷。</summary>
public sealed class OpenAISseChunk
{
    /// <summary>是否为流结束标记（[DONE] 或流终止）。</summary>
    public bool IsDone { get; init; }

    /// <summary>本次增量正文。</summary>
    public string? Content { get; init; }

    /// <summary>本次增量思考内容（reasoning_content / thinking / reasoning 等原生字段）。</summary>
    public string? Reasoning { get; init; }

    /// <summary>服务端返回的用量（通常只在流末尾出现）。</summary>
    public JsonObject? Usage { get; init; }

    /// <summary>服务端在流中返回的错误。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// OpenAI 兼容 SSE 解析器（对齐官方 AlifeSseParser 的原生字段拆分写法，并做三处健壮性增强）：
/// ① 支持 idleTimeoutMs 流空闲超时，防止渠道返回 200 后流挂起导致永久等待；
/// ② content 既支持字符串也支持多模态 parts 数组（部分渠道忽略 stream 结构差异）；
/// ③ 错误帧同时兼容 {"error":{"message":...}} 与 {"error":"..."}。
/// 厂商差异化字段集中在此适配，上层只消费结构化块。
/// </summary>
public static class OpenAISseParser
{
    /// <summary>各家厂商在 delta 中表达思考内容的字段名（与官方保持一致）。</summary>
    static readonly string[] ReasoningKeys =
    [
        "reasoning_content",
        "thought",
        "thinking",
        "thought_content",
        "reasoning"
    ];

    public static async IAsyncEnumerable<OpenAISseChunk> ParseAsync(
        Stream stream,
        int idleTimeoutMs = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (true)
        {
            string? line = await ReadLineAsync(reader, idleTimeoutMs, cancellationToken);
            if (line == null)
            {
                yield return new OpenAISseChunk { IsDone = true };
                yield break;
            }

            // 兼容 "data: {...}" 与 "data:{...}"，忽略 event:/id:/注释行
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == false)
                continue;

            string data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
            {
                yield return new OpenAISseChunk { IsDone = true };
                yield break;
            }

            JsonObject? chunk;
            try
            {
                chunk = JsonNode.Parse(data) as JsonObject;
            }
            catch (JsonException)
            {
                continue; // 坏帧静默跳过，不让单帧解析失败打断整轮输出
            }
            if (chunk == null)
                continue;

            if (chunk["error"] is JsonNode error)
            {
                yield return new OpenAISseChunk { Error = ErrorText(error) };
                continue;
            }

            JsonObject? delta = null;
            if (chunk["choices"] is JsonArray choices && choices.Count > 0)
                delta = choices[0]?["delta"] as JsonObject;

            string? content = ExtractContent(delta?["content"]);
            string? reasoning = ExtractReasoning(delta);

            yield return new OpenAISseChunk {
                Content = content,
                Reasoning = reasoning,
                Usage = chunk["usage"] as JsonObject
            };
        }
    }

    /// <summary>读取一行；开启 idleTimeoutMs 时，超时未收到任何数据按连接异常处理。</summary>
    static async ValueTask<string?> ReadLineAsync(StreamReader reader, int idleTimeoutMs, CancellationToken cancellationToken)
    {
        if (idleTimeoutMs <= 0)
            return await reader.ReadLineAsync(cancellationToken);

        using CancellationTokenSource idleSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleSource.CancelAfter(idleTimeoutMs);
        try
        {
            return await reader.ReadLineAsync(idleSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
        {
            throw new TimeoutException($"灵枢：响应流空闲超时（{idleTimeoutMs}ms 未收到任何数据），请检查渠道连通性");
        }
    }

    /// <summary>content 可能是字符串，也可能是 parts 数组；数组时拼接其中的 text。</summary>
    internal static string? ExtractContent(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
            return string.IsNullOrEmpty(text) ? null : text;

        if (node is JsonArray parts)
        {
            StringBuilder builder = new();
            foreach (JsonNode? part in parts)
            {
                if (part is JsonObject item
                    && item["type"]?.ToString() == "text"
                    && item["text"] is JsonValue partValue
                    && partValue.TryGetValue<string>(out string? partText))
                {
                    builder.Append(partText);
                }
            }
            return builder.Length > 0 ? builder.ToString() : null;
        }

        return null;
    }

    /// <summary>从 delta/message 中按各家字段名提取思考增量（取第一个非空）。</summary>
    internal static string? ExtractReasoning(JsonObject? delta)
    {
        if (delta == null)
            return null;

        foreach (string key in ReasoningKeys)
        {
            if (delta[key] is not JsonNode node || node.GetValueKind() == JsonValueKind.Null)
                continue;
            if (node is JsonValue value && value.TryGetValue<string>(out string? text) && string.IsNullOrEmpty(text) == false)
                return text;
        }
        return null;
    }

    /// <summary>错误帧文本（兼容对象与字符串两种形态）。</summary>
    internal static string ErrorText(JsonNode error)
    {
        if (error is JsonObject obj)
            return obj["message"]?.ToString() ?? obj.ToJsonString();
        return error.ToString();
    }
}
