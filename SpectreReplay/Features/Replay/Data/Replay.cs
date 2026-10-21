using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.Replay;

public class Replay
{
    public ReplayMetadata Metadata { get; set; } = new();
    public List<KeyEvent> KeyEvents { get; set; } = [];
    public List<HitSnapshot> HitSnapshots { get; set; } = [];
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
