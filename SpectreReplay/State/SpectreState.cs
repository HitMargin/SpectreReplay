using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SpectreReplay.API;
using SpectreReplay.Features.Compat;
using SpectreReplay.Features.Replay;
using static SpectreReplay.Features.Replay.ReplayKeys;
using HarmonyLib;
using MonsterLove.StateMachine;
using SkyHook;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityModManagerNet;
using Object = UnityEngine.Object;

namespace SpectreReplay;

public static class SpectreState
{
    internal static class DataIntegrity
    {
        /// <summary>
        /// 回放数据缺字段就返回 true（名字沿用旧版“Check 为真即有问题”的语义，调用点在 Patch_Lifecycle）。
        /// 必需字段清单现在集中定义在 <see cref="ReplayData.FirstMissingRequiredKey"/>，
        /// 不再用四张字符串键列表 + 字典 ContainsKey 逐项比对——键名写错在旧实现里是静默漏检。
        /// </summary>
        internal static bool Check()
        {
            string missing = data?.FirstMissingRequiredKey;
            if (missing == null) return false;
            Debug.Log("Missing:" + missing);
            return true;
        }
    }

    /// <summary>
    /// 从已载入的回放跳到它录自的那一关（含编辑器）。
    ///
    /// **为什么必须有它。** 回放的启动时机是 <c>scrController.Start_Rewind</c>
    /// ——也就是"重开/换关/进关"时。玩家在设置页点「回放」只是**装弹**，
    /// 必须自己再进那一关才会播。而练习谱/自制谱要找到那一关很麻烦
    /// （退出当前关 → 关卡列表里翻 → 记不清是哪个文件）。
    /// 上游 Spectre 给了「打开关卡」按钮直达，本模组的对应实现就在下面。
    ///
    /// **转场状态设置必须原样**——
    /// 这不是可以随手简化的代码：漏设 <c>previousScene</c> 会让"返回"按钮
    /// 跳错地方，漏设 <c>checkpointNum</c> 会从第 0 块而不是回放起点开始。
    /// </summary>
    internal static class OpenReplayLevel
    {
        /// <summary>
        /// 跳到回放录自的关卡。<paramref name="editorReplay"/> 为真时走编辑器。
        ///
        /// 调用方负责先确认 <c>levelPath</c> 非空且文件存在——这里只做跳转，
        /// 不替调用方判断该不该跳（那属于界面层的决策）。
        /// </summary>
        internal static void Go(string levelPath, bool editorReplay)
        {
            if (string.IsNullOrEmpty(levelPath))
            {
                return;
            }
            // 跳转前必须停掉正在跑的录制/回放：否则旧场景的协程会带着
            // 旧状态跨场景活下来，在新关卡里继续注入按键。
            PlayActions.StopPlaying();
            PlayActions.StopRecording();

            int startTile = data != null && data.StartTile is int stv
                ? Math.Max(0, stv) : 0;

            if (editorReplay)
            {
                // 编辑器：让编辑器加载完自己打开这个谱面，并把选中砖定位到起点。
                // SelectFloor 是异步的（谱面加载要几帧），所以用协程等就绪。
                scnEditor.levelToOpenOnLoad = levelPath;
                GCS.sceneToLoad = "scnEditor";
                GCS.checkpointNum = startTile;
                // 只从非关卡场景进来时才清掉试玩/练习标记——上游原样保留这个判断：
                // 从关卡内跳编辑器时清了会丢当前试玩设置。
                if (!ADOBase.isScnGame)
                {
                    GCS.speedTrialMode = false;
                    GCS.practiceMode = false;
                }
                ADOBase.loader.LoadSceneWithTransition(WipeDirection.StartsFromRight);
                MainThread.StartCoroutine(SelectEditorFloorWhenReady(startTile));
            }
            else
            {
                GCS.checkpointNum = startTile;
                GCS.previousScene = "scnGame";
                // 飙速模式的倍速要一并还原：回放是按当时倍速录的，
                // 不还原会让整份回放的按键位置全错。
                if (data != null && data.SpeedTrailMode is bool stm)
                {
                    GCS.speedTrialMode = stm;
                    if (stm && data.SpeedTrail is double st)
                    {
                        GCS.currentSpeedTrial = (float)st;
                    }
                }
                scrController controller = scrController.instance;
                if (controller != null)
                {
                    // 已经在关卡里：直接换谱面，不重载场景（更快，也不闪白）
                    controller.LoadCustomLevel(levelPath);
                }
                else
                {
                    GCS.sceneToLoad = "scnGame";
                    GCS.customLevelPaths = new[] { levelPath };
                    GCS.loadCustomFromBundle = false;
                    scrController.deaths = 0;
                    ADOBase.loader.LoadSceneWithTransition(WipeDirection.StartsFromRight);
                }
            }
            Debug.Log("[SpectreReplay] open replay level (" + (editorReplay ? "editor" : "game")
                + "): " + levelPath);
        }

