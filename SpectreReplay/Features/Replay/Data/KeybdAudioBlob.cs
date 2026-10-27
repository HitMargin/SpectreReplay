namespace SpectreReplay.Features.Replay;

/// <summary>
/// 嵌入在回放文件里的键盘音（已压缩）。
///
/// <para><b>为什么要有这个类型，而不是直接把 wav 字节塞进 metadata。</b>
/// 压缩块自带三个必须与字节捆绑的参数——编码格式、采样率、样本数。
/// 只要其中一个和负载对不上，解出来的就是噪声或半截音频；把它们拆成
/// 三个独立字段到处传，迟早有一处漏改。打包成一个整体，序列化与反序列化
/// 就只有一处，校验也只有一处。</para>
///
/// <para><b>旧文件没有这一块是正常状态，不是错误。</b><see cref="HasData"/> 为
/// false 时，回放退回「同目录同名 .wav」的老路径（见
/// <c>ReplayManager.TryLoadKeybdSound</c>）。所以所有消费方都必须先问
/// <see cref="HasData"/>，不能假定它一定有值，更不能让它抛异常。</para>
///
/// <para><b>线程约定：</b>本结构只在主线程读写（保存、装载、导出都在主线程）。
/// 编码本身是纯计算，可以被调用方丢到后台线程，但那时传入的是 wav 字节副本，
/// 不是这个结构。</para>
///
/// <para><b>为什么是 public：</b><see cref="ReplayData"/> 是公开 API，
/// 它上面的属性不能是 internal 类型——那会让整个属性对外不可见，
/// 而外部订阅者（如存档工具）需要知道「这份回放是不是自带声音」。</para>
/// </summary>
public struct KeybdAudioBlob
{
    /// <summary>编码格式，取值见 <c>KeybdAudioCodec</c> 的 <c>Codec*</c> 常量。</summary>
    public int Codec;

    /// <summary>解码后的采样率（编码时已降采样到这个值）。</summary>
    public int SampleRate;

    /// <summary>声道数，恒为 1（录制端 <c>AudioRecorder</c> 已下混单声道）。</summary>
    public int Channels;

    /// <summary>每个声道的样本数（解码后必须正好这么多）。</summary>
    public int SampleCount;

    /// <summary>压缩后的负载字节。null 或空数组表示「这份回放没有嵌入键盘音」。</summary>
    public byte[] Payload;

    /// <summary>
    /// 是否携带可解码的音频。**所有消费方都必须先问这个**：
    /// 它为 false 时既不能解码，也不能据此认为文件损坏。
    /// </summary>
    public readonly bool HasData => Payload != null && Payload.Length > 0 && SampleCount > 0 && SampleRate > 0;
}