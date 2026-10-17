using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

internal static class ReplayEncoder
{
    public static byte[] Encode(Replay replay)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(ReplayConstants.Magic);
            writer.Write(ReplayConstants.FormatVersion);

            WriteMetadata(writer, replay.Metadata);
            WriteStr(writer, replay.EndTime?.ToString("O") ?? "");
            WriteKeyEvents(writer, replay.KeyEvents);
            WriteHitContexts(writer, replay.HitContexts);
        }

        var compressed = ReplayCompression.Compress(ms.ToArray());
        return compressed;
    }

    private static void WriteMetadata(BinaryWriter w, Replay.ReplayMetadata m)
    {
        w.Write(ReplayConstants.MagicMetadata);
        w.Write(ReplayConstants.MetadataVersion);
        w.Write(m.StartingFloorId);
        w.Write(m.TotalFloorCount);
        WriteStr(w, m.SceneName);
        WriteStr(w, m.SongName);
        WriteStr(w, m.ArtistName);
        WriteStr(w, m.FileName);
        WriteStr(w, m.JudgeMode);
        WriteStr(w, m.LevelPath);
        WriteStr(w, m.InternalLevelName);
        WriteStr(w, m.HitMarginLimit);
        WriteStr(w, m.HoldBehavior);
        WriteStr(w, m.LoadedMods);
        WriteStr(w, m.DeviceID);
        WriteStr(w, m.ModVersion);
        WriteStr(w, m.KeybdSoundFileName);
        WriteStr(w, m.KeybdSoundHash);
        w.Write(m.StartTile);
        w.Write(m.FloorHash);
        w.Write(m.SpeedHash);
        w.Write(m.TimeHash);
        w.Write(m.Pitch);
        w.Write(m.LevelID);
        w.Write(m.AudioBufferSize);
        w.Write(m.Bpm);
        w.Write(m.SpeedTrail);
        w.Write(m.PlaybackSpeed);
        w.Write(m.KeybdSoundStartTick);
        w.Write(m.IsOfficialLevel);
        w.Write(m.SpeedTrailMode);
        w.Write(m.QuickPitched);
        w.Write(m.IfNoFail);
        w.Write(m.StartTime?.ToUnixTimeMilliseconds() ?? long.MinValue);
        WriteStr(w, m.SpVersion);
        w.Write(m.PercentXacc);
        w.Write(m.MaximumUsedKeys);
        WriteStr(w, m.JudgmentList);
    }

    private static void WriteStr(BinaryWriter w, string s) => w.Write(s ?? "");

    private static void WriteKeyEvents(BinaryWriter w, List<KeyEvent> events)
    {
        w.Write(ReplayConstants.MagicKeyEvent);
        w.Write(ReplayConstants.KeyEventVersion);
        w.Write(events.Count);
        foreach (var e in events)
        {
            w.Write(e.SongPosition);
            w.Write(e.KeyCode);
            w.Write(e.IsPressed);
        }
    }

    private static void WriteHitContexts(BinaryWriter w, List<HitContext> contexts)
    {
        w.Write(ReplayConstants.MagicHitContext);
        w.Write(ReplayConstants.HitContextVersion);
        w.Write(contexts.Count);
        foreach (var h in contexts)
        {
            w.Write(h.CurrentFloorID);
            w.Write(h.CurrAngle);
            w.Write(h.OverloadCounter);
            w.Write(h.NoFailHit);
            w.Write(h.IsAuto);
            w.Write(h.NextFloorAuto);
            w.Write(h.CachedAngle);
            w.Write(h.TargetExitAngle);
            w.Write(h.MidspinInfiniteMargin);
            w.Write(h.RDC_auto);
            w.Write(h.curFreeRoamSection);
            // v2 追加：r150+ 异步输入命中 tick（旧版录制恒 null，写 0 长度标记）
            w.Write(h.HitTick.HasValue);
            if (h.HitTick.HasValue)
                w.Write(h.HitTick.Value);
        }
    }
}
