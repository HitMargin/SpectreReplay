using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 回放运行时数据容器（录制 / 播放 / 保存共用）。
/// 值类型字段用可空类型表示"是否有值"，字符串用 null 表示缺失。
/// </summary>
public sealed class ReplayData
{
    #region 字符串字段（旧 strings 字典）
    /// <summary>存档文件名。原键 file_name。</summary>
    public string FileName { get; set; }
    /// <summary>按键音 wav 文件名。原键 keybdsound_file_name。</summary>
    public string KeybdSoundFileName { get; set; }
    /// <summary>按键音 wav 内容哈希。原键 keybdsoundHash。</summary>
    public string KeybdSoundHash { get; set; }
    /// <summary>曲名。原键 song_name。</summary>
    public string SongName { get; set; }
    /// <summary>曲师。原键 artist_name。</summary>
    public string ArtistName { get; set; }
    /// <summary>录制时的场景名。原键 scene_name。</summary>
    public string SceneName { get; set; }
    /// <summary>判定模式（Pure / NotPure 等）。原键 judge_mode。</summary>
    public string JudgeMode { get; set; }
    /// <summary>官方关卡的内部关名。原键 internalLevelName。</summary>
    public string InternalLevelName { get; set; }
    /// <summary>关卡文件路径。原键 level_path。</summary>
    public string LevelPath { get; set; }
    /// <summary>判定宽限设置。原键 hitMarginLimit。</summary>
    public string HitMarginLimit { get; set; }
    /// <summary>长按行为设置。原键 holdBehavior。</summary>
    public string HoldBehavior { get; set; }
    /// <summary>录制时加载的 mod 列表。原键 LoadedMods。</summary>
    public string LoadedMods { get; set; }
    /// <summary>设备标识。原键 deviceID。</summary>
    public string DeviceId { get; set; }
    /// <summary>录制端 mod 版本。原键 ModVersion。</summary>
    public string ModVersion { get; set; }
    /// <summary>判定统计列表（形如 [0,3,17] ）。原键 JudgmentList。</summary>
    public string JudgmentList { get; set; }
    #endregion

    #region 整数字段（旧 ints 字典）
    /// <summary>起点砖块编号。原键 start_tile。</summary>
    public int? StartTile { get; set; }
    /// <summary>终点砖块编号。原键 end_tile。</summary>
    public int? EndTile { get; set; }
    /// <summary>砖块角度表哈希。原键 floor_hash。</summary>
    public int? FloorHash { get; set; }
    /// <summary>速度事件表哈希。原键 speed_hash。</summary>
    public int? SpeedHash { get; set; }
    /// <summary>时间事件表哈希。原键 time_hash。</summary>
    public int? TimeHash { get; set; }
    /// <summary>音高。原键 pitch。</summary>
    public int? Pitch { get; set; }
    /// <summary>关卡 ID。原键 levelID。</summary>
    public int? LevelId { get; set; }
    /// <summary>音频缓冲区大小。原键 audioBufferSize。</summary>
    public int? AudioBufferSize { get; set; }
    /// <summary>录制中同时按下的最大键数。原键 maximumUsedKeys。</summary>
    public int? MaximumUsedKeys { get; set; }
    #endregion

    #region 布尔字段（旧 bools 字典）
    /// <summary>是否为官方关卡。原键 isOfficialLevel。</summary>
    public bool? IsOfficialLevel { get; set; }
    /// <summary>是否开启速度拖尾。原键 SpeedTrailMode。</summary>
    public bool? SpeedTrailMode { get; set; }
    /// <summary>是否变速不变调。原键 QuickPitched。</summary>
    public bool? QuickPitched { get; set; }
    /// <summary>是否无失败模式。原键 if_nofail。</summary>
    public bool? IfNoFail { get; set; }
    #endregion

    #region 浮点字段（旧 doubles 字典）
    /// <summary>BPM。原键 bpm。</summary>
    public double? Bpm { get; set; }
    /// <summary>速度拖尾强度。原键 SpeedTrail。</summary>
    public double? SpeedTrail { get; set; }
    /// <summary>XAcc 百分比。原键 percentXacc。</summary>
    public double? PercentXacc { get; set; }
    /// <summary>播放速度倍率。原键 playbackSpeed。</summary>
    public double? PlaybackSpeed { get; set; }
    /// <summary>按键音的起始 tick；播放时按需延后计算。原键 keybdsound_start_tick。</summary>
    public double? KeybdSoundStartTick { get; set; }
    #endregion

    /// <summary>按键事件流。旧名 KeyEvent_list。</summary>
    public List<KeyEvent> KeyEvents { get; set; } = [];

    /// <summary>判定上下文流。旧名 HitContext_list。</summary>
    public List<HitSnapshot> HitSnapshots { get; set; } = [];

    /// <summary>
    /// 回放元数据是否齐全。等价于旧版对四张必需键字典逐个 ContainsKey，
    /// 供保存前的完整性检查使用（判定指标见 <see cref="FirstMissingRequiredKey"/>）。
    /// </summary>
    public bool HasRequiredMetadata => FirstMissingRequiredKey == null;

    /// <summary>返回第一个缺失的必需字段名；字段齐全时返回 null。</summary>
    public string FirstMissingRequiredKey
    {
        get
        {
            if (SongName == null) return "SongName";
            if (SceneName == null) return "SceneName";
            if (InternalLevelName == null) return "InternalLevelName";
            if (JudgeMode == null) return "JudgeMode";
            if (!IsOfficialLevel.HasValue) return "IsOfficialLevel";
            if (!SpeedTrailMode.HasValue) return "SpeedTrailMode";
            if (!QuickPitched.HasValue) return "QuickPitched";
            if (!FloorHash.HasValue) return "FloorHash";
            if (!TimeHash.HasValue) return "TimeHash";
            if (!SpeedHash.HasValue) return "SpeedHash";
            if (!Pitch.HasValue) return "Pitch";
            if (!StartTile.HasValue) return "StartTile";
            if (!Bpm.HasValue) return "Bpm";
            if (!PlaybackSpeed.HasValue) return "PlaybackSpeed";
            if (!SpeedTrail.HasValue) return "SpeedTrail";
            return null;
        }
    }

    /// <summary>清空为"一张空回放"的状态。旧名 reset()。</summary>
    public void Reset()
    {
        FileName = null;
        KeybdSoundFileName = null;
        KeybdSoundHash = null;
        SongName = null;
        ArtistName = null;
        SceneName = null;
        JudgeMode = null;
        InternalLevelName = null;
        LevelPath = null;
        HitMarginLimit = null;
        HoldBehavior = null;
        LoadedMods = null;
        DeviceId = null;
        ModVersion = null;
        JudgmentList = null;
        StartTile = null;
        EndTile = null;
        FloorHash = null;
        SpeedHash = null;
        TimeHash = null;
        Pitch = null;
        LevelId = null;
        AudioBufferSize = null;
        MaximumUsedKeys = null;
        IsOfficialLevel = null;
        SpeedTrailMode = null;
        QuickPitched = null;
        IfNoFail = null;
        Bpm = null;
        SpeedTrail = null;
        PercentXacc = null;
        PlaybackSpeed = null;
        KeybdSoundStartTick = null;
        KeyEvents.Clear();
        HitSnapshots.Clear();
    }
}
