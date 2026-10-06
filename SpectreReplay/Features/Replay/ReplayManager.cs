using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using static SpectreReplay.Features.Replay.ReplayKeys;

namespace SpectreReplay.Features.Replay;

internal static class ReplayManager
{
    public enum ReplaySaveFormat
    {
        New,   // sprp / psprp
        Crp2,  // crpl2 / pcrpl2
        Json   // crpl / pcrpl
    }

    private class ConfigData
    {
        public CompactReplayData CompactReplayFile { get; set; }
    }

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
    /// `LoadReplayAuto` 能读的全部扩展名（含不解密的那些变体）。
    ///
    /// **为什么需要这个属性**：回放列表原先只 glob `ReplayConstants.Extension`
    /// （.sprp，也就是默认的 New 格式）。可本类是**三种格式全能读**的——
    /// `.sprp` 走 <see cref="ReplayDecoder"/>、`.crpl2` 走
    /// <see cref="ReplayCrp2Codec.OptimizedBinaryToCompact"/>、`.crpl` 走
    /// JSON 反序列化（<see cref="LoadReplay"/>）。只列一种的后果是另外两种
    /// 文件在界面上完全不存在：玩家从 Spectre 拷来的、或改用 Crp2 格式录的
    /// 回放都点不了。
    ///
    /// 扩展名知识集中在这一处（与 SaveReplay* 各方法的 ext 同行维护），
    /// 别处不写死字面量——格式一改，写死的那边会静默失效。
    /// </summary>
    internal static readonly string[] ReadableExtensions =
    {
        ".crpl2", ".pcrpl2",   // Crp2（优化二进制 + CRP2 magic）
        ".crpl",  ".pcrpl",    // Json（旧版）
        ReplayConstants.Extension,           // .sprp  New（默认）
        ReplayConstants.ExtensionNoEncrypt,  // .psprp New 不解密
    };

    internal static bool SaveReplay(LegacyReplayData replayData, string filePath, bool no_encryption = false)
        => SaveReplay(replayData, filePath, ReplaySaveFormat.New, no_encryption);

    internal static bool SaveReplay(LegacyReplayData replayData, string filePath, ReplaySaveFormat format, bool no_encryption = false)
    {
        switch (format)
        {
            case ReplaySaveFormat.Json:
                return SaveReplayJson(replayData, filePath, no_encryption);
            case ReplaySaveFormat.Crp2:
                return SaveReplayOptimized(replayData, filePath, no_encryption);
            default:
                return SaveReplayNew(replayData, filePath, no_encryption);
        }
    }

