using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
/// 行为对齐官方 Alife.Function.Language.OpenAI 的 AlifeContentUtility，并做了三点防卡死加固：
/// ① 网址先下载到本地再读取，下载有独立超时（面板可配，默认 30s），超时立刻放弃并把原因告诉 AI/用户；
/// ② 下载 + 读取 + 追加对话全部放到后台：框架会等函数执行完才结束本轮（XmlFunctionCaller.EndFeeding），
///   一段慢下载以前会把整轮对话乃至桌宠一起卡住；
/// ③ 同一链接在可配的缓存时长内复用本地文件，失败链接在可配的暂停期内直接拒绝重试，
///   避免"反复看同一张图反复卡死"。
/// </summary>
public static class AlifeContentUtility
{
    /// <summary>未配置超时（或配置为 0）时的兜底下载超时，对齐旧行为。</summary>
    const int FallbackLoadTimeoutSeconds = 180;

    /// <summary>构建统一的 Load 系函数。protocolTypeName 用于向执行器询问"当前渠道组是否开启该类型"。</summary>
    public static XmlFunction BuildXmlFunction(
        string name,
        string protocolTypeName,
        Func<string, bool, KernelContent> fileToContent,
        ChatBot chatBot,
        IMultimodalExecutor executor,
        LanguageModelRouterConfig config)
    {
        return new XmlFunction
        {
            Name = name,
            Mode = FunctionMode.OneShot,
            Parameters =
            [
                new XmlParameter { Name = "pathOrUrl", Type = "String", Description = "本机文件路径或可直链访问的网络地址" },
                new XmlParameter { Name = "persistent", Type = "bool", Description = "将内容常驻上下文以持续分析（在后续多轮对话中依然可见），默认false（仅本次分析后即移除）" },
            ],
            Invoker = (context, cancellationToken) =>
            {
                string? pathOrUrl = GetParameter(context, "pathOrUrl", "path", "url");
                if (string.IsNullOrWhiteSpace(pathOrUrl))
                {
                    chatBot.Poke("请提供本机文件路径或可直链访问的网络地址。");
                    return Task.CompletedTask;
                }

                // 失败冷却（面板可配）：同一链接刚超时/断网过，先拦下来，避免一轮又一轮地卡在同一个链接上
                if (IsUrl(pathOrUrl) && TryGetFailureCooldown(pathOrUrl, out int remainSeconds))
                {
                    chatBot.Poke($"这个链接刚刚加载失败过（可能已过期或网络不通），{remainSeconds} 秒内不再重试；"
                        + "如果确实需要查看，请让用户重新发送一次内容。");
                    return Task.CompletedTask;
                }

                // 临时分析依赖"当前服务渠道组真正支持该媒体"：不支持时媒体会被剥离成占位文本，模型看不到
                string? blockReason = executor.GetContentBlockReason(protocolTypeName);
                if (blockReason != null)
                {
                    chatBot.Poke(blockReason);
                    return Task.CompletedTask;
                }

                // 常驻需要面板开关授权（对齐官方 enabledPersistentContentTypes）；未授权时降级为临时，不打断流程
                bool persistentRequested = ResolvePersistentRequested(context);
                bool persistent = persistentRequested && config != null && config.EnablePersistentMedia;
                if (persistentRequested && persistent == false)
                    Console.WriteLine("[灵枢] AI 请求常驻媒体，但面板未开启「允许媒体常驻上下文」，已降级为临时。");

                // 关键：不在本轮同步等待下载。框架的 XmlFunctionCaller 会等函数执行完才结束本轮对话，
                // 下载慢/卡住时整轮对话（以及桌宠）都被拖着；改为后台执行，本轮立即结束，结果由下一轮回答。
                _ = LoadAndChatAsync(chatBot, config, fileToContent, pathOrUrl, persistent, cancellationToken);
                return Task.CompletedTask;
            }
        };
    }

    /// <summary>后台任务：解析链接 / 校验文件 → 读入内容 → 追加一条消息并触发一次正常对话。</summary>
    static async Task LoadAndChatAsync(
        ChatBot chatBot,
        LanguageModelRouterConfig? config,
        Func<string, bool, KernelContent> fileToContent,
        string pathOrUrl,
        bool persistent,
        CancellationToken cancellationToken)
    {
        bool isUrl = IsUrl(pathOrUrl);
        int timeoutSeconds = ResolveLoadTimeoutSeconds(config);
        long maxBytes = Math.Max(1, config?.MediaMaxMegabytes ?? 10) * 1024L * 1024L;
        string localPath = pathOrUrl;
        bool fromCache = false;

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0)
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            if (isUrl)
            {
                long startedAt = Environment.TickCount64;
                (localPath, fromCache) = await ResolveUrlToFileAsync(
                    pathOrUrl, maxBytes, ResolveCacheLifetime(config), timeoutSource.Token);
                Console.WriteLine(fromCache
                    ? $"[灵枢] 内容命中本地缓存（不再下载）：{ShortenUrl(pathOrUrl)}"
                    : $"[灵枢] 内容下载完成（{Environment.TickCount64 - startedAt}ms）：{ShortenUrl(pathOrUrl)}");
            }

            if (File.Exists(localPath) == false)
            {
                chatBot.Poke($"本地文件不存在：{localPath}");
                return;
            }

            long fileSize = new FileInfo(localPath).Length;
            if (fileSize <= 0)
            {
                chatBot.Poke("内容读取失败：文件为空。");
                return;
            }
            if (fileSize > maxBytes)
            {
                chatBot.Poke($"内容过大（{fileSize / 1024 / 1024.0:F1} MB，上限 {maxBytes / 1024 / 1024} MB），已取消发送以避免上游拒绝。");
                return;
            }

