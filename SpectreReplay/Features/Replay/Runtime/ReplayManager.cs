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

    /// <summary>
    /// 嵌入键盘音的负载上限（4 MB）。
    ///
    /// <para>超限就**不嵌入**，退回「回放 + 同名 .wav」的老形态。理由不是省磁盘，
    /// 而是云同步：回放本体一旦带上几十 MB 音频，单文件超限会让整份回放都传不上去，
    /// 现在至少画面还能备份。4 MB 按 8 kHz ADPCM（约 4 KB/s）算是约 17 分钟，
    /// 覆盖正常整曲；真超了就说明录音长得离谱，退回旁挂文件更稳妥。</para>
    /// </summary>
    private const int MaxEmbedPayloadBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 把同目录下的键盘音 wav 压进回放数据（已经有了就跳过）。
    ///
    /// <para><b>为什么在保存时做、而不是在录制结束时做。</b>录制结束到保存之间隔着
    /// 结算画面与可能的失败重试，那段时间里 wav 还在 <c>tmp.wav</c> 上、随时可能
    /// 被下一次录制覆盖。放在保存这一刻读，拿到的一定是这一局最终的那个文件。</para>
    ///
    /// <para><b>失败一律只记日志。</b>嵌入是「顺带把体积做到最小」的优化，
    /// 不该让一次正常保存变成失败：压不出来就继续用旁挂 wav。</para>
    /// </summary>
    private static void TryEmbedKeybdSound(ReplayData replayData, string filePath)
    {
        if (replayData == null || replayData.KeybdAudio.HasData)
            return;
        string name = replayData.KeybdSoundFileName;
        if (string.IsNullOrEmpty(name))
            return;

        string dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            dir = Settings.SavePath;
        if (string.IsNullOrEmpty(dir))
            return;

        // 保存时序：SpectreState.SaveFile 先调本方法写回放，成功之后才把
        // tmp.wav 改名成正式 wav。所以这一刻正式文件通常还不存在，
        // 真正就绪的是 tmp.wav；两个都试，谁在就用谁。
        string[] candidates =
        {
            Path.Combine(dir, name),
            Path.Combine(dir, "tmp.wav"),
        };

        foreach (string wavPath in candidates)
        {
            if (!File.Exists(wavPath))
                continue;
            byte[] wav;
            try
            {
                wav = File.ReadAllBytes(wavPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 读取键盘音失败，本次不嵌入: " + wavPath + " —— " + ex.Message);
                continue;
            }

            // 防「嵌错文件」：tmp.wav 是固定文件名，上一次保存中断时可能残留着
            // 上一局的录音；候选路径里又优先正式名，容易把别人的声音嵌进来。
            // 用本局录制时算好的 PCM 哈希先验一次，对不上就当这个候选不存在。
            string expectedHash = replayData.KeybdSoundHash;
            if (!string.IsNullOrEmpty(expectedHash)
                && (!SpectreState.DebugMode || !SpectreState.debug_skip_Verification)
                && !AudioRecorder.VerifyAudioData(wav, expectedHash))
            {
                Debug.LogWarning("[SpectreReplay] 键盘音与本次录制不符（疑似残留的 tmp.wav），本次不嵌入: " + wavPath);
                continue;
            }

            // 档位：默认「均衡」（16 kHz）；玩家勾了「极限压缩」就用 8 kHz。
            // 设置只在保存这一刻读一次——已存好的回放不会被重压。
            int quality = Settings.KeybdSoundCompact
                ? KeybdAudioCodec.QualityExtreme
                : KeybdAudioCodec.QualityBalanced;
            KeybdAudioBlob blob;
            string err;
            try
            {
                blob = KeybdAudioCodec.Encode(wav, quality, out err);
                // 超限先降一档再试：8 kHz 的负载是一半，多半能塞进去。
                // 玩家本来就选了极限档时没有更低的一档，直接走下面的超限放弃。
                if (quality != KeybdAudioCodec.QualityExtreme
                    && blob.Payload != null && blob.Payload.Length > MaxEmbedPayloadBytes)
                {
                    Debug.Log("[SpectreReplay] 键盘音压缩后仍偏大（" + blob.Payload.Length
                        + " 字节），改用极限档重压");
                    blob = KeybdAudioCodec.Encode(wav, KeybdAudioCodec.QualityExtreme, out err);
                }
            }
            catch (Exception ex)
            {
                // 编解码器按约定不抛；真抛了也只放弃嵌入，绝不让一次正常保存变成失败。
                Debug.LogWarning("[SpectreReplay] 压缩键盘音时出错，保留旁挂 wav: " + ex);
                return;
            }

            if (!blob.HasData)
            {
                Debug.LogWarning("[SpectreReplay] 键盘音压缩失败，保留旁挂 wav: " + err);
                return;
            }
            if (blob.Payload.Length > MaxEmbedPayloadBytes)
            {
                Debug.Log("[SpectreReplay] 键盘音压缩后仍超限（" + blob.Payload.Length
                    + " 字节），本次不嵌入，保留旁挂 wav");
                return;
            }

            replayData.KeybdAudio = blob;
            Debug.Log("[SpectreReplay] 键盘音已嵌入回放：" + blob.SampleRate + "Hz / "
                + blob.SampleCount + " 样本 / " + blob.Payload.Length + " 字节（原始 wav "
                + wav.Length + " 字节）");
            return;
        }
    }

    /// <summary>
    /// 把「这一份回放」的键盘音覆盖值写回它的文件（音量 / 静音 / 偏移）。
    ///
    /// <para><b>为什么覆盖值要写进文件、而不是本地配置。</b>这样它跟着文件走：
    /// 上传 Steam 云、换一台机器、发给别人，调好的偏移都还在。代价是改一次
    /// 就要重新编码整份回放，所以界面上的保存是**显式按钮**，不是每拖一下就写。</para>
    ///
    /// <para><b>重写时绝不重新嵌入键盘音。</b>走 <c>embedKeybdSound: false</c>：
    /// 否则从磁盘重存一份「本来没有音频的旧回放」时，会把当前残留的 tmp.wav
    /// 顺手压进去，等于凭空长出一段别人的录音。</para>
    ///
    /// <para>已知副作用：重写会把 <c>EndTime</c> 刷新成现在（<see cref="SaveReplayNew"/> 的
    /// 既有行为）。目前没有任何界面或逻辑读它，所以先不为此多解一次码。</para>
    /// </summary>
    /// <param name="filePath">回放本体的完整路径；带不带扩展名都接受。</param>
    internal static bool TrySaveKeybdPrefs(ReplayData replayData, string filePath, out string error)
    {
        error = null;
        if (replayData == null)
        {
            error = "no_replay";
            return false;
        }
        if (string.IsNullOrEmpty(filePath))
        {
            // 云端直接装载的那一份没有本地文件，改不了它的字节。
            error = "no_local_file";
            return false;
        }

        // SaveReplayNew 自己会拼扩展名，所以先把路径归一化成「不带扩展名的基名」，
        // 同时记住它是加密那份还是免加密那份——决定写回时用哪个后缀。
        string ext = Path.GetExtension(filePath) ?? "";
        bool noEncryption = string.Equals(ext, ReplayConstants.ExtensionNoEncrypt,
            StringComparison.OrdinalIgnoreCase);
        bool encrypted = string.Equals(ext, ReplayConstants.Extension,
            StringComparison.OrdinalIgnoreCase);
        string basePath = (encrypted || noEncryption)
            ? filePath.Substring(0, filePath.Length - ext.Length)
            : filePath;

        string target = basePath
            + (noEncryption ? ReplayConstants.ExtensionNoEncrypt : ReplayConstants.Extension);
        if (!File.Exists(target))
        {
            error = "not_found";
            return false;
        }

        if (!SaveReplayNew(replayData, basePath, noEncryption, embedKeybdSound: false))
        {
            error = "write_failed";
            return false;
        }
        Debug.Log("[SpectreReplay] 键盘音覆盖值已写回回放：" + target);
        return true;
    }

    internal static bool SaveReplay(ReplayData replayData, string filePath, bool no_encryption = false,
        bool embedKeybdSound = true)
        => SaveReplayNew(replayData, filePath, no_encryption, embedKeybdSound);

    internal static bool SaveReplayNew(ReplayData replayData, string filePath, bool no_encryption = false,
        bool embedKeybdSound = true)
    {
        // 截断标记：File.WriteAllBytes 内部是 Create 打开（先截断）再写，写到一半失败
        // （磁盘满/IO 错误）会留下残缺文件，而末尾的 File.Exists 会把它当成功——
        // 调用方 SpectreState 据此告诉用户保存成功，用户直到回放时才发现文件坏了。
        bool opened = false;
        try
        {
            // 先尝试把键盘音压进本体（失败不影响保存）。必须在 FromReplayData
            // 之前做：那一步会把 KeybdAudio 一起搬进 Replay 模型。
            // embedKeybdSound 为 false 对应「这次保存不要键盘音」（API 的
            // without_sound），那种情况下连旁挂 wav 都不留，更不该嵌进去。
            if (embedKeybdSound) TryEmbedKeybdSound(replayData, filePath);

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
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            // 落盘路径这里以前会直接把异常抛给调用方；统一成 false + 一行错误，
            // 让「读到一半被占用/权限不足」和「文件不存在」在上层是同一类失败。
            Debug.LogError("[SpectreReplay] 回放文件读取失败: \"" + filePath + "\" —— " + ex.Message);
            return false;
        }
        return ReadReplayData(out replayData, raw, filePath, no_encryption);
    }

    /// <summary>
    /// 与上一个重载同一条解码路径，只是**字节已经在内存里**：云端直接回放、
    /// 以及「下载完立刻播」都走这里，不必先把文件落到磁盘再读一遍。
    ///
    /// <para><paramref name="displayName"/> 只用于日志与是否加密的判定（按扩展名），
    /// 不要求它对应真实存在的文件——云端那条本来就还没落盘。</para>
    /// </summary>
    internal static bool ReadReplayData(out ReplayData replayData, byte[] raw,
        string displayName, bool no_encryption = false)
    {
        replayData = new ReplayData();
        if (!no_encryption && displayName != null
            && displayName.EndsWith(ReplayConstants.ExtensionNoEncrypt, StringComparison.OrdinalIgnoreCase))
        {
            no_encryption = true;
        }
        if (raw == null || raw.Length < 8)
        {
            Debug.LogError("[SpectreReplay] 回放数据过短，不是回放文件: \"" + displayName + "\" ("
                + (raw == null ? 0 : raw.Length) + " B)");
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
                Debug.LogError("[SpectreReplay] 回放解密失败: \"" + displayName + "\" —— "
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
            Debug.LogError("[SpectreReplay] 回放解码失败: \"" + displayName + "\" —— "
                + "文件头不匹配或数据损坏（本模组只认 .sprp / .psprp）: " + ex.Message);
            return false;
        }
        replayData = ReplayDecoder.ToReplayData(replay);
        return true;
    }

    /// <summary>
    /// 与 <see cref="LoadReplay"/> 同义。保留此名是因为界面层与离屏套件按它调用。
    /// </summary>
    internal static bool LoadReplayAuto(out ReplayData replayData, string filePath, bool no_encryption = false)
        => LoadReplay(out replayData, filePath, no_encryption);

    /// <summary>
    /// 从内存字节装载回放（云端直接回放、下载后立即回放）。
    ///
    /// <para>与 <see cref="LoadReplay"/> 的唯一差别是「数据从哪来」：这里完全不碰磁盘。
    /// 但它同样会替换键盘音——调用方就是要把它切成当前回放，「只换画面不换声音」
    /// 会让上一份回放的键盘音继续响，那才是真的串音。</para>
    ///
    /// <para><paramref name="wavData"/> 是同名 .wav 的字节，没有就传 null；此时清掉
    /// 旧键盘音，与「读一个没有旁路 wav 的本地文件」结果一致。</para>
    /// </summary>
    internal static bool LoadReplayFromMemory(out ReplayData replayData, byte[] raw,
        string displayName, byte[] wavData = null, bool no_encryption = false)
    {
        if (!ReadReplayData(out replayData, raw, displayName, no_encryption))
            return false;
        TryLoadKeybdSoundFromMemory(replayData, wavData);
        Debug.Log("replay load (memory)!");
        return true;
    }

    /// <summary>
    /// 内存版的键盘音装载：规矩与 <see cref="TryLoadKeybdSound"/> 完全一致
    /// （文件名与哈希都非空，才算这份回放带了键盘音），只是数据源换成 byte[]。
    /// </summary>
    private static void TryLoadKeybdSoundFromMemory(ReplayData data, byte[] wavData)
    {
        // **嵌入优先，与磁盘路径同一条规矩。**云端那份回放多半根本没有旁挂 wav
        // （上传时已经跳过了），必须先看体内有没有音频，否则「从云端播」永远静音。
        if (data.KeybdAudio.HasData)
        {
            TryLoadKeybdSoundFromBlob(data, null);
            return;
        }
        if (data.KeybdSoundFileName is not string fileName
            || data.KeybdSoundHash is not string hash
            || string.IsNullOrEmpty(fileName)
            || string.IsNullOrEmpty(hash))
        {
            SpectreState.HasKeybdSound = false;
            return;
        }
        if (wavData == null || wavData.Length == 0)
        {
            // 回放自称带键盘音，云端却没给同名 wav（上传中断，或单独删掉了）。
            // 不当错误：画面照放，只是没声音。
            Debug.LogWarning("[SpectreReplay] 回放带有键盘音记录，但未取到对应的 wav 数据: " + fileName);
            SpectreState.HasKeybdSound = false;
            return;
        }
        if (!SpectreState.DebugMode || !SpectreState.debug_skip_Verification)
        {
            if (!AudioRecorder.VerifyAudioData(wavData, hash))
            {
                Debug.LogError("[SpectreReplay] 键盘音哈希验证失败（内存）: " + fileName);
                SpectreState.HasKeybdSound = false;
                return;
            }
        }
        WavLoader.loaded_clip = WavLoader.LoadFromMemory(wavData, Path.GetFileNameWithoutExtension(fileName));
        SpectreState.HasKeybdSound = WavLoader.loaded_clip != null;
    }

    private static void TryLoadKeybdSound(string filePath, ReplayData data)
    {
        // **嵌入优先**：新回放的音频就在本体里，磁盘上根本没有同名 wav，
        // 先走这条路能省一次文件存在性检查，也让「新格式一定有声音」成立。
        if (data.KeybdAudio.HasData)
        {
            TryLoadKeybdSoundFromBlob(data, filePath);
            return;
        }

        // 仅当文件名与哈希均非空（即确实录制了键盘音）时才加载，
        // 否则 KeybdSoundFileName 为空串会导致 Path.Combine 退化为目录路径而误报“文件不存在”。
        if (data.KeybdSoundFileName is string fileName
            && data.KeybdSoundHash is string hash
            && !string.IsNullOrEmpty(fileName)
            && !string.IsNullOrEmpty(hash)
            && IsPureFileName(fileName))
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

    /// <summary>
    /// 从回放体内的压缩块装载键盘音。
    ///
    /// <para>解码失败只降级为「没有声音」并记一行日志：画面照放。嵌入数据是回放
    /// 文件的一部分，文件本身能解码说明主体没坏，音频段坏了不该连画面一起拒绝。</para>
    /// </summary>
    private static void TryLoadKeybdSoundFromBlob(ReplayData data, string replayPath)
    {
        byte[] wav = null;
        try
        {
            wav = KeybdAudioCodec.DecodeToWav(data.KeybdAudio);
        }
        catch (Exception ex)
        {
            Debug.LogError("[SpectreReplay] 嵌入键盘音解码异常: " + ex.Message);
        }
        if (wav == null)
        {
            Debug.LogError("[SpectreReplay] 嵌入键盘音解码失败（"
                + data.KeybdAudio.SampleRate + "Hz / " + data.KeybdAudio.SampleCount + " 样本）: " + replayPath);
            SpectreState.HasKeybdSound = false;
            return;
        }
        // **不能用 KeybdSoundHash 校验这一段。** 那个哈希是对原始 wav 的 PCM 算的，
        // 而嵌入的是有损重编码（降采样 + ADPCM），PCM 必然不同——拿它比对
        // 只会让每一份新回放都被判成「校验失败 → 没声音」。
        // 完整性另有保障：.sprp 的 HMAC 覆盖整段密文，且段内的长度/样本数
        // 都在解码时做了越界校验；.psprp 本来就是「玩家自愿不加密」的形态。
        if (data.KeybdAudio.Codec <= 0 || data.KeybdAudio.Channels != 1)
        {
            Debug.LogError("[SpectreReplay] 嵌入键盘音的段头不可信（codec="
                + data.KeybdAudio.Codec + ", ch=" + data.KeybdAudio.Channels + "）: " + replayPath);
            SpectreState.HasKeybdSound = false;
            return;
        }
        string clipName = data.FileName ?? Path.GetFileNameWithoutExtension(replayPath);
        WavLoader.loaded_clip = WavLoader.LoadFromMemory(wav, clipName);
        SpectreState.HasKeybdSound = WavLoader.loaded_clip != null;
    }

    /// <summary>纯文件名校验：拒绝空、`.`、`..` 与任何目录成分。</summary>
    private static bool IsPureFileName(string name)
        => !string.IsNullOrEmpty(name)
           && name != "." && name != ".."
           && name == Path.GetFileName(name)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// 把这份回放携带的键盘音导出成一个 .wav 文件（给「导出为音频」用）。
    ///
    /// <para>两条来源都要支持：新回放的音频在体内（解码出来），旧回放的音频是
    /// 同目录同名 wav（直接复制字节）。玩家不该被要求知道这份回放是哪一代格式。</para>
    ///
    /// <para>导出目录固定在 <c>SavePath/keybdsound/</c>，不弹保存框：这里要的是一个
    /// 「拿得走」的文件，而不是又一条需要玩家填路径的交互路径。</para>
    /// </summary>
    internal static bool ExportKeybdSound(ReplayData source, out string savedPath, out string error)
    {
        savedPath = null;
        error = null;
        if (source == null)
        {
            error = "no_replay";
            return false;
        }

        byte[] wav = null;
        if (source.KeybdAudio.HasData)
        {
            try
            {
                wav = KeybdAudioCodec.DecodeToWav(source.KeybdAudio);
            }
            catch (Exception ex)
            {
                Debug.LogError("[SpectreReplay] 导出键盘音解码异常: " + ex.Message);
            }
            if (wav == null)
            {
                error = "decode_failed";
                return false;
            }
        }
        else if (source.KeybdSoundFileName is string name && IsPureFileName(name))
        {
            string dir0 = Settings.SavePath;
            if (string.IsNullOrEmpty(dir0))
            {
                error = "no_dir";
                return false;
            }
            string src = Path.Combine(dir0, name);
            if (!File.Exists(src))
            {
                error = "no_sound";
                return false;
            }
            try
            {
                wav = File.ReadAllBytes(src);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 读取旁挂键盘音失败: " + ex.Message);
                error = "write_failed";
                return false;
            }
        }
        else
        {
            error = "no_sound";
            return false;
        }

        try
        {
            string root = Settings.SavePath;
            if (string.IsNullOrEmpty(root))
            {
                error = "no_dir";
                return false;
            }
            string outDir = Path.Combine(root, "keybdsound");
            Directory.CreateDirectory(outDir);

            string baseName = source.FileName ?? "keybdsound";
            baseName = RemoveInvalidPathChars(baseName);
            if (string.IsNullOrEmpty(baseName))
                baseName = "keybdsound";
            string target = Path.Combine(outDir, baseName + ".wav");
            // 重名不覆盖：玩家的用途多半是「再存一份听听」，覆盖上一份等于悄悄毁掉它。
            for (int i = 2; File.Exists(target) && i < 1000; i++)
                target = Path.Combine(outDir, baseName + "_" + i + ".wav");

            File.WriteAllBytes(target, wav);
            savedPath = target;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 导出键盘音写文件失败: " + ex.Message);
            error = "write_failed";
            return false;
        }
    }
}

