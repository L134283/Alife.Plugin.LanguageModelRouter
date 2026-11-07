using System;
using System.IO;
using System.Text.Json.Nodes;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.SemanticKernel;

namespace Alife.Plugin.LanguageModelRouter;

/// <summary>音频内容：协议序列化为 <c>input_audio</c>。AI 通过 <c>LoadAudio</c> 查看，用 persistent 参数选择常驻/临时。
/// 格式按文件头嗅探（无扩展名的下载链不再一律当 mp3）。</summary>
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
        return AlifeContentUtility.BuildXmlFunction("LoadAudio", ProtocolTypeName, AudioFileToContent, chatBot, executor, config);
    }

    static KernelContent AudioFileToContent(string file, bool persistent)
    {
        byte[] data = File.ReadAllBytes(file);
        return new AudioContent
        {
            Data = data,
            Format = MediaSniffer.ResolveAudioFormat(file, data),
            Metadata = persistent ? MediaHygiene.PersistentMetadata : MediaHygiene.TemporaryMetadata
        };
    }
}
