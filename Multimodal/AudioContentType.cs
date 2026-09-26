using System;
using System.IO;
using System.Text.Json.Nodes;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>音频内容：协议序列化为 <c>input_audio</c>。AI 通过 <c>LoadAudio</c> 查看，用 persistent 参数选择常驻/临时。
/// 写法对齐官方 Alife.Function.Language.OpenAI 4.5.0 的 AudioContentRegistrar。</summary>
public sealed class AudioContent : KernelContent
{
    public required byte[] Data { get; init; }
    public required string Format { get; init; }
}

public sealed class AudioContentType : AlifeContentHandlerBase
{
    public override Type ContentType => typeof(AudioContent);
    public override string DisplayName => "音频输入";
    public override string ProtocolTypeName => "input_audio";

    public override JsonObject SerializeContent(KernelContent content)
    {
        AudioContent audio = (AudioContent)content;
        return new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(audio.Data),
                ["format"] = audio.Format
            }
        };
    }

    public override XmlFunction? CreateXmlFunction(ChatBot chatBot, LanguageModelRouterConfig config, IMultimodalExecutor executor)
    {
        return AlifeContentUtility.BuildXmlFunction("LoadAudio", ProtocolTypeName, AudioFileToContent, chatBot, executor);
    }

    static KernelContent AudioFileToContent(string file)
    {
        return new AudioContent
        {
            Data = File.ReadAllBytes(file),
            Format = GetFormat(file)
        };

        static string GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "mp3",
            ".wav" => "wav",
            ".m4a" => "m4a",
            ".flac" => "flac",
            ".ogg" or ".opus" => "ogg",
            ".aac" => "aac",
            ".webm" => "webm",
            _ => "mp3"
        };
    }
}