        /// <summary>
        /// 跳到回放录自的**官方关卡**。
        ///
        /// **为什么官方关卡能跳、而以前不能。** 以前这里只显示一句"官方关卡没有
        /// 可打开的谱面文件"就收尾——理由是对的（官方关卡确实没有 .adofai 文件），
        /// 但结论下错了：官方关卡不是"没有关卡"，而是**关卡数据烤在游戏里**，
        /// 靠 <c>GCS.internalLevelName</c> 这个字符串（形如 "0-0"、"AR-X"）选，
        /// 而不是靠文件路径。
        ///
        /// **游戏自己的加载链**（反编译 Assembly-CSharp 核实）：
        ///  ① <c>GCS.internalLevelName</c> 非 null ⇒ <c>ADOBase.isInternalLevel</c> 为真；
        ///  ② <c>scnGame</c> 场景重载后，<c>scnGame.Update</c> 在第 3 帧做
        ///     「<c>text = GCS.internalLevelName ?? GCS.customLevelPaths[index]</c>」→
        ///     <c>LoadAndPlayLevel(text)</c>；
        ///  ③ <c>scnGame.Awake</c> 只在 <c>internalLevelName</c> 为 null **且**
        ///     customLevelPaths 为空时才去填默认值——所以只要我们**先**把名字写好，
        ///     它就不会被覆盖。
        ///  ⇒ 我们只需要：清掉 customLevelPaths、写好 internalLevelName、重载 scnGame。
        ///
        /// **必须清 <c>customLevelPaths</c>**：它是 `scnGame.Update` 那个 null 合并的
        /// 另一边，留着上一关的自制谱路径会让关卡选错。
        ///
        /// **关卡名从哪来**：回放录制时 <c>Patch_Lifecycle</c> 就把
        /// <c>GCS.internalLevelName</c> 存进了 <c>data.InternalLevelName</c>
        /// （官方关卡才有值，自制谱是空串）。所以这里**不需要**从 LevelPath 里
        /// 反解——那个字段对官方关卡是 <c>"OfficialLevel &lt;歌名&gt;"</c>，不是关卡 id。
        /// </summary>
        internal static void GoOfficial()
        {
            if (data == null)
            {
                return;
            }
            if (data.InternalLevelName is not string internalName
                || string.IsNullOrEmpty(internalName))
            {
                // 老回放（该字段还没加时录的）没有内部关卡名，跳不了。
                // 这**不是**缺陷，是数据缺失，说清楚就行。
                // **必须走 Ui.Loc，不是 LocalizationManager**：这条文案只存在于
                // 界面层那两张表里（zh/en），14 语言大表里没有它——用错会在
                // LocCheck 那里报"缺 14 种语言"，而运行时则是露出原始键名。
                TriggerMessage(Ui.Loc.T("playback.openlevel.official"), 4f);
                Debug.LogWarning("[SpectreReplay] 该回放没有记录内部关卡名（旧版本录制），无法直达官方关卡");
                return;
            }

            // 与 Go() 同理：跳转前必须停掉录制/回放，否则旧场景的协程会带着
            // 旧状态跨场景活下来，在新关卡里继续注入按键。
            PlayActions.StopPlaying();
            PlayActions.StopRecording();

            int startTile = data.StartTile is int stv
                ? Math.Max(0, stv) : 0;

            GCS.checkpointNum = startTile;
            GCS.previousScene = "scnGame";
            // 官方关卡：**清掉**自制谱路径，只留 internalLevelName 这一条路。
            // 这两个字段是 scnGame.Update 里二选一的两边，同时有值会打架。
            GCS.customLevelPaths = null;
            GCS.loadCustomFromBundle = false;
            GCS.internalLevelName = internalName;
            // 飙速/练习标记必须还原成录制时的样子：回放按键位置是按当时的
            // 速度与起跳点录的，不还原会整体错位。
            if (data.SpeedTrailMode is bool stm)
            {
                GCS.speedTrialMode = stm;
                if (stm && data.SpeedTrail is double st)
                {
                    GCS.currentSpeedTrial = (float)st;
                }
            }
            GCS.sceneToLoad = "scnGame";
            scrController.deaths = 0;
            ADOBase.loader.LoadSceneWithTransition(WipeDirection.StartsFromRight);
            Debug.Log("[SpectreReplay] open replay official level: " + internalName
                + " (startTile=" + startTile + ")");
        }

