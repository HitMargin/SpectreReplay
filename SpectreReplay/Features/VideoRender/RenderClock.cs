using System;

namespace SpectreReplay.Features.VideoRender;

/// <summary>
/// 确定性渲染时钟：帧号驱动一切，与音频设备/真实耗时完全脱钩。
/// （OrbitRender RenderClock 同款思路）
///
/// 核心公式：
///   Time = FrameIndex / Fps          ← 每帧固定步进
///   DspTime = DspOrigin + Time       ← DSP 锚点 + 帧时间
///   SongPosition = (DspTime - dspStart - calibration) × pitch - offset
///
/// 使用方式：
///   渲染开始 → AnchorDsp(当前真实 dspTime) → 每帧 Advance()
///   scrConductor.Update 的 Transpiler 把 AudioSettings.dspTime 换成 DspTime
///   → 游戏自己的节拍推进/相机/hitsound 全部确定性化，无需拦截 songposition
/// </summary>
internal sealed class RenderClock
{
    internal int Fps { get; }
    internal long FrameIndex { get; private set; }
    internal double Time => FrameIndex / (double)Fps;

    /// <summary>DSP 锚点（渲染起点的真实 AudioSettings.dspTime，之后不再变）</summary>
    internal double DspOrigin { get; private set; }

    /// <summary>游戏读到的"DSP 时间"= 锚点 + 帧时间</summary>
    internal double DspTime => DspOrigin + Time;

    /// <summary>每帧固定步长（替代 Time.unscaledDeltaTime）</summary>
    internal float Delta => 1f / Fps;

    internal RenderClock(int fps)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        Fps = fps;
    }

    /// <summary>在帧 0 之前锚定到当前真实 DSP 时间（渲染循环起拍时调用一次）</summary>
    internal void AnchorDsp(double origin)
    {
        if (FrameIndex != 0 || double.IsNaN(origin) || double.IsInfinity(origin))
            return; // 静默跳过（已在推进中或无效锚点），不炸
        DspOrigin = origin;
    }

    /// <summary>游戏公式同款：从 DSP 时钟算谱面位置</summary>
    internal double SongPosition(double dspStart, double pitch, double offset, double calibration)
        => (DspTime - dspStart - calibration) * pitch - offset;

    /// <summary>推进一步（渲染循环每帧调用一次）</summary>
    internal void Advance()
    {
        checked { FrameIndex++; }
    }
}
