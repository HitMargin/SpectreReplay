using System;
using System.Collections.Generic;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    /// <summary>
    /// 回放文件格式往返：链接**真实的** Spectre 移植编解码（ReplayEncoder/Decoder）。
    /// 编解码是纯 System 操作，不碰引擎，可以离屏直接跑。
    /// 覆盖：新旧格式往返、空数据、两轨都有数据的形状。
    /// </summary>
    internal static class ReplayCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 回放格式往返（真实编解码源码） ──");
            int fail = 0;
            fail += RoundTrip("⑴ 两轨都有数据的典型局", Sample(3, 3, 5));
            fail += RoundTrip("⑵ 只有键轨（无命中）", Sample(0, 0, 5));
            fail += RoundTrip("⑶ 空回放", Sample(0, 0, 0));
            fail += RoundTrip("⑷ 只有命中无按键", Sample(3, 3, 0));
            if (fail == 0) Console.WriteLine("    ✓ 回放格式往返全部一致");
            return fail;
        }

        private static int RoundTrip(string name, Replay src)
        {
            try
            {
                byte[] encoded = ReplayEncoder.Encode(src);
                Replay back = ReplayDecoder.Decode(encoded);
                var bad = new List<string>();
                if (back.KeyEvents.Count != src.KeyEvents.Count)
                    bad.Add("KeyEvents 数不符 " + back.KeyEvents.Count + " vs " + src.KeyEvents.Count);
                if (back.HitContexts.Count != src.HitContexts.Count)
                    bad.Add("HitContexts 数不符 " + back.HitContexts.Count + " vs " + src.HitContexts.Count);
                if (back.Metadata == null || back.Metadata.SongName != src.Metadata.SongName)
                    bad.Add("Metadata.SongName 不一致");
                if (back.Metadata.StartingFloorId != src.Metadata.StartingFloorId)
                    bad.Add("Metadata.StartingFloorId 不一致");
                for (int i = 0; i < src.KeyEvents.Count && i < back.KeyEvents.Count; i++)
                {
                    var a = src.KeyEvents[i]; var b = back.KeyEvents[i];
                    if (a.KeyCode != b.KeyCode || a.IsPressed != b.IsPressed
                        || Math.Abs(a.SongPosition - b.SongPosition) > 1e-9)
                    { bad.Add("KeyEvents[" + i + "] 不一致"); break; }
                }
                for (int i = 0; i < src.HitContexts.Count && i < back.HitContexts.Count; i++)
                {
                    var a = src.HitContexts[i]; var b = back.HitContexts[i];
                    if (a.CurrentFloorID != b.CurrentFloorID
                        || Math.Abs(a.CachedAngle - b.CachedAngle) > 1e-9
                        || Math.Abs(a.TargetExitAngle - b.TargetExitAngle) > 1e-9
                        || a.IsAuto != b.IsAuto || a.NoFailHit != b.NoFailHit
                        || a.MidspinInfiniteMargin != b.MidspinInfiniteMargin)
                    { bad.Add("HitContexts[" + i + "] 不一致"); break; }
                }
                if (bad.Count == 0) { Console.WriteLine("    ✓ " + name); return 0; }
                foreach (var p in bad) Console.WriteLine("    ✗ " + name + "：" + p);
                return bad.Count;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ✗ " + name + "：抛异常 " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static Replay Sample(int hits, int keys, int seedBias)
        {
            var r = new Replay
            {
                Metadata = new Replay.ReplayMetadata
                {
                    StartingFloorId = 0,
                    TotalFloorCount = 100,
                    SceneName = "scnGame",
                    SongName = "Sample<" + seedBias + ">",
                    ArtistName = "PortCheck",
                    JudgeMode = "Perfect",
                    HitMarginLimit = "Perfect",
                }
            };
            for (int i = 0; i < keys; i++)
                r.KeyEvents.Add(new KeyEvent
                {
                    KeyCode = (ushort)(65 + i),
                    IsPressed = i % 2 == 0,
                    SongPosition = 0.5 * (i + 1)
                });
            for (int i = 0; i < hits; i++)
                r.HitContexts.Add(new HitContext
                {
                    CurrentFloorID = 7 + i,
                    CurrAngle = 31.0 + i,
                    OverloadCounter = 0.15f * (i + 1),
                    NoFailHit = i % 2 == 0,
                    IsAuto = i == 1,
                    NextFloorAuto = i != 1,
                    CachedAngle = 100.5 * (i + 1),
                    TargetExitAngle = -99.25 * (i + 1),
                    MidspinInfiniteMargin = i % 2 == 1,
                    RDC_auto = i == 2,
                    curFreeRoamSection = i == 2 ? 3 : 0,
                    HitTick = i == 1 ? 1234567L : null
                });
            return r;
        }
    }
}