        /// <summary>
        /// 编辑器打开谱面后，等加载完成（<c>isLoading</c> 熄灭、地板表就绪），
        /// 把选中地板定位到回放起始砖——编辑器试玩是从**当前选中砖**起跳的，
        /// 不定位就会从第 0 块开始，回放位置全错。
        ///
        /// 参考上游 <c>SettingsUI.SelectEditorFloorWhenReady</c>（那份是他们的，
        /// 我们按同样的判据自己写）。**1800 帧上限**是防"打开失败后无限空转"
        /// ——没有它，谱面永远加载不出来时这个协程会一直挂着。
        /// </summary>
        /// **必须写全限定名 `System.Collections.IEnumerator`。** 本文件
        /// 顶部有 `using System.Collections.Generic`，直接写 `IEnumerator`
        /// 会被解析成泛型 `IEnumerator<T>`，报 CS0305（需要 1 个类型参数）。
        /// 别为此加 `using System.Collections`——那会和 Generic 的同名类型打架。
        private static System.Collections.IEnumerator SelectEditorFloorWhenReady(int floor)
        {
            for (int i = 0; i < 1800; i++)
            {
                scnEditor editor = ADOBase.editor;
                if (editor != null && !editor.isLoading
                    && editor.floors != null && editor.floors.Count > floor)
                {
                    // cameraJump 必须 true：这是交互式定位，镜头要跟到起始砖。
                    // （视频渲染里用的是 false——那是离线渲染，镜头由回放接管，
                    //  两种场景的取舍不同，不能照搬。）
                    editor.SelectFloor(editor.floors[floor], cameraJump: true);
                    yield break;
                }
                yield return null;
            }
            Debug.Log("[SpectreReplay] 编辑器定位起始砖超时（第 " + floor + " 块）");
        }
    }

    internal class PlayActions
    {
        internal static void SwitchToPlayMode()
        {
            PlayMode = true;
            RecordMode = false;
            is_playing = false;
            is_recording = false;
            HitLocked = false;
            PendingSave = false;
            LateSaveMode = false;
            PlayIndex = 0;
            KeyEventIndex = 0;
            AllAutoHits = true;
            IgnoreMarkFail = false;
        }

        internal static void SwitchToRecordMode()
        {
            HitLocked = false;
            PlayMode = false;
            RecordMode = true;
            is_playing = false;
            is_recording = false;
            PendingSave = false;
            LateSaveMode = false;
            PlayIndex = 0;
            KeyEventIndex = 0;
            AllAutoHits = true;
            IgnoreMarkFail = false;
            IsFullRun = false;
        }

        /// <summary>
        /// 卸载当前回放，回到"什么都没装弹"的录制态。
        ///
        /// **为什么需要它。** <c>ReplayManager.UnLoadReplay</c> 一直在库里，
        /// 但**零调用点**——界面上只有「回放」没有「卸载」，于是载入一份回放后
        /// 就再也回不到未装弹状态：那颗「回放」按钮会一直高亮，
        /// 玩家想换成"直接打一局录制"必须先重启游戏。
        /// 这与"键盘音三项 / 回放驱动开关 / 保存目录"是同一族缺陷
        /// （数据在、链路通、界面缺），只是这次连界面行都没有。
        ///
        /// **顺序不能改**（照上游 <c>SpectreUiPages.More.cs:40-44</c>）：
        /// ① 先 StopPlaying/StopRecording——回放/录制协程还在跑时清 data，
        ///    它们下一帧会去读已清空的 data，轻则报错重则卡死；
        /// ② 再 UnLoadReplay 换出 data 并清掉键盘音（<c>WavLoader.loaded_clip</c>
        ///    与 <c>HasKeybdSound</c> 不清的话，下一局会带着上一局的键盘音）；
        /// ③ 最后 SwitchToRecordMode 把 PlayMode 翻回 false 并复位所有游标。
        /// </summary>
        internal static void UnloadReplay()
        {
            PlayActions.StopPlaying();
            PlayActions.StopRecording();
            Features.Replay.ReplayManager.UnLoadReplay(out data);
            PlayActions.SwitchToRecordMode();
        }

