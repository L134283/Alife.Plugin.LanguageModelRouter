using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// Load 系多模态函数的统一工具：把"本机路径或网络地址"归一化为可直接读取的本地文件，
/// 并以统一的 (pathOrUrl, persistent) 参数构建 AI 可调用函数。
/// 行为对齐官方 Alife.Function.Language.OpenAI 4.5.0 的 AlifeContentUtility：
/// ① 网址一律下载为本地临时文件后再读取（避免直链失效，也能真正验证可用性）；
/// ② 参数统一为 pathOrUrl，persistent 默认 false（仅本次分析后移除），显式 true 才常驻上下文。
/// </summary>
public static class AlifeContentUtility
{
    /// <summary>构建统一的 Load 系函数。protocolTypeName 用于向执行器询问"当前渠道组是否开启该类型"。</summary>
    public static XmlFunction BuildXmlFunction(
        string name,
        string protocolTypeName,
        Func<string, KernelContent> fileToContent,
        ChatBot chatBot,
        IMultimodalExecutor executor)
    {
        return new XmlFunction
        {
            Name = name,
            Mode = FunctionMode.OneShot,
            Parameters =
            [
                new XmlParameter { Name = "pathOrUrl", Type = "String", Description = "本机文件路径或可直链访问的网络地址" },
                new XmlParameter { Name = "persistent", Type = "bool", Description = "将内容常驻上下文以持续分析，默认false（仅本次分析后即移除）" },
            ],
            Invoker = async (context, cancellationToken) =>
            {
                string? pathOrUrl = GetParameter(context, "pathOrUrl", "path", "url");
                if (string.IsNullOrWhiteSpace(pathOrUrl))
                {
                    chatBot.Poke("请提供本机文件路径或可直链访问的网络地址。");
                    return;
                }

                // 临时分析依赖"当前服务渠道组真正支持该媒体"：不支持时媒体会被容灾管道剥离成占位文本，模型看不到
                if (executor.CanUseContentType(protocolTypeName) == false)
                {
                    chatBot.Poke("当前渠道组未开启对应的「原生多模态」，无法分析该内容。请在对应组的配置中开启后再试。");
                    return;
                }

                bool persistent = ResolvePersistentRequested(context);

                // 网址统一下载为本地临时文件后再读取：避免直链失效，且能真正验证可用性
                bool isUrl = IsUrl(pathOrUrl);
                string localPath = pathOrUrl;
                if (isUrl)
                {
                    try
                    {
                        localPath = await UrlToPath(pathOrUrl);
                    }
                    catch (Exception e)
                    {
                        chatBot.Poke($"内容下载失败：{e.Message}");
                        return;
                    }
                }

                try
                {
                    if (File.Exists(localPath) == false)
                    {
                        chatBot.Poke($"本地文件不存在：{localPath}");
                        return;
                    }

                    KernelContent content = fileToContent(localPath);
                    StartContentChat(chatBot, content, persistent);
                }
                catch (Exception e)
                {
                    chatBot.Poke($"内容加载失败：{e.Message}");
                }
                finally
                {
                    if (isUrl)
                    {
                        try { File.Delete(localPath); } catch { }
                    }
                }
            }
        };
    }

