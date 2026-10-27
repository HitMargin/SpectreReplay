namespace SpectreReplay.Features.Replay;

/// <summary>
/// 一份回放自带的键盘音播放覆盖值。字段为默认值时 HasXxx 为 false，表示「跟随全局设置」。
///
/// <para><b>为什么覆盖值是「有/无」而不是「等于某个数」。</b>全局设置才是常规来源；
/// 逐份回放只是少数例外。若用哨兵值（如 Volume = -1）表示「没覆盖」，那么
/// 「恰好想设成该值」就再也表达不出来，而且每个消费点都要记得判哨兵。
/// 每位独立一个 Has 标志，缺口只在「整段不存在」这一种——那由
/// <see cref="HasAny"/> 统一回答。</para>
///
/// <para><b>为什么偏移是「替换」而不是「相加」。</b>回放的键盘音与谱面音频的
/// 对轴误差是**这一份文件自己的属性**（同一台机器、同一次录制决定），把全局偏移
/// 叠加上去会让「换个人播放」结果不同，回放就不再可复现。要覆盖就直接替换，
/// 语义只有一条。</para>
/// </summary>
public struct KeybdPrefs
{
    /// <summary>是否覆盖音量。false 时 <see cref="Volume"/> 无意义（段里仍占 4 字节，恒写 0）。</summary>
    public bool HasVolume;

    /// <summary>音量 0..10，10 = 满音量（与 Settings.KeybdSoundVolume 同量纲；播放侧 /10f → AudioSource.volume）。</summary>
    public int Volume;

    /// <summary>是否覆盖偏移。false 时 <see cref="OffsetMs"/> 无意义（段里仍占 4 字节，恒写 0）。</summary>
    public bool HasOffset;

    /// <summary>偏移毫秒；**替换**全局 Settings.MicrophoneOffset（不是相加）。</summary>
    public int OffsetMs;

    /// <summary>这一份回放是否静音。与 <see cref="Volume"/> 独立：静音时不看音量值。</summary>
    public bool IsMuted;

    /// <summary>
    /// 是否携带任何覆盖值。false 表示「段一个字节都不写、播放完全跟随全局设置」。
    ///
    /// <para>**必须由三个 Has 字段算出来，不能额外存一个 bool 字段。**存了就有两份真值，
    /// 改字段忘了改标记时，写入端会写出一个「全是默认值」的段，而读取端看到
    /// <c>HasAny == true</c> 就把全局音量覆盖成 0 —— 症状是「回放突然没声音」，
    /// 且与真正静音无法区分。</para>
    /// </summary>
    public bool HasAny => HasVolume || HasOffset || IsMuted;
}