        // 启停事件只在状态真正翻转时触发（Start/Stop 内部各有守卫，重复调用无害）；
        // 全部启停路径都经 PlayActions 收口，这里是对外事件的唯一挂点。
        // Start* 会把互斥的另一侧标志直接翻转（不经过对应 Stop），此处一并补发 Stopped
        internal static void StopPlaying()
        {
            bool was = is_playing;
            // Stop 内部对场景已卸做了空守卫，此处的 catch 是最后防线：
            // 即使再有意外异常，is_playing 标志已翻转，事件照常触发
            try { ReplayPlayer.Stop(); }
            catch (Exception ex) { Debug.LogException(ex); }
            if (was && !is_playing) SpectreAPI.RaisePlaybackStopped();
        }

        internal static void StartPlaying()
        {
            bool wasPlay = is_playing;
            bool wasRec = is_recording;
            ReplayPlayer.Start();
            if (!wasPlay && is_playing)
            {
                if (wasRec) SpectreAPI.RaiseRecordingStopped();
                SpectreAPI.RaisePlaybackStarted();
            }
        }

        internal static void StartRecording()
        {
            bool wasRec = is_recording;
            bool wasPlay = is_playing;
            ReplayRecorder.Start();
            if (!wasRec && is_recording)
            {
                if (wasPlay) SpectreAPI.RaisePlaybackStopped();
                SpectreAPI.RaiseRecordingStarted();
            }
        }

        internal static void StopRecording()
        {
            bool was = is_recording;
            ReplayRecorder.Stop();
            if (was && !is_recording) SpectreAPI.RaiseRecordingStopped();
        }
    }

    internal static bool ApiBlockPlaying = false;

    internal static bool is_playing = false;

    internal static bool is_recording = false;

    internal static bool? CachedNoFail;

    // 回放期临时套用的判定模式（Difficulty.Lenient/Normal/Strict）。
    // null = 本局没有接管过难度，Stop 时无需还原。
    // 玩家自己的默认难度存在 Persistence 里，这里只改本次会话的 GCS.difficulty。
    internal static Difficulty? CachedDifficulty;

    /// <summary>
    /// 把编辑器里的难度标签刷新成当前 <see cref="GCS.difficulty"/>。
    ///
    /// 游戏自己的 <c>EditorDifficultySelector.UpdateDifficultyDisplay</c> 是 private，
    /// 但它的两个显示字段（<c>editorDifficultyText</c> / <c>bullseyeImage</c>）是 public，
    /// 照同样的写法刷一遍即可。不刷的话：难度已经被我们改了，界面上还显示旧值，
    /// 玩家会以为「判定模式没对齐」。
    /// </summary>
    internal static void SyncEditorDifficultyLabel()
    {
        var selector = scnEditor.instance != null ? scnEditor.instance.editorDifficultySelector : null;
        if (selector == null) return;
        try
        {
            if (selector.editorDifficultyText != null)
                selector.editorDifficultyText.text = RDString.Get("enum.Difficulty." + GCS.difficulty);
            if (selector.bullseyeImage != null)
                selector.bullseyeImage.sprite = RDConstants.data.bullseyeSprites[(int)GCS.difficulty];
        }
        catch (System.Exception ex)
        {
            // 纯显示：刷不上不该影响回放本身
            Debug.Log("[SpectreReplay] 难度标签刷新失败: " + ex.Message);
        }
    }

    internal static void SyncEditorNoFailButton()
    {
        if (scnEditor.instance == null) return;
        var btn = scnEditor.instance.buttonNoFail;
        if (btn == null) return;
        if (data == null || !data.IfNoFail.HasValue) return;
        var img = btn.GetComponent<UnityEngine.UI.Image>();
        img.color = data.IfNoFail == true ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
    }

