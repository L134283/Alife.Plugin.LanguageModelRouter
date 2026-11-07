using System;
using System.IO;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>
/// 媒体类型嗅探：优先按文件头魔数识别真实类型，扩展名仅作补充。
/// 对齐官方 Alife.Function.Language.OpenAI 的 ImageContentRegistrar：
/// 无扩展名的临时文件（如 QQ 下载链 multimedia.nt.qq.com.cn/download?appid=…）
/// 只按扩展名会回退 application/octet-stream，被上游直接拒收（HTTP 400 弹窗）。
/// </summary>
public static class MediaSniffer
{
    /// <summary>魔数识别图片 MIME；无法识别返回 null。</summary>
    public static string? SniffImage(byte[] data)
    {
        if (data.Length < 3)
            return null;

        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return "image/png";

        // GIF: "GIF8"
        if (data.Length >= 4 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return "image/gif";

        // WEBP: "RIFF"…"WEBP"
        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "image/webp";

        // BMP: "BM"
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)
            return "image/bmp";

        // TIFF: II*\0 或 MM\0*
        if (data.Length >= 4
            && ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00)
                || (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A)))
            return "image/tiff";

        // HEIF/AVIF: 偏移 4 起为 "ftyp"，品牌为 heic/heix/mif1/avif
        if (data.Length >= 12
            && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
        {
            string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
            if (brand is "heic" or "heix" or "mif1" or "msf1")
                return "image/heic";
            if (brand is "avif")
                return "image/avif";
        }

        return null;
    }

    /// <summary>魔数识别音频格式（input_audio 协议要求 format 而非 mime）；无法识别返回 null。</summary>
    public static string? SniffAudioFormat(byte[] data)
    {
        if (data.Length < 4)
            return null;

        // MP3: "ID3" 或帧同步 FF Ex
        if ((data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
            || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x06) != 0))
            return "mp3";

        // WAV: "RIFF"…"WAVE"
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data.Length >= 12 && data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45)
            return "wav";

        // OGG / OPUS: "OggS"
        if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
            return "ogg";

        // FLAC: "fLaC"
        if (data[0] == 0x66 && data[1] == 0x4C && data[2] == 0x61 && data[3] == 0x43)
            return "flac";

        // AMR: "#!AMR"
        if (data[0] == 0x23 && data[1] == 0x21 && data[2] == 0x41 && data[3] == 0x4D)
            return "amr";

        // M4A / AAC(ADTS): ftyp(m4a) 或 FF F1/FF F9
        if (data.Length >= 12 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
            return "m4a";
        if (data[0] == 0xFF && (data[1] == 0xF1 || data[1] == 0xF9))
            return "aac";

        return null;
    }

    /// <summary>魔数识别视频 MIME；无法识别返回 null。</summary>
    public static string? SniffVideoMime(byte[] data)
    {
        if (data.Length < 12)
            return null;

        // MP4 / MOV / M4V: 偏移 4 起为 "ftyp"
        if (data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
        {
            string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
            if (brand is "qt  ")
                return "video/quicktime";
            if (brand is "M4V " or "M4VH" or "M4VP")
                return "video/x-m4v";
            return "video/mp4";
        }

        // AVI: "RIFF"…"AVI "
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x41 && data[9] == 0x56 && data[10] == 0x49 && data[11] == 0x20)
            return "video/x-msvideo";

        // MKV / WEBM: EBML 头 1A 45 DF A3（无法进一步区分，统一按 webm 交给上游判断）
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            return "video/webm";

        return null;
    }

    /// <summary>
    /// 判断内容是否像文本（HTML/JSON 错误页）：
    /// 下载到的大概率是鉴权过期返回的错误页，直接报错比发 octet-stream 更易排查。
    /// </summary>
    public static bool LooksLikeText(byte[] data)
    {
        int i = 0;
        while (i < data.Length && (data[i] == 0x20 || data[i] == 0x09 || data[i] == 0x0D || data[i] == 0x0A
                                   || data[i] == 0xEF || data[i] == 0xBB || data[i] == 0xBF))
            i++;
        if (i >= data.Length)
            return true;

        byte b = data[i];
        return b == 0x3C || b == 0x7B || b == 0x5B; // < / { / [
    }

    /// <summary>扩展名 → MIME（图片/视频/音频，含官方支持的集合与少量补充）。</summary>
    public static string FromExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" or ".heif" => "image/heic",
            ".avif" => "image/avif",
            ".svg" => "image/svg+xml",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            ".m4a" => "audio/m4a",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            ".amr" => "audio/amr",
            _ => ""
        };
    }

    /// <summary>扩展名 → input_audio 的 format 字段。</summary>
    public static string AudioFormatFromExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "mp3",
            ".wav" => "wav",
            ".m4a" => "m4a",
            ".flac" => "flac",
            ".ogg" or ".opus" => "ogg",
            ".aac" => "aac",
            ".amr" => "amr",
            ".webm" => "webm",
            _ => ""
        };
    }

    /// <summary>解析图片 MIME：魔数 → 扩展名 → 文本检测（抛错）→ image/jpeg 兜底。</summary>
    /// <exception cref="NotSupportedException">下载内容是文本（链接过期/鉴权失败）。</exception>
    public static string ResolveImageMime(string path, byte[] data)
    {
        string? sniffed = SniffImage(data);
        if (sniffed != null)
            return sniffed;

        string fromExtension = FromExtension(path);
        if (fromExtension.StartsWith("image/", StringComparison.Ordinal))
            return fromExtension;

        if (LooksLikeText(data))
            throw new NotSupportedException("下载到的内容不是图片（可能是链接过期返回了文本），请更换链接后重试。");

        return "image/jpeg";
    }

    /// <summary>解析视频 MIME：魔数 → 扩展名 → 文本检测（抛错）→ video/mp4 兜底。</summary>
    public static string ResolveVideoMime(string path, byte[] data)
    {
        string? sniffed = SniffVideoMime(data);
        if (sniffed != null)
            return sniffed;

        string fromExtension = FromExtension(path);
        if (fromExtension.StartsWith("video/", StringComparison.Ordinal))
            return fromExtension;

        if (LooksLikeText(data))
            throw new NotSupportedException("下载到的内容不是视频（可能是链接过期返回了文本），请更换链接后重试。");

        return "video/mp4";
    }

    /// <summary>解析音频 format：魔数 → 扩展名 → mp3 兜底。</summary>
    public static string ResolveAudioFormat(string path, byte[] data)
    {
        string? sniffed = SniffAudioFormat(data);
        if (sniffed != null)
            return sniffed;

        string fromExtension = AudioFormatFromExtension(path);
        if (string.IsNullOrEmpty(fromExtension) == false)
            return fromExtension;

        return "mp3";
    }
}
