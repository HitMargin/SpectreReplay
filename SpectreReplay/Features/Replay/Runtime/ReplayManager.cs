using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

internal static class ReplayManager
{
    // 回放文件只有两种后缀：.sprp（加密）、.psprp（不加密）。

    internal static string RemoveInvalidPathChars(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;
        char[] invalidPathChars = Path.GetInvalidPathChars();
        StringBuilder sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            bool valid = true;
            if (c < ' ' || c == '\u007f' || c == '/' || c == '\\' || c == ':' || c == '?' || c == '*' || c == '<' || c == '>' || c == '|' || c == '"' || c == '\n')
                valid = false;
            else
            {
                foreach (char ic in invalidPathChars)
                {
                    if (c == ic) { valid = false; break; }
                }
            }
            sb.Append(valid ? c : '_');
        }
        return sb.ToString().Trim();
    }

    private static void EnsureDirectory(string filePath)
    {
        string dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    internal static readonly string[] ReadableExtensions =
    {
        ReplayConstants.Extension,           // .sprp  加密（默认）
        ReplayConstants.ExtensionNoEncrypt,  // .psprp 不解密
    };

    internal static bool SaveReplay(ReplayData replayData, string filePath, bool no_encryption = false)
        => SaveReplayNew(replayData, filePath, no_encryption);

    internal static bool SaveReplayNew(ReplayData replayData, string filePath, bool no_encryption = false)
    {
        // 截断标记：File.WriteAllBytes 内部是 Create 打开（先截断）再写，写到一半失败
        // （磁盘满/IO 错误）会留下残缺文件，而末尾的 File.Exists 会把它当成功——
        // 调用方 SpectreState 据此告诉用户保存成功，用户直到回放时才发现文件坏了。
        bool opened = false;
        try
        {
            var replay = ReplayDecoder.FromReplayData(replayData);
            if (replay == null) return false;
            replay.EndTime = DateTimeOffset.UtcNow;

            byte[] data = ReplayEncoder.Encode(replay);
            string ext = no_encryption ? ReplayConstants.ExtensionNoEncrypt : ReplayConstants.Extension;
            filePath += ext;

            if (!no_encryption)
                data = ReplayCrypto.Encrypt(data);

            EnsureDirectory(filePath);

            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                opened = true;
                fs.Write(data, 0, data.Length);
            }
            Debug.Log("replay saved! (" + ext + ")");
        }
        catch (Exception ex)
        {
            // 仅在已截断目标文件后才删除；编码/加密阶段失败不动原文件
            if (opened)
            {
                try { File.Delete(filePath); } catch { }
            }
            Debug.Log("SaveReplay error: " + ex);
        }
        return File.Exists(filePath);
    }

    internal static void UnLoadReplay(out ReplayData replayData)
    {
        replayData = new ReplayData();
        WavLoader.loaded_clip = null;
        SpectreState.HasKeybdSound = false;
    }

    internal static bool LoadReplay(out ReplayData replayData, string filePath, bool no_encryption = false)
    {
        if (!ReadReplayData(out replayData, filePath, no_encryption))
            return false;
        TryLoadKeybdSound(filePath, replayData);
        Debug.Log("replay load!");
        return true;
    }

    /// <summary>
    /// 只把回放数据读出来，**不产生任何全局副作用**（不碰键盘音、不改 SpectreState）。
    ///
    /// <para>给「点开文件看信息」用：那条路径要拿到元数据，但玩家并没有装载它，
    /// 不该因此改变当前回放的播放状态。</para>
    /// </summary>
    internal static bool ReadReplayData(out ReplayData replayData, string filePath,
        bool no_encryption = false)
    {
        replayData = new ReplayData();
        if (!File.Exists(filePath))
        {
            Debug.LogError("[SpectreReplay] 回放文件不存在: \"" + filePath + "\"");
            return false;
        }
        if (!no_encryption
            && filePath.EndsWith(ReplayConstants.ExtensionNoEncrypt, StringComparison.OrdinalIgnoreCase))
        {
            no_encryption = true;
        }
        byte[] raw = File.ReadAllBytes(filePath);
        if (raw.Length < 8)
        {
            Debug.LogError("[SpectreReplay] 回放文件过短，不是回放文件: \"" + filePath + "\" (" + raw.Length + " B)");
            return false;
        }

        byte[] decrypted;
        if (no_encryption)
            decrypted = raw;
        else
        {
            decrypted = ReplayCrypto.Decrypt(raw);
            if (decrypted == null)
            {
                Debug.LogError("[SpectreReplay] 回放解密失败: \"" + filePath + "\" —— "
                    + "文件可能已损坏、不是回放文件，或是被别的程序的密钥加密过。"
                    + "（本模组的回放不依赖任何外部密钥文件，换机/重装后仍可读）");
                return false;
            }
        }

        Replay replay;
        try
        {
            replay = ReplayDecoder.Decode(decrypted);
        }
        catch (Exception ex)
        {
            // 这一句是「四不像文件」的唯一出口。旧实现在这里会再当旧 JSON 试一次，
            // 于是「不是回放文件」最终被报成「回放文件结构无法识别」，更难懂。
            Debug.LogError("[SpectreReplay] 回放解码失败: \"" + filePath + "\" —— "
                + "文件头不匹配或数据损坏（本模组只认 .sprp / .psprp）: " + ex.Message);
            return false;
        }
        replayData = ReplayDecoder.ToReplayData(replay);
        return true;
    }

    /// <summary>
    /// 读回放的历史入口名。现在与 <see cref="LoadReplay"/> 完全同义——
    /// 旧实现在这里先试 CRP2、再试新格式、最后回落到旧 JSON，三条路已合并成一条。
    /// 保留这个名字是因为界面层（`Pages.cs`）与离屏套件都按它调用。
    /// </summary>
    internal static bool LoadReplayAuto(out ReplayData replayData, string filePath, bool no_encryption = false)
        => LoadReplay(out replayData, filePath, no_encryption);

    private static void TryLoadKeybdSound(string filePath, ReplayData data)
    {
        // 仅当文件名与哈希均非空（即确实录制了键盘音）时才加载，
        // 否则 KeybdSoundFileName 为空串会导致 Path.Combine 退化为目录路径而误报“文件不存在”。
        if (data.KeybdSoundFileName is string fileName
            && data.KeybdSoundHash is string hash
            && !string.IsNullOrEmpty(fileName)
            && !string.IsNullOrEmpty(hash))
        {
            string dir = Path.GetDirectoryName(filePath);
            string wavPath = Path.Combine(dir, fileName);
            WavLoader.loaded_clip = WavLoader.LoadFromFile(wavPath, hash);
            SpectreState.HasKeybdSound = WavLoader.loaded_clip != null;
        }
        else
        {
            SpectreState.HasKeybdSound = false;
        }
    }
}