    internal static bool RecordMode = true;

    internal static bool PlayMode = false;

    internal static bool HitLocked = false;

    internal static bool IgnoreMarkFail = false;

    internal static bool UseAsyncInput = false;

    internal static bool PendingSave = false;

    internal static bool LateSaveMode = false;

    internal static int[] HitMarginCounts = new int[Enum.GetValues(typeof(HitMargin)).Cast<int>().Max() + 1];

    // 仅作引用比较的哨兵（Patch_Recording 里判断是否同一块），不实例化 MonoBehaviour
    internal static scrFloor CurrentHitFloor;

    internal static ReplayData data;

    internal static int PlayIndex = 0;

    internal static int KeyEventIndex = 0;

    internal static bool AllAutoHits = true;

    internal static bool IsFullRun = false;
    internal static bool HasKeybdSound = false;

    internal static HitMarginLimit StoredHitMarginLimit = (HitMarginLimit)0;

    internal static bool DebugMode = false;

    internal static bool debug_skip_Verification = false;

    // 写「不解密」的 `.psprp` 而不是加密的 `.sprp`。**这与旧格式无关**，是新格式的调试开关：
    // 解开加密要拿密钥，排查「文件是不是坏了」时先看明文更省事。保留。
    internal static bool debug_no_encryption = false;

    // 原先这里还有 `debug_save_format`（New/Crp2/Json 三选一）。
    // 它**全仓只有声明一处**、没有任何界面/配置/写入点，即 Crp2 与 Json 两条写路径
    // 从来不可达；2026-10-18 用户裁定删掉旧回放文件适配时一并移除，写侧只留新格式。

    internal static bool debug_hide_text = false;

    internal static bool debug_use_old_hit = false;

    internal static KeyCode save_button = (KeyCode)290;

    internal static KeyCode cancel_save_button = (KeyCode)291;

    internal static byte[] PrevKeyState = new byte[256];

    internal static byte[] CurrKeyState = new byte[256];

    private static readonly ushort[] ExtendedKeys = { 165, 163, 38, 40, 37, 39 };

    private static readonly ushort[] BlockedKeys = { 16, 17, 18, 27 };

    internal static int GetKeyboardState(byte[] pbKeyState)
        => KeyboardSimulation.GetKeyboardState(pbKeyState);

    internal static bool IsExtendedKey(ushort keyCode)
        => Array.IndexOf(ExtendedKeys, keyCode) >= 0;

    internal static bool IsBlockedKey(ushort keyCode)
        => Array.IndexOf(BlockedKeys, keyCode) >= 0;

    internal static float GetAngle()
        => scrController.instance == null ? float.NaN : GetAngle(scrController.instance);

    /// <summary>
    /// 用**调用方持有的那个** controller 取角度。
    ///
    /// 一定要有这个重载：回放推进循环用补丁入参 <c>__instance</c> 判定当前砖块
    /// （那里注释说明了为什么不能用静态 instance——场景卸载/重载的中间态它会指向
    /// 另一个实例），却用无参 <see cref="GetAngle()"/> 取角度。两个 controller 不是
    /// 同一个对象时，砖块取自 A、角度取自 B，而 B 的星球角度可以任意大，
    /// <c>angle &gt;= CurrAngle</c> 一路恒真——整份命中快照在一帧内被吃完。
    /// </summary>
    internal static float GetAngle(scrController controller)
    {
        if (controller == null) return float.NaN;
        if (controller.chosenPlanet == null) return float.NaN;
        float num = (float)(controller.chosenPlanet.angle - controller.chosenPlanet.targetExitAngle);
        if (controller.playerOne != null && !controller.playerOne.planetarySystem.isCW)
        {
            num *= -1f;
        }
        return num;
    }