    internal static bool SaveReplayNew(LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        // 截断标记：File.WriteAllBytes 内部是 Create 打开（先截断）再写，写到一半失败
        // （磁盘满/IO 错误）会留下残缺文件，而末尾的 File.Exists 会把它当成功——
        // 调用方 SpectreState 据此告诉用户保存成功，用户直到回放时才发现文件坏了。
        // SaveReplayOptimized 早已按同样的理由处理，这里对齐。
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

    internal static bool SaveReplayJson(LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        // 同 SaveReplayNew：写失败留下的截断文件会被末尾的 File.Exists 误判为成功
        bool opened = false;
        try
        {
            string ext = no_encryption ? ".pcrpl" : ".crpl";
            filePath += ext;
            string json = JsonConvert.SerializeObject(replayData, Formatting.Indented);
            json = DemoteJsonFieldNames(json);
            byte[] data = Encoding.UTF8.GetBytes(json);
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
            if (opened)
            {
                try { File.Delete(filePath); } catch { }
            }
            Debug.Log("SaveReplayJson error: " + ex);
        }
        return File.Exists(filePath);
    }

    internal static bool SaveReplayOptimized(LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        // 扩展名在 try 外拼好：catch 里才知道要清理哪个文件
        string fullPath = filePath + (no_encryption ? ".pcrpl2" : ".crpl2");
        bool opened = false;
        try
        {
            ReplayCrp2Codec.ConvertToCompact(replayData, out CompactReplayData compact);
            byte[] body = ReplayCrp2Codec.CompactToOptimizedBinary(compact);
            using (FileStream fs = new FileStream(fullPath, FileMode.Create))
            {
                opened = true;
                fs.Write(new byte[4] { 67, 82, 80, 50 }, 0, 4);
                fs.Write(BitConverter.GetBytes(2), 0, 4);
                if (!no_encryption)
                    body = ReplayCrypto.Encrypt(body);
                fs.Write(body, 0, body.Length);
            }
            Debug.Log("replay saved!");
        }
        catch (Exception ex)
        {
            // 分段写中途失败会留下残缺文件：File.Exists 把它当成功，还会挡住重转。
            // 仅在已截断目标文件后才删除；编码阶段失败不动原文件
            if (opened)
            {
                try { File.Delete(fullPath); } catch { }
            }
            Debug.Log(ex);
        }
        return File.Exists(fullPath);
    }

    internal static void UnLoadReplay(out LegacyReplayData replayData)
    {
        replayData = new LegacyReplayData();
        WavLoader.loaded_clip = null;
        SpectreState.HasKeybdSound = false;
    }

    internal static bool LoadReplay(out LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        replayData = new LegacyReplayData();
        if (!File.Exists(filePath))
        {
            Debug.LogError("[SpectreReplay] 回放文件不存在: \"" + filePath + "\"");
            return false;
        }
        byte[] raw = File.ReadAllBytes(filePath);
        if (!no_encryption)
        {
            byte[] decrypted = ReplayCrypto.Decrypt(raw);
            if (decrypted == null)
            {
                // 用户主动点开的文件解不开：最可能是选错文件、文件损坏，
                // 或该回放是用另一把密钥录的。原来只留一行英文日志，
                // 用户既看不懂也不知道下一步该干什么。
                Debug.LogError("[SpectreReplay] 回放解密失败: \"" + filePath + "\" —— "
                    + "文件可能已损坏、不是回放文件，或是用不同密钥录制的（密钥由本程序集内的 SHA256(\"qwerty\") 派生，不随版本变化）");
                return false;
            }
            raw = decrypted;
        }
        try
        {
            string text = Encoding.UTF8.GetString(raw);
            text = NormalizeJsonFieldNames(text);
            ConfigData configData = JsonConvert.DeserializeObject<ConfigData>(text);
            if (configData?.CompactReplayFile != null)
            {
                ReplayCrp2Codec.ConvertFromCompact(configData.CompactReplayFile, out replayData);
            }
            else
            {
                LegacyReplayData direct = JsonConvert.DeserializeObject<LegacyReplayData>(text);
                if (direct != null)
                    replayData = direct;
                else
                {
                    Debug.LogError("[SpectreReplay] 回放文件结构无法识别: \"" + filePath
                        + "\" —— 既不是新版（compact/optimized）也不是旧版 JSON 回放格式");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.Log("Error loading data: " + ex.Message);
            return false;
        }
        TryLoadKeybdSound(filePath, replayData);
        Debug.Log("replay load!");
        return true;
    }

    internal static bool LoadReplayAuto(out LegacyReplayData replayData, string filePath, bool no_encryption = false)
    {
        replayData = new LegacyReplayData();
        if (!File.Exists(filePath))
            return false;

        byte[] raw = File.ReadAllBytes(filePath);
        if (raw.Length < 8)
            return false;

        if (raw[0] == 67 && raw[1] == 82 && raw[2] == 80 && raw[3] == 50)
        {
            byte[] body = new byte[raw.Length - 8];
            Buffer.BlockCopy(raw, 8, body, 0, body.Length);
            if (!no_encryption)
            {
                body = ReplayCrypto.Decrypt(body);
                if (body == null) return false;
            }
            var compact = ReplayCrp2Codec.OptimizedBinaryToCompact(body);
            if (compact == null) return false;
            ReplayCrp2Codec.ConvertFromCompact(compact, out replayData);
            TryLoadKeybdSound(filePath, replayData);
            return true;
        }

        byte[] decrypted;
        if (no_encryption)
            decrypted = raw;
        else
        {
            decrypted = ReplayCrypto.Decrypt(raw);
            if (decrypted == null)
                return false;
        }

        try
        {
            var replay = ReplayDecoder.Decode(decrypted);
            replayData = ReplayDecoder.ConvertToOldFormat(replay);
            TryLoadKeybdSound(filePath, replayData);
            return true;
        }
        catch (Exception ex)
        {
            Debug.Log("New format load failed, trying old: " + ex.Message);
        }

        return LoadReplay(out replayData, filePath, no_encryption);
    }

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

    // 旧版 JSON 字段名 (小写) 与新字段名 (PascalCase) 的映射，用于加载/保存时互相转换。
    private static readonly (string old, string modern)[] JsonFieldMappings =
    {
        ("key_event_list", "KeyEvent_list"),
        ("key_code", "KeyCode"),
        ("if_press", "IsPressed"),
        ("songposition", "SongPosition"),
        ("CompactCreplayfile", "CompactReplayFile") // 仅旧 -> 新
    };

    internal static string NormalizeJsonFieldNames(string json)
    {
        foreach (var m in JsonFieldMappings)
            json = json.Replace("\"" + m.old + "\"", "\"" + m.modern + "\"");
        return json;
    }

    internal static string DemoteJsonFieldNames(string json)
    {
        // CompactCreplayfile 仅存在于旧格式，保存时不需降级
        for (int i = 0; i < JsonFieldMappings.Length - 1; i++)
        {
            var m = JsonFieldMappings[i];
            json = json.Replace("\"" + m.modern + "\"", "\"" + m.old + "\"");
        }
        return json;
    }
}
