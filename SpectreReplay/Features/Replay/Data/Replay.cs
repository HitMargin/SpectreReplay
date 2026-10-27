using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

public class Replay
{
    public ReplayMetadata Metadata { get; set; } = new();
    public List<KeyEvent> KeyEvents { get; set; } = [];
    public List<HitSnapshot> HitSnapshots { get; set; } = [];
    /// <summary>指针轨迹（容器尾部的可选段；旧文件没有 → 空表）。</summary>
    public List<CursorSample> CursorTrack { get; set; } = [];
    /// <summary>
    /// 嵌入的键盘音（容器尾部的可选段；旧文件没有 → <c>HasData == false</c>）。
    ///
    /// <para>与 <see cref="CursorTrack"/> 同一套扩展方式：追加在最后一段之后，
    /// 旧构建读到未知魔数就停止，不会因此读坏文件。</para>
    /// </summary>
    public KeybdAudioBlob KeybdAudio { get; set; }
    /// <summary>
    /// 这一份回放自带的键盘音播放覆盖值（容器尾部的可选段；旧文件没有 →
    /// 全默认，<see cref="KeybdPrefs.HasAny"/> 为 false，播放完全跟随全局设置）。
    ///
    /// <para>**刻意不放进 metadata**：metadata 是定长顺序结构，往里加字段会让
    /// 旧构建从第一个新字段起整体错位——那正是「不做版本号也能兼容」的反例。
    /// 尾部可选段则相反：旧构建读到 CursorTrack 就返回，永远看不到它。</para>
    /// </summary>
    public KeybdPrefs KeybdPrefs { get; set; }
    /// <summary>
    /// When this replay ended (UTC). Must be serialized in <see cref="ReplayEncoder"/> and <see cref="ReplayDecoder"/>
    /// to persist across save/load. Written as an ISO 8601 string in metadata.
    /// </summary>
    public DateTimeOffset? EndTime { get; set; }

    public class ReplayMetadata
    {
        public int StartingFloorId;
        public int TotalFloorCount;
        public string SceneName = "";
        public string SongName = "";
        public string ArtistName = "";
        public string FileName = "";
        public string JudgeMode = "";
        public string LevelPath = "";
        public string InternalLevelName = "";
        public string HitMarginLimit = "";
        public string HoldBehavior = "";
        public string LoadedMods = "";
        public string DeviceID = "";
        public string ModVersion = "";
        public string KeybdSoundFileName = "";
        public string KeybdSoundHash = "";

        public int StartTile;
        public int FloorHash;
        public int SpeedHash;
        public int TimeHash;
        public int Pitch;
        public int LevelID;
        public int AudioBufferSize;

        public double Bpm;
        public double SpeedTrail;
        public double PlaybackSpeed;
        /// <summary>
        /// 键盘音在歌曲时间轴上的起点。**NaN = 没有基准**（写入端把 <c>null</c> 落成 NaN，
        /// 0 与负数都是合法值）。解码端按 MetadataVersion 迁移旧文件里的 0 哨兵。
        /// </summary>
        public double KeybdSoundStartTick;

        public bool IsOfficialLevel;
        public bool SpeedTrailMode;
        public bool QuickPitched;
        public bool IfNoFail;

        public double PercentXacc;
        public int MaximumUsedKeys;
        public string JudgmentList = "";

        public DateTimeOffset? StartTime;
        public string SpVersion = "";
    }
}