    internal static bool SaveFile(bool without_sound = false, bool retry = false)
    {
        if (data == null)
        {
            return true;
        }
        string message = "";
        bool success = true;
        string savedPath = null;
        if (retry)
        {
            data.FileName = CreateFileName(safe_mode: true);
            data.KeybdSoundFileName = data.FileName + ".wav";
        }
        // 旧字典版用 ContainsKey 判存在：空串算“有值”。这里必须是 == null，不能换成 IsNullOrEmpty。
        string fileName = data.FileName == null ? CreateFileName() : data.FileName;
        string savePath = Path.Combine(Settings.SavePath, fileName);
        if (ReplayManager.SaveReplay(data, savePath, debug_no_encryption))
        {
            message += LocalizationManager.GetLocalizedText("note.save_success") + "\n";
            savedPath = savePath;
        }
        else
        {
            message += LocalizationManager.GetLocalizedText("note.save_fail") + savePath + "\n";
            success = false;
        }
        if (!without_sound && data.KeybdSoundFileName != null)
        {
            string tmpPath = Path.Combine(Settings.SavePath, "tmp.wav");
            string keybdName = data.KeybdSoundFileName;
            // 键存在但值为空串时（解码自外部/损坏的回放文件），Path.Combine 会退化成
            // "目录\tmp.wav" 这样的路径：要么 File.Move 抛异常，要么把 tmp.wav 移到目录上。
            // ReplayManager.TryLoadKeybdSound 已对同源问题做了这道判断，这里对齐。
            if (string.IsNullOrEmpty(keybdName))
            {
                message += LocalizationManager.GetLocalizedText("note.keybdsoundfile_save_fail") + " (empty name)" + "\n";
            }
            else
            {
                string wavPath = Path.Combine(Settings.SavePath, keybdName);
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        if (File.Exists(wavPath))
                        {
                            File.Delete(wavPath);
                        }
                        File.Move(tmpPath, wavPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log(ex);
                }
                if (!File.Exists(wavPath))
                {
                    message += LocalizationManager.GetLocalizedText("note.keybdsoundfile_save_fail") + wavPath + "\n";
                }
            }
        }
        // **备份放在 wav 处理之后**：回放的键盘音是按「与回放同目录的
        // KeybdSoundFileName」找的（见 ReplayManager.TryLoadKeybdSound），
        // 所以备份目录里也必须有一份同名 wav，否则备份出来的回放**静默没有键盘音**
        // ——文件能开、能播，只是少了声音，玩家会以为原始录制就没录上。
        // **主保存失败时不能走备份。** savedPath 只在 SaveReplay 成功时才赋值
        // （上面声明为 null），失败时它仍是 null，而 `savedPath + mainExt` 在
        // C# 里是**字符串拼接**、不是空引用异常——得到裸串 "sprp"。于是
        // File.Copy 去开一个叫 sprp 的文件、抛 FileNotFoundException，被下面的
        // catch 吞掉后再追加一条「备份失败」：用户看到主保存失败 + 备份失败
        // 两条，真正的原因反而被噪声盖住。
        // 备份的对象是「刚写好的主文件」，主文件不存在时它本就无从谈起。
        if (Settings.Backup && savedPath != null)
        {
            // 备份结果原本被完全忽略：主回放已保存成功时，备份可能因权限受限/磁盘满/
            // 目录不可写而失败，而用户毫无察觉 —— 直到真出事那天才发现"备份早就没了"。
            // 备份失败不影响主回放的成功状态（success），但必须告知用户。
            //
            // **必须传"目录 + 文件名"，不能只传目录。** SaveReplayNew 内部是
            // `filePath += ext`——它把实参当成**含扩展名的完整文件路径**。
            // 原先这里传的是 Path.Combine(mod.Path, "backup")，于是每局都写
            // 同一个 `backup\backup.sprp`，而它还是个**目录名拼上扩展名**的
            // 怪路径。README 承诺的是"为每份回放多留一份副本"——单文件覆盖
            // 等于只有最后一局有备份，前面全丢，且用户看不出来（每局都提示成功）。
            // ⇒ 备份文件名跟主回放同名，只是换个目录。
            //
            // **备份不再重跑整条保存管线，直接复制刚写好的主文件。**
            // 原先这里再调一次 SaveReplay：编码、Brotli、PBKDF2、AES 全部重来一遍，
            // 而每遍 PBKDF2 在 Mono 上要一两百毫秒——一局保存因此翻倍卡顿
            // （2026-10-19 用户报「保存时游戏会卡」）。备份要的是**同一份字节**，
            // 复制在语义上完全等价，且省掉一整套派生与压缩。
            //
            // 复制失败与原先"备份保存失败"同语义：只提示，不算整局保存失败。
            // 扩展名必须跟着主文件走：调试开关会写 `.psprp`（不解密变体），
            // 硬编码 `.sprp` 会让备份指向一个不存在的文件、每局都提示失败。
            string mainExt = debug_no_encryption
                ? ReplayConstants.ExtensionNoEncrypt : ReplayConstants.Extension;
            string backupDir = Path.Combine(Main.mod.Path, "backup");
            string backupPath = Path.Combine(backupDir, fileName) + mainExt;
            bool backupOk = false;
            try
            {
                Directory.CreateDirectory(backupDir);
                File.Copy(savedPath + mainExt, backupPath, true);
                backupOk = true;
            }
            catch (Exception ex)
            {
                Debug.Log("[SpectreReplay] 备份回放失败：" + ex.Message);
            }
            if (!backupOk)
            {
                message += LocalizationManager.GetLocalizedText("note.save_fail") + "backup\n";
            }
            else if (!without_sound
                     && data.KeybdSoundFileName is string backupWav
                     && !string.IsNullOrEmpty(backupWav))
            {
                // 同名 wav 复制过去。失败只提示、不算整局保存失败。
                try
                {
                    string srcWav = Path.Combine(Settings.SavePath, backupWav);
                    if (File.Exists(srcWav))
                    {
                        Directory.CreateDirectory(backupDir);
                        string dstWav = Path.Combine(backupDir, backupWav);
                        File.Copy(srcWav, dstWav, true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Log("[SpectreReplay] 备份键盘音失败：" + ex.Message);
                }
            }
        }
        // 事件放在伴生 wav 改名/备份之后：订阅者此刻能拿到完整的成套文件
        if (savedPath != null)
            SpectreAPI.RaiseReplaySaved(savedPath);
        TriggerMessage(message);
        if (!success && !retry)
        {
            return SaveFile(without_sound, retry: true);
        }
        return success;
    }

    internal enum NotifType { Info, Warning }

    internal static void TriggerMessage(string message, float dur = 3f, NotifType type = NotifType.Info)
    {
        if (debug_hide_text) return;
        // Notification 内部 StartCoroutine + UI 操作，后台线程直调会炸——转回主线程
        if (!MainThread.IsMainThread)
        {
            MainThread.Run(() => TriggerMessage(message, dur, type));
            return;
        }
        try
        {
            var n = Notification.instance;
            n.text.text = message.TrimEnd('\n');
            n.icon.enabled = true;
            n.button.onClick.RemoveAllListeners();
            switch (type)
            {
                case NotifType.Warning:
                    n.icon.sprite = n.warningIcon;
                    n.icon.color = new Color(1f, 0.2f, 0.2f);
                    break;
                default:
                    n.icon.sprite = n.completeIcon;
                    n.icon.color = new Color(0.2f, 0.7215686f, 0.3921569f);
                    break;
            }
            var graphics = n.bar.GetComponentsInChildren<Graphic>(true);
            var states = graphics.Select(g => g.raycastTarget).ToArray();
            foreach (var g in graphics) g.raycastTarget = false;
            n.StartCoroutine(RestoreGraphics(graphics, states, dur));
            var m = PatchManager.GetMethodInfo(typeof(Notification), "SetupNotification",
                [typeof(float), typeof(float), typeof(bool)]);
            m.Invoke(n, [dur, 1f, true]);
        }
        catch (Exception ex)
        {
            Debug.Log(ex);
        }
    }

    private static System.Collections.IEnumerator RestoreGraphics(Graphic[] graphics, bool[] states, float dur)
    {
        yield return new WaitForSecondsRealtime(0.5f + 0.5f + dur + 0.3f);
        for (int i = 0; i < graphics.Length; i++)
            if (graphics[i] != null) graphics[i].raycastTarget = states[i];
    }

    public static bool ReplayHit(scrPlayer player, HitSnapshot hitdata)
        => ReplayPlayer.ReplayHit(player, hitdata);

    internal static void Play_keybd_event(bool flag)
        => ReplayPlayer.PlayKeyboardEvent(flag);

    internal static void Record_kbdstate(bool forceSync = false)
        => ReplayRecorder.RecordKeyboardState(forceSync);

    private static UnityEngine.Events.UnityAction<SkyHook.SkyHookEvent> _asyncKeyListener;

    public static void SetGetKeyAsyncEnabled(bool enabled)
    {
        if (enabled == UseAsyncInput)
            return;
        UseAsyncInput = enabled;
        if (enabled)
        {
            if (_asyncKeyListener == null)
                _asyncKeyListener = AsyncKeyRecorder.OnKeyUpdated;
            SkyHook.SkyHookManager.KeyUpdated.AddListener(_asyncKeyListener);
        }
        else if (_asyncKeyListener != null)
        {
            SkyHook.SkyHookManager.KeyUpdated.RemoveListener(_asyncKeyListener);
        }
    }

    internal static void StopAll()
    {
        if (is_playing)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
        }
        if (is_recording)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_recording"));
        }
        PlayActions.StopPlaying();
        PlayActions.StopRecording();
    }

