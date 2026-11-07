using System;
using System.IO;
using SpectreReplay.Features.Replay;

namespace UiPreview;

/// <summary>
/// 离线解包 .sprp：报告元数据与命中/按键事件计数。
/// 用法：UiPreview --inspect <file.sprp> [more.sprp ...]
/// </summary>
internal static class OfflineInspect
{
    internal static int Run(string[] files)
    {
        int rc = 0;
        foreach (var path in files)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                byte[] plain = ReplayCrypto.Decrypt(raw);
                Replay replay = ReplayDecoder.Decode(plain);
                ReplayData data = ReplayDecoder.ToReplayData(replay);
                Console.WriteLine("== " + Path.GetFileName(path) + " (" + raw.Length + " B, plain "
                    + plain.Length + " B)");
                Console.WriteLine("   Artist  = [" + (data.ArtistName ?? "<null>") + "]");
                Console.WriteLine("   Song    = [" + (data.SongName ?? "<null>") + "]");
                Console.WriteLine("   Scene   = [" + (data.SceneName ?? "<null>") + "]");
                Console.WriteLine("   Path    = [" + (data.LevelPath ?? "<null>") + "]");
                Console.WriteLine("   Start   = " + (data.StartTime?.ToString("O") ?? "<null>"));
                Console.WriteLine("   Hits    = " + (data.HitSnapshots?.Count.ToString() ?? "<null>"));
                Console.WriteLine("   KeyEvts = " + (data.KeyEvents?.Count.ToString() ?? "<null>"));
                Console.WriteLine("   Start/EndTile = " + data.StartTile + " / " + data.EndTile);
                Console.WriteLine("   JudgmentList = [" + (data.JudgmentList ?? "<null>") + "]");
            }
            catch (Exception ex)
            {
                Console.WriteLine("!! " + Path.GetFileName(path) + ": " + ex.GetType().Name + " " + ex.Message);
                rc = 1;
            }
        }
        return rc;
    }
}
