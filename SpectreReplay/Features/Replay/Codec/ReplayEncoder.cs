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
            WriteHitSnapshots(writer, replay.HitSnapshots);
            // 尾部可选段：空轨迹也写（count=0），布局恒定；旧构建读到即止、不受影响
            WriteCursorTrack(writer, replay.CursorTrack);
            // 键盘音段**必须**在指针轨迹之后：它是「更晚出现的尾部段」，
            // 旧构建读完 CursorTrack 就返回，因而完全不会读到这一段。
            // 没有音频时不写——省 32 字节头，也让「有没有声音」在文件里
            // 只有一种表示（段不存在），而不是「段存在但长度为 0」两种。
            WriteKeybdAudio(writer, replay.KeybdAudio);
            // 播放覆盖段必须排在键盘音之后：它是更晚出现的尾部段，旧构建连
            // 音频段都读不到，因而绝不会读到它。没有覆盖值时一个字节都不写——
            // 与音频段同一约定（「段不存在」就是「没有覆盖值」）。
            WriteKeybdPrefs(writer, replay.KeybdPrefs);
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

    private static void WriteHitSnapshots(BinaryWriter w, List<HitSnapshot> contexts)
    {
        w.Write(ReplayConstants.MagicHitSnapshot);
        w.Write(ReplayConstants.HitSnapshotVersion);
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

    private static void WriteCursorTrack(BinaryWriter w, List<CursorSample> track)
    {
        w.Write(ReplayConstants.MagicCursorTrack);
        w.Write(ReplayConstants.CursorTrackVersion);
        w.Write(track.Count);
        foreach (var s in track)
        {
            w.Write(s.SongPosition);
            w.Write(s.X);
            w.Write(s.Y);
        }
    }

    /// <summary>
    /// 写键盘音段（尾部可选段，位于指针轨迹之后）。
    ///
    /// <para>布局：魔数 / 段版本 / 编码格式 / 采样率 / 声道 / 样本数 / 负载长度 / 负载。
    /// 长度字段必须显式写：负载是压缩数据，长度不能由「读到流尾」推出——
    /// 后面还可能追加更晚的段。</para>
    /// </summary>
    private static void WriteKeybdAudio(BinaryWriter w, KeybdAudioBlob blob)
    {
        if (!blob.HasData)
            return;
        w.Write(ReplayConstants.MagicKeybdAudio);
        w.Write(ReplayConstants.KeybdAudioVersion);
        w.Write(blob.Codec);
        w.Write(blob.SampleRate);
        w.Write(blob.Channels);
        w.Write(blob.SampleCount);
        w.Write(blob.Payload.Length);
        w.Write(blob.Payload);
    }

    /// <summary>
    /// 写键盘音播放覆盖段（尾部可选段，位于键盘音段之后）。
    ///
    /// <para>布局：魔数 / 段版本 / 标志位 / 音量 / 偏移。<b>某一项没有也照写 4 字节</b>
    /// ——布局必须定长，解码端才能靠固定偏移读完整段；「这一项有没有」由标志位
    /// 表达，不是靠字段长短。若按「有才写」做变长布局，读取端遇到未知组合就只能
    /// 猜长度，猜错就是整段错位。</para>
    /// </summary>
    private static void WriteKeybdPrefs(BinaryWriter w, KeybdPrefs prefs)
    {
        if (!prefs.HasAny)
            return;
        w.Write(ReplayConstants.MagicKeybdPrefs);
        w.Write(ReplayConstants.KeybdPrefsVersion);
        byte flags = 0;
        if (prefs.HasVolume) flags |= ReplayConstants.KeybdPrefsFlagHasVolume;
        if (prefs.HasOffset) flags |= ReplayConstants.KeybdPrefsFlagHasOffset;
        if (prefs.IsMuted) flags |= ReplayConstants.KeybdPrefsFlagIsMuted;
        w.Write(flags);
        w.Write(prefs.HasVolume ? prefs.Volume : 0);
        w.Write(prefs.HasOffset ? prefs.OffsetMs : 0);
    }
}