    internal static void CancelSave()
    {
        if (!PendingSave) return;
        PendingSave = false;
        LateSaveMode = false;
        TriggerMessage(LocalizationManager.GetLocalizedText("note.cancel_save"));
    }

    public static double ComputePercentXacc()
    {
        var tracker = scrController.instance?.playerOne?.marginTracker;
        if (tracker == null || tracker.hitMargins == null || tracker.hitMargins.Count == 0)
            return 0.0;
        return ComputePercentXaccVanilla();
    }

    // 原版口径 xacc：官方四段权重（1 / 0.75 / 0.4 / 0.2）对 margin 桶计数加权。
    // 文件名固定用这个口径——文件名要跨玩家通用可比。
    public static double ComputePercentXaccVanilla()
    {
        var tracker = scrController.instance?.playerOne?.marginTracker;
        if (tracker == null || tracker.hitMargins == null || tracker.hitMargins.Count == 0)
            return 0.0;
        // 权重表按名解析、逐版本复刻（旧版下标与 r150 重排后的下标由兼容层各自持有）：
        //   旧版：1.0×{Perfect,Auto} + 0.75×{Early,LatePerfect} + 0.4×{VeryEarly,VeryLate} + 0.2×{TooEarly,TooLate}
        //   r150：1.0×{XPerfect,PerfectMinus,PerfectPlus} + … + 0.2×{TooEarly,TooLate,FailedFloor}，Auto 不计入
        double numerator = HitMarginCompat.XAccNumerator(HitMarginCounts);
        return numerator / (double)tracker.hitMargins.Count;
    }

