using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

internal static class ReplayKeys
{
    // String keys
    public const string FileName = "file_name";
    public const string KeybdSoundFileName = "keybdsound_file_name";
    public const string KeybdSoundHash = "keybdsoundHash";
    public const string SongName = "song_name";
    public const string ArtistName = "artist_name";
    public const string SceneName = "scene_name";
    public const string JudgeMode = "judge_mode";
    public const string InternalLevelName = "internalLevelName";
    public const string LevelPath = "level_path";
    public const string HitMarginLimitKey = "hitMarginLimit";
    public const string HoldBehaviorKey = "holdBehavior";
    public const string LoadedMods = "LoadedMods";
    public const string DeviceID = "deviceID";
    public const string ModVersion = "ModVersion";
    public const string JudgmentList = "JudgmentList";

    // Int keys
    public const string StartTile = "start_tile";
    public const string EndTile = "end_tile";
    public const string FloorHash = "floor_hash";
    public const string SpeedHash = "speed_hash";
    public const string TimeHash = "time_hash";
    public const string Pitch = "pitch";
    public const string LevelID = "levelID";
    public const string AudioBufferSize = "audioBufferSize";
    public const string MaximumUsedKeys = "maximumUsedKeys";

    // Bool keys
    public const string IsOfficialLevel = "isOfficialLevel";
    public const string SpeedTrailMode = "SpeedTrailMode";
    public const string QuickPitched = "QuickPitched";
    public const string IfNoFail = "if_nofail";

    // Double keys
    public const string Bpm = "bpm";
    public const string SpeedTrail = "SpeedTrail";
    public const string PercentXacc = "percentXacc";
    public const string PlaybackSpeed = "playbackSpeed";
    public const string KeybdSoundStartTick = "keybdsound_start_tick";
}