    /// <summary>
    /// 把内容作为一条正常用户消息加入对话并触发一次正常对话（AI 自身直接回答，无中间人二次转述）。
    /// persistent=false 时对话结束后移除该媒体消息；true 则常驻上下文。
    /// 不 await：避免阻塞 AI 的函数调用（对齐官方写法），对话仍会走本插件的容灾管道。
    /// </summary>
    static void StartContentChat(ChatBot chatBot, KernelContent content, bool persistent)
    {
        ChatMessageContent message = new(AuthorRole.User, [content])
        {
            Content = $"[多模态内容({content.GetType().Name})]"
        };

        _ = RunAsync();

        async Task RunAsync()
        {
            bool keep = false;
            try
            {
                ChatResult result = await chatBot.ChatAsync(message, false);
                if (result.Exception != null)
                    chatBot.Poke("多模态内容分析失败：" + result.Exception.Message);
                else
                    keep = persistent;
            }
            catch (Exception e)
            {
                chatBot.Poke("多模态内容分析失败：" + e.Message);
            }

            if (keep == false)
            {
                try
                {
                    await chatBot.EditChatHistoryAsync(thread =>
                    {
                        thread.ChatHistory.Remove(message);
                        return Task.CompletedTask;
                    }, "移除多模态资源");
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 解析 persistent 参数：新参数优先；兼容旧 temp（语义相反，temp=true 等价 persistent=false）。
    /// 都不传时默认 false = 仅本次分析后移除（对齐官方 4.5.0）。
    /// </summary>
    static bool ResolvePersistentRequested(XmlContext context)
    {
        if (context.Parameters.TryGetValue("persistent", out string? persistent) && string.IsNullOrWhiteSpace(persistent) == false)
            return IsTrue(persistent);
        if (context.Parameters.TryGetValue("temp", out string? temp) && string.IsNullOrWhiteSpace(temp) == false)
            return IsTrue(temp) == false; // 旧 temp=true 表示"临时"，等价新 persistent=false
        return false;
    }

    static bool IsTrue(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1", StringComparison.OrdinalIgnoreCase);

    /// <summary>按候选参数名依次读取（非空即返回），全部缺失返回 null。用于兼容历史/近义参数名。</summary>
    public static string? GetParameter(XmlContext context, params string[] names)
    {
        foreach (string name in names)
        {
            if (context.Parameters.TryGetValue(name, out string? value) && string.IsNullOrWhiteSpace(value) == false)
                return value;
        }
        return null;
    }

    public static bool IsUrl(string pathOrUrl)
        => pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把网址下载为本地临时文件并返回路径。无扩展名时用 HEAD 请求补全扩展名
    /// （含 QQ 多媒体域名的 Referer 适配，否则直链会 403）。
    /// </summary>
    public static async Task<string> UrlToPath(string url)
    {
        string extension = Path.GetExtension(url.Split('?')[0]);
        if (string.IsNullOrEmpty(extension))
            extension = await GetExtensionFromContentTypeAsync(url);

        Directory.CreateDirectory(TempFolder);
        string tempPath = Path.Combine(TempFolder, $"{Guid.NewGuid():N}{extension}");
        await DownloadAsync(url, tempPath);
        return tempPath;
    }

    static async Task DownloadAsync(string url, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBrowserHeaders(request, url);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(path);
        await response.Content.CopyToAsync(fileStream);
    }

    static async Task<string> GetExtensionFromContentTypeAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            ApplyBrowserHeaders(request, url);
            using var response = await HttpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string? contentType = response.Content.Headers.ContentType?.ToString();
                if (string.IsNullOrEmpty(contentType) == false)
                    return ContentTypeToExtension(contentType);
            }
        }
        catch
        {
            // 静默失败，回退到空扩展名
        }
        return string.Empty;
    }

    /// <summary>伪装浏览器 UA；对 QQ 多媒体域名补 Referer，否则会被拒绝。</summary>
    static void ApplyBrowserHeaders(HttpRequestMessage request, string url)
    {
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        if (url.Contains("multimedia.nt.qq.com.cn") || url.Contains("qpic.cn"))
            request.Headers.TryAddWithoutValidation("Referer", "https://q.qq.com/");
    }

    static string ContentTypeToExtension(string contentType) => contentType.Split(';')[0].Trim() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/tiff" => ".tiff",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/m4a" => ".m4a",
        "audio/aac" => ".aac",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "video/x-msvideo" => ".avi",
        _ => string.Empty,
    };

    /// <summary>下载临时目录（插件自建，不依赖框架新增 API，保证向后兼容）。</summary>
    static string TempFolder => _tempFolder ??= CreateTempFolder();

    static string? _tempFolder;

    static string CreateTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Alife.Plugin.LanguageModelRouter");
        try { Directory.CreateDirectory(folder); } catch { }
        return folder;
    }

    static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(3) };
}