    public static double ComputePercentComplete(int curr_floor)
    {
        // lm 已加载但 listFloors 为空的窗口期（转场中/关卡构造中）会让除法得到
        // Infinity/NaN，直接透出到 UI 会显示成 "Infinity%"。分母为 0 时按 0 处理。
        if (scrController.instance != null && ADOBase.lm != null
            && ADOBase.lm.listFloors != null && ADOBase.lm.listFloors.Count > 0)
        {
            return (float)(curr_floor + 1) / (float)ADOBase.lm.listFloors.Count;
        }
        return 0.0;
    }

    internal static string CreateFileName(bool safe_mode = false)
    {
        if (safe_mode)
        {
            return DateTime.Now.ToString("yyyyMMddHHmmss") + "_";
        }
        string fileName = "";
        if (data.ArtistName != null && data.SongName != null)
        {
            fileName = data.ArtistName + "_" + data.SongName;
        }
        fileName = Regex.Replace(fileName, "<[^>]*>", string.Empty);
        fileName = DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + fileName;
        try
        {
            bool isFullRun = data.EndTile == ADOBase.lm.listFloors.Count - 1 && data.StartTile == 0;
            if (isFullRun)
            {
                fileName += "_" + (100.0 * ComputePercentXaccVanilla()).ToString("F2");
            }
            else if (data.StartTile.HasValue && data.EndTile.HasValue)
            {
                // 缺起点/终点字段时旧版会抛 KeyNotFoundException 并被外层 catch 吞掉（不加后缀），
                // 这里显式跳过以保持一致，而不是把缺失当 0 处理。
                fileName += "_" + (100.0 * ComputePercentComplete(data.StartTile.Value)).ToString("F0")
                    + "-" + (100.0 * ComputePercentComplete(data.EndTile.Value)).ToString("F0");
            }
        }
        catch
        {
        }
        return ReplayManager.RemoveInvalidPathChars(fileName);
    }
}
