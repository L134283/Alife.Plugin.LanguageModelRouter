using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Newtonsoft.Json.Linq;

namespace Alife.Plugin.LanguageModelRouter;

    /// <summary>
    /// 多路容灾切换 Handler：检测 HTTP 429/402/5xx 或响应体中的错误关键字，
    /// 自动切换到下一组 endpoint/apiKey 重试。
    /// 同时处理 SSE 流中 reasoning_content/thinking 等字段到 content 的兼容转换。
    /// </summary>
public class FallbackHandler : DelegatingHandler
{
    readonly Func<List<FallbackGroup>> getGroups;
    readonly List<string> errorKeywords;
    readonly int retryDelayMs;
    readonly Func<int> getForcedGroupIndex;
    readonly Func<bool> getAutoFailoverEnabled;
    readonly Action<int>? onFailover;
    readonly Func<bool>? consumeTestFlag;

    static readonly string[] ReasoningKeys = {
        "reasoning_content",
        "thought",
        "thinking",
        "thought_content",
        "reasoning"
    };

    public FallbackHandler(
        HttpMessageHandler innerHandler,
        Func<List<FallbackGroup>> getGroups,
        List<string> errorKeywords,
        int retryDelayMs,
        Func<int>? getForcedGroupIndex = null,
        Func<bool>? getAutoFailoverEnabled = null,
        Action<int>? onFailover = null,
        Func<bool>? consumeTestFlag = null
    ) : base(innerHandler)
    {
        this.getGroups = getGroups;
        this.errorKeywords = errorKeywords;
        this.retryDelayMs = retryDelayMs;
        this.getForcedGroupIndex = getForcedGroupIndex ?? (() => -1);
        this.getAutoFailoverEnabled = getAutoFailoverEnabled ?? (() => true);
        this.onFailover = onFailover;
        this.consumeTestFlag = consumeTestFlag;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var groups = getGroups() ?? new List<FallbackGroup>();
        if (groups.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("灵枢：未配置任何渠道组")
            };

        int forcedIdx = getForcedGroupIndex();
        bool autoEnabled = getAutoFailoverEnabled();

        int startGroup = forcedIdx >= 0 && forcedIdx < groups.Count ? forcedIdx : 0;
        int maxAttempts = autoEnabled ? groups.Count : 1;

        HttpResponseMessage? lastResponse = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int groupIdx = (startGroup + attempt) % groups.Count;
            var group = groups[groupIdx];

            HttpRequestMessage req;
            if (groupIdx == 0)
            {
                req = request;
            }
            else
            {
                req = await CloneRequestAsync(request);
                req.RequestUri = BuildNewUri(request.RequestUri!, group.Endpoint.AbsoluteUri);
                if (req.Headers.Authorization != null)
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", group.ApiKey);
                await RewriteRequestBody(req, group.ModelId);
            }

            if (attempt > 0 && retryDelayMs > 0)
                await Task.Delay(retryDelayMs, cancellationToken);

            HttpResponseMessage response;
            if (attempt == 0 && consumeTestFlag?.Invoke() == true)
            {
                response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":\"[灵枢 测试] 模拟 429\"}")
                };
            }
            else
            {
                response = await base.SendAsync(req, cancellationToken);
            }
            lastResponse = response;

            if (response.IsSuccessStatusCode)
            {
                if (attempt > 0)
                {
                    onFailover?.Invoke(groupIdx);
                }
                await ProcessReasoningStreamAsync(response);
                return response;
            }

            if (!autoEnabled)
            {
                Console.WriteLine($"[灵枢] #{groupIdx + 1} {group.Endpoint.Host} ✗{(int)response.StatusCode} 容灾已关闭");
                return response;
            }

            bool shouldFallback = IsFallbackStatus(response.StatusCode);

            if (!shouldFallback && errorKeywords.Count > 0)
            {
                shouldFallback = await ContainsErrorKeywordsAsync(response, cancellationToken);
            }

            if (!shouldFallback)
            {
                Console.WriteLine($"[灵枢] #{groupIdx + 1} {group.Endpoint.Host} ✗{(int)response.StatusCode}");
                return response;
            }

            if (attempt < maxAttempts - 1)
                Console.WriteLine($"[灵枢] #{groupIdx + 1} {group.Endpoint.Host} ✗{(int)response.StatusCode} → 容灾切换");
            else
                Console.WriteLine($"[灵枢] #{groupIdx + 1} {group.Endpoint.Host} ✗{(int)response.StatusCode}，无备用渠道可切换");

            if (attempt < maxAttempts - 1)
            {
                try { await response.Content.ReadAsStringAsync(cancellationToken); } catch { }
                response.Dispose();
            }
        }

        Console.WriteLine("[灵枢] 所有渠道均已失败");
        return lastResponse!;
    }

    static bool IsFallbackStatus(HttpStatusCode status)
    {
        int code = (int)status;
        return code == 429 || code == 402 || code >= 500;
    }

    async Task<bool> ContainsErrorKeywordsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            return errorKeywords.Any(kw => body.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 克隆 HttpRequestMessage（请求只能发送一次）
    /// </summary>
    static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };

        // 克隆 Content
        if (original.Content != null)
        {
            byte[] contentBytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);

            // 复制 Content Headers
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // 复制 Request Headers
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // 复制 Properties/Options
        foreach (var option in original.Options)
            clone.Options.TryAdd(option.Key, option.Value);

        return clone;
    }

    static async Task RewriteRequestBody(HttpRequestMessage req, string newModelId)
    {
        if (req.Content == null) return;
        byte[] body = await req.Content.ReadAsByteArrayAsync();
        JObject obj = JObject.Parse(Encoding.UTF8.GetString(body));
        obj["model"] = newModelId;
        byte[] newBody = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
        req.Content = new ByteArrayContent(newBody);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    /// <summary>
    /// 将原始 URI 的 scheme+host+port 替换为新 endpoint，保留原始路径和查询参数
    /// </summary>
    static Uri BuildNewUri(Uri originalUri, string newEndpoint)
    {
        if (!Uri.TryCreate(newEndpoint, UriKind.Absolute, out var endpointUri))
            return new Uri(newEndpoint);

        string basePath = endpointUri.AbsolutePath.TrimEnd('/');
        string path = basePath + "/chat/completions";
        string query = originalUri.Query;

        var builder = new UriBuilder(endpointUri)
        {
            Path = path,
            Query = query
        };
        return builder.Uri;
    }

    /// <summary>
    /// 处理 SSE 流中的 reasoning_content → content 转换
    /// </summary>
    async Task ProcessReasoningStreamAsync(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            Stream stream = await response.Content.ReadAsStreamAsync();
            response.Content = new StreamContent(new CompatibleStreamWrapper(stream));
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }
    }

    class CompatibleStreamWrapper : Stream
    {
        readonly Stream innerStream;
        readonly StreamReader reader;
        readonly MemoryStream outputBuffer = new();

        public CompatibleStreamWrapper(Stream innerStream)
        {
            this.innerStream = innerStream;
            this.reader = new StreamReader(innerStream, Encoding.UTF8);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (outputBuffer.Position >= outputBuffer.Length)
            {
                outputBuffer.SetLength(0);
                outputBuffer.Position = 0;

                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) return 0;

                string processedLine = ProcessLine(line) + "\n";
                byte[] bytes = Encoding.UTF8.GetBytes(processedLine);
                outputBuffer.Write(bytes);
                outputBuffer.Position = 0;
            }

            int count = await outputBuffer.ReadAsync(buffer, cancellationToken);
            return count;
        }

        private string ProcessLine(string line)
        {
            if (!line.StartsWith("data: ")) return line;

            string jsonPart = line.Substring(6).Trim();
            if (string.IsNullOrWhiteSpace(jsonPart) || jsonPart == "[DONE]") return line;

            try
            {
                JObject obj = JObject.Parse(jsonPart);
                JToken? delta = obj["choices"]?[0]?["delta"];
                if (delta is JObject deltaObj)
                {
                    foreach (var key in ReasoningKeys)
                    {
                        JToken? reasoning = deltaObj[key];
                        if (reasoning != null && reasoning.Type != JTokenType.Null)
                        {
                            string val = reasoning.ToString();
                            if (!string.IsNullOrEmpty(val))
                            {
                                JToken? curContent = deltaObj["content"];
                                bool hasContent = curContent != null && curContent.Type != JTokenType.Null
                                    && !string.IsNullOrEmpty(curContent.ToString());
                                if (!hasContent)
                                    deltaObj["content"] = $"{ChatBot.ThinkContentPrefix}{val}";
                                deltaObj.Remove(key);
                                break;
                            }
                        }
                    }
                }
                return "data: " + obj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return line;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;
        public override long Position { get => innerStream.Position; set => throw new NotSupportedException(); }
        public override void Flush() => innerStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("请使用 ReadAsync");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Dispose();
                innerStream.Dispose();
                outputBuffer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public record FallbackGroup(Uri Endpoint, string ModelId, string ApiKey);