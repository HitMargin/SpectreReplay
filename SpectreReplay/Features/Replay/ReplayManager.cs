using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

internal static class ReplayManager
{
    // **回放只有一种格式**：`.sprp`（加密）/ `.psprp`（不加密）。
    //
    // 2026-10-18 用户裁定「重构整个回放文件，删掉旧的回放文件适配」——此前
    // 这套代码背着一整个旧容器世代：
    //   · 读：`.crpl`（旧版 JSON，含 `CompactCreplayfile` 内嵌结构）、
    //         `.crpl2`（CRP2 magic + 优化二进制）；
    //   · 写：`ReplaySaveFormat { New, Crp2, Json }` 三条路径，由
    //         `SpectreState.debug_save_format` 切换。
    // 两者**一并删除**。写侧本来也不可达（`debug_save_format` 全仓只有声明
    // `= New`，没界面、没配置键），但它是「旧格式还能产出」的唯一来源；
    // 读侧留着没有意义——自己不再产出，只认别人十年前的文件，等于把一整代
    // 容器结构永久钉在代码里。
    //
    // **保留**：`ReplayCrypto` 加解密、`.psprp` 不解密变体、
    // `SpectreState.debug_no_encryption`——那是新格式的调试开关，与旧格式无关。

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

    /// <summary>
    /// 回放文件列表要 glob 的扩展名。
    ///
    /// **只有新格式两种**（`.sprp` 加密 / `.psprp` 不解密）。2026-10-18 用户裁定
    /// 删掉旧回放文件适配后，`.crpl` / `.crpl2` 的读写都不再存在，列表自然也
    /// 不该再列出它们——否则玩家点一份旧文件，只会得到「不是回放文件」。
    ///
    /// 扩展名知识集中在这一处（与 <see cref="SaveReplay"/> 的 ext 同行维护），
    /// 别处不写死字面量——格式一改，写死的那边会静默失效。
    /// </summary>
    internal static readonly string[] ReadableExtensions =
    {
        ReplayConstants.Extension,           // .sprp  加密（默认）
        ReplayConstants.ExtensionNoEncrypt,  // .psprp 不解密
    };

    internal static bool SaveReplay(LegacyReplayData replayData, string filePath, bool no_encryption = false)
        => SaveReplayNew(replayData, filePath, no_encryption);

    internal static bool SaveReplayNew(LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        // 截断标记：File.WriteAllBytes 内部是 Create 打开（先截断）再写，写到一半失败
        // （磁盘满/IO 错误）会留下残缺文件，而末尾的 File.Exists 会把它当成功——
        // 调用方 SpectreState 据此告诉用户保存成功，用户直到回放时才发现文件坏了。
        bool opened = false;
        try
        {
            var replay = ReplayDecoder.ConvertFromOldFormat(replayData);
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

    internal static void UnLoadReplay(out LegacyReplayData replayData)
    {
        replayData = new LegacyReplayData();
        WavLoader.loaded_clip = null;
        SpectreState.HasKeybdSound = false;
    }

    /// <summary>
    /// 读回放。**只有新格式一条路**：解密（或 `.psprp` 不解密）→ <see cref="ReplayDecoder"/>。
    ///
    /// 旧的 `LoadReplay`（`.crpl` 旧版 JSON / `CompactCreplayfile` 内嵌结构）与
    /// `LoadReplayAuto` 里的 CRP2 magic 分支已按 2026-10-18 用户裁定删除。
    /// 那时的兜底是「新格式解不开就当旧 JSON 再试一次」——它把**任何损坏文件**
    /// 都拖进一条慢路径，且失败后只吐一句笼统的错误，玩家分不清是文件坏了、
    /// 选错了、还是版本不对。现在一步到位，错误信息更具体。
    /// </summary>
    internal static bool LoadReplay(out LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        replayData = new LegacyReplayData();
        if (!File.Exists(filePath))
        {
            Debug.LogError("[SpectreReplay] 回放文件不存在: \"" + filePath + "\"");
            return false;
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
                // 用户主动点开的文件解不开：最可能是选错文件、文件损坏，
                // 或该回放是用另一把密钥录的。原来只留一行英文日志，
                // 用户既看不懂也不知道下一步该干什么。
                Debug.LogError("[SpectreReplay] 回放解密失败: \"" + filePath + "\" —— "
                    + "文件可能已损坏、不是回放文件，或是用不同密钥录制的（密钥由本程序集内的 SHA256(\"qwerty\") 派生，不随版本变化）");
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
        replayData = ReplayDecoder.ConvertToOldFormat(replay);
        TryLoadKeybdSound(filePath, replayData);
        Debug.Log("replay load!");
        return true;
    }

    /// <summary>
    /// 读回放的历史入口名。现在与 <see cref="LoadReplay"/> 完全同义——
    /// 旧实现在这里先试 CRP2、再试新格式、最后回落到旧 JSON，三条路已合并成一条。
    /// 保留这个名字是因为界面层（`Pages.cs`）与离屏套件都按它调用。
    /// </summary>
    internal static bool LoadReplayAuto(out LegacyReplayData replayData, string filePath, bool no_encryption = false)
        => LoadReplay(out replayData, filePath, no_encryption);

    private static void TryLoadKeybdSound(string filePath, LegacyReplayData data)
    {
        // 仅当文件名与哈希均非空（即确实录制了键盘音）时才加载，
        // 否则 KeybdSoundFileName 为空串会导致 Path.Combine 退化为目录路径而误报“文件不存在”。
        if (data.strings.TryGetValue(KeybdSoundFileName, out var fileName)
            && data.strings.TryGetValue(KeybdSoundHash, out var hash)
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

