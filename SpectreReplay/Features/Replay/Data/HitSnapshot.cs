namespace SpectreReplay.Features.Replay;

public class HitSnapshot
{
    public int CurrentFloorID;

    public double CurrAngle;

    public float OverloadCounter;

    public bool NoFailHit;

    public bool IsAuto;

    public bool NextFloorAuto;

    public double CachedAngle;

    public double TargetExitAngle;

    public bool MidspinInfiniteMargin;

    public bool RDC_auto;

    public int curFreeRoamSection;

    // r150+ 异步输入：命中时刻的音频 tick（长按命中的准时判定依据）。
    // 旧版无此概念，恒为 null；序列化兼容见 ReplayEncoder/Decoder 的可选字段处理
    public long? HitTick;
}