            KernelContent content = fileToContent(localPath, persistent);
            StartContentChat(chatBot, content, persistent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 对话被打断 / 模块卸载：静默结束，不打扰用户
            Console.WriteLine("[灵枢] 内容加载已取消（对话中断或插件卸载）");
        }
        catch (OperationCanceledException)
        {
            string text = $"内容加载超时（{timeoutSeconds} 秒）：链接可能已过期或网络不通，本轮不再等待。"
                + "可以稍后重试，或让用户重新发送一次内容。";
            Console.WriteLine($"[灵枢] {text}（{ShortenUrl(pathOrUrl)}）");
            if (isUrl)
            {
                MarkFailureCooldown(pathOrUrl, ResolveFailureCooldownSeconds(config));
                DeleteBrokenFile(localPath);
            }
            SafePoke(chatBot, text);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[灵枢] 内容加载失败：{e.Message}（{ShortenUrl(pathOrUrl)}）");
            if (isUrl && e is HttpRequestException)
                MarkFailureCooldown(pathOrUrl, ResolveFailureCooldownSeconds(config));
            if (isUrl)
                DeleteBrokenFile(localPath);
            SafePoke(chatBot, $"内容加载失败：{e.Message}");
        }
    }

    /// <summary>删除加载失败的文件（含缓存文件），避免下次继续命中一个坏文件；本地路径不动。</summary>
    static void DeleteBrokenFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 删不掉也无所谓：缓存会按有效期自然过期
        }
    }

    /// <summary>
    /// 把内容作为一条正常用户消息加入对话并触发一次正常对话（AI 自身直接回答，无中间人二次转述）。
    /// persistent=false 时对话结束后移除该媒体消息；true 则常驻上下文（直到记忆压缩收走这一段）。
    /// 不 await：避免阻塞调用方（对齐官方写法），对话仍会走本插件的容灾管道。
    /// </summary>
    static void StartContentChat(ChatBot chatBot, KernelContent content, bool persistent)
    {
        ChatMessageContent message = new(AuthorRole.User, [content])
        {
            Content = persistent ? $"[多模态内容({content.GetType().Name})·常驻]" : $"[多模态内容({content.GetType().Name})]"
        };

        _ = RunAsync();

        async Task RunAsync()
        {
            bool keep = false;
            try
            {
                ChatResult result = await chatBot.ChatAsync(message, false);
                if (result.Exception != null)
                    SafePoke(chatBot, "多模态内容分析失败：" + result.Exception.Message);
                else
                    keep = persistent;
            }
            catch (Exception e)
            {
                SafePoke(chatBot, "多模态内容分析失败：" + e.Message);
            }

            if (keep == false)
                await RemoveContentMessageAsync(chatBot, message);
        }
    }

    /// <summary>Poke 的安全包装：角色活动正在销毁时 ChatBot 可能已不可用，不能让它把后台任务搞崩。</summary>
    static void SafePoke(ChatBot chatBot, string message)
    {
        try { chatBot.Poke(message); } catch { }
    }

    /// <summary>移除临时媒体消息；失败时重试一次，避免残留（残留会被媒体卫生层在下一轮剥离，但仍应尽量清干净）。</summary>
    static async Task RemoveContentMessageAsync(ChatBot chatBot, ChatMessageContent message)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await chatBot.EditChatHistoryAsync(thread =>
                {
                    thread.ChatHistory.Remove(message);
                    return Task.CompletedTask;
                }, "移除多模态资源");
                return;
            }
            catch (Exception e)
            {
                if (attempt == 1)
                    Console.WriteLine($"[灵枢] 移除临时媒体消息失败（媒体卫生层会在下一轮剥离）：{e.Message}");
                else
                    await Task.Delay(200);
            }
        }
    }

    /// <summary>
    /// 解析 persistent 参数：新参数优先；兼容旧 temp（语义相反，temp=true 等价 persistent=false）。
    /// 都不传时默认 false = 仅本次分析后移除（对齐官方）。
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

    /// <summary>下载超时秒数：配置为 0/负数时退回旧的 180 秒兜底，避免出现"永不超时"。</summary>
    static int ResolveLoadTimeoutSeconds(LanguageModelRouterConfig? config)
    {
        int configured = config?.MediaLoadTimeoutSeconds ?? 0;
        return configured > 0 ? Math.Clamp(configured, 5, 600) : FallbackLoadTimeoutSeconds;
    }

    /// <summary>本地缓存复用时长：0 = 不缓存（每次重新下载）。</summary>
    static TimeSpan ResolveCacheLifetime(LanguageModelRouterConfig? config)
    {
        int minutes = config?.MediaCacheMinutes ?? 0;
        return minutes > 0 ? TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440)) : TimeSpan.Zero;
    }

    /// <summary>失败后暂停重试的时长（秒）：0 = 不禁用重试。</summary>
    static int ResolveFailureCooldownSeconds(LanguageModelRouterConfig? config)
    {
        int seconds = config?.MediaFailureCooldownSeconds ?? 0;
        return seconds > 0 ? Math.Clamp(seconds, 1, 3600) : 0;
    }

    // ──── 下载与缓存 ────

    /// <summary>
    /// 把网址解析为本地文件：优先命中缓存时长内的文件，否则下载到缓存目录。
    /// 缓存让"同一张图重试/重看"不再重新联网，也是 QQ 图片链接偶发超时时能立刻恢复的关键。
    /// </summary>
    static async Task<(string Path, bool FromCache)> ResolveUrlToFileAsync(
        string url, long maxBytes, TimeSpan cacheLifetime, CancellationToken cancellationToken)
    {
        string cacheKey = CacheKeyOf(url);
        if (cacheLifetime > TimeSpan.Zero)
        {
            string? cached = FindCachedFile(cacheKey, cacheLifetime);
            if (cached != null)
                return (cached, true);
        }

        string extension = Path.GetExtension(url.Split('?')[0]);
        if (string.IsNullOrEmpty(extension))
            extension = await GetExtensionFromContentTypeAsync(url, cancellationToken);

        Directory.CreateDirectory(CacheFolder);
        string path = Path.Combine(CacheFolder, cacheKey + extension);

        await DownloadAsync(url, path, maxBytes, cancellationToken);
        SweepCacheFolder(cacheLifetime);
        return (path, false);
    }

    /// <summary>流式下载到目标路径：超过大小上限立即中断，失败时不留半截文件。</summary>
    static async Task DownloadAsync(string url, string path, long maxBytes, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        ApplyBrowserHeaders(request, url);

        using HttpResponseMessage response = await HttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > maxBytes)
            throw new Exception($"内容过大（{declaredLength / 1024.0 / 1024:F1} MB，上限 {maxBytes / 1024 / 1024} MB）");

        string tempPath = path + ".part";
        try
        {
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream target = File.Create(tempPath);

            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new Exception($"内容过大（超过 {maxBytes / 1024 / 1024} MB 上限）");

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total <= 0)
                throw new Exception("下载到空内容，链接可能已失效。");
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        File.Move(tempPath, path, true);
    }

    static async Task<string> GetExtensionFromContentTypeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Head, url);
            ApplyBrowserHeaders(request, url);

            // HEAD 只是用来补扩展名，失败/超时都不影响下载本身（真实类型仍由 MediaSniffer 按文件头嗅探）
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
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

    /// <summary>缓存键：网址的 SHA256 前缀（纯十六进制，可安全做文件名）。</summary>
    static string CacheKeyOf(string url)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    /// <summary>查找未过期的缓存文件（过期或空文件顺手清理）。</summary>
    static string? FindCachedFile(string cacheKey, TimeSpan cacheLifetime)
    {
        try
        {
            if (Directory.Exists(CacheFolder) == false)
                return null;

            foreach (string file in Directory.EnumerateFiles(CacheFolder, cacheKey + "*"))
            {
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) <= cacheLifetime && new FileInfo(file).Length > 0)
                    return file;

                TryDelete(file);
            }
        }
        catch
        {
            // 缓存目录不可用时退化为直接下载
        }
        return null;
    }

    /// <summary>缓存目录轻量清理：文件过多时删掉过期项，避免长期运行把临时目录撑爆。</summary>
    static void SweepCacheFolder(TimeSpan cacheLifetime)
    {
        try
        {
            if (Directory.Exists(CacheFolder) == false)
                return;

            string[] files = Directory.GetFiles(CacheFolder);
            if (files.Length <= 60)
                return;

            TimeSpan keep = cacheLifetime > TimeSpan.Zero ? cacheLifetime : TimeSpan.FromMinutes(10);
            DateTime now = DateTime.UtcNow;
            foreach (string file in files)
            {
                if (now - File.GetLastWriteTimeUtc(file) > keep)
                    TryDelete(file);
            }
        }
        catch
        {
            // 清理失败无所谓
        }
    }

    static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { File.Delete(path); } catch { }
    }

    // ──── 失败冷却 ────

    static readonly Dictionary<string, DateTime> failureCooldowns = new(StringComparer.Ordinal);

    /// <summary>该链接是否刚失败过（返回剩余秒数）。</summary>
    static bool TryGetFailureCooldown(string url, out int remainSeconds)
    {
        remainSeconds = 0;
        lock (failureCooldowns)
        {
            if (failureCooldowns.TryGetValue(url, out DateTime until) == false)
                return false;

            TimeSpan remain = until - DateTime.UtcNow;
            if (remain <= TimeSpan.Zero)
            {
                failureCooldowns.Remove(url);
                return false;
            }

            remainSeconds = Math.Max(1, (int)Math.Ceiling(remain.TotalSeconds));
            return true;
        }
    }

    /// <summary>记录一次失败（超时/网络错误），在配置的暂停期内不再重试同一链接；0 = 不记录。</summary>
    static void MarkFailureCooldown(string url, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0)
            return;

        lock (failureCooldowns)
        {
            if (failureCooldowns.Count > 50)
                failureCooldowns.Clear();
            failureCooldowns[url] = DateTime.UtcNow + TimeSpan.FromSeconds(cooldownSeconds);
        }
    }

    /// <summary>日志里的网址预览：只留主机与前若干字符，避免把签名参数整段写进日志。</summary>
    static string ShortenUrl(string url)
    {
        if (IsUrl(url) == false)
            return url;

        int query = url.IndexOf('?');
        string head = query > 0 ? url[..query] : url;
        return head.Length <= 80 ? head : head[..80] + "…";
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
        "image/heic" => ".heic",
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

    /// <summary>下载缓存目录（插件自建，不依赖框架新增 API，保证向后兼容）。</summary>
    static string CacheFolder
    {
        get
        {
            string folder = Path.Combine(TempFolder, "cache");
            try { Directory.CreateDirectory(folder); } catch { }
            return folder;
        }
    }

    /// <summary>临时目录（插件自建，不依赖框架新增 API，保证向后兼容）。</summary>
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
