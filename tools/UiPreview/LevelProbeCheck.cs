// 「在不在关卡里」的判据检查。
//
// **这条检查的存在理由已经变了**（2026-10-06 复核）。
// 原先它盯的是自研引擎的 LevelWatcher/LiveSession 里那个布尔探针；整体移植
// Spectre 之后，那套判据连同类代码一起进了 `_legacy_srp` 停用——现在主项目里
// **根本没有 gameworld 探针**，Patch_Lifecycle 直接拿场景名判断
// （Patch_Lifecycle.cs:203/:268/:317）。也就是说这条检查的"被测对象"已经没了。
//
// 它仍然值得跑，原因有两个：
//   ① 那三次错判据的历史值得留档——它们全都错在"找一个表示在关卡里的布尔量"，
//      而那些量描述的是**玩到哪一步**，全都晚于"进关"这个时刻。
//      下一个加"需不需要在关卡里"判断的人，会从同一口井里打水。
//   ② 它复刻的三个错判据正好是**反向断言**的教材：断言"旧做法错"，
//      而不是"新做法对"。哪天有人把判据改回去，立刻红。
//
// 下面把三种错判据的真实时序摊开，逐个断言它们在"玩家刚开始玩"时
// 会给出错误答案——这样这条检查守的是**判据的选择**，不是某段代码。
using System;

namespace UiPreview
{
    internal static class LevelProbeCheck
    {
        /// <summary>
        /// 一个"刚进关、还没按开始键"的时刻，各字段的真实取值。
        ///
        /// 全部照抄游戏源码的事实，不是猜的：
        ///   gameworld     = false  ← 只在首次命中后置 true（scrController.cs:2872）
        ///   currentState   = None   ← Start 状态要等玩家按开始键
        ///   playerOne      抛异常   ← players[0]，此时 players 可能还是空的
        ///   song           != null  ← 歌曲已经在播了
        ///   scene          = scnGame
        /// </summary>
        private static readonly bool GameWorld = false;
        private static readonly bool CurrentStateIsNone = true;
        private static readonly bool SongExists = true;
        private const string Scene = "scnGame";

        private const string SceneGame = "scnGame";
        private const string SceneEditor = "scnEditor";
        private const string SceneLevelSelect = "scnLevelSelect";
        private const string SceneLoading = "scnLoading";
        private const string SceneSplash = "scnSplash";
        private const string SceneCLS = "scnCLS";

        /// <summary>players[0] 在此刻取不到。</summary>
        private static bool PlayerOneThrows() => true;

        /// <summary>
        /// 三个曾用过的错判据，签名与真机一致。
        ///
        /// 上面三个事实字段刻意**不写成 const**：写成 const 时编译器能
        /// 推出 if 恒假、throw 之后的代码不可达，于是报 CS0162——
        /// 而那段不可达代码恰恰是要演示的东西。改成 static readonly
        /// 字段后编译器看不穿，检查里的分支照常参与编译。
        /// </summary>
        private static bool V1_GameWorldAndSongAndPlayer()
        {
            if (!GameWorld || !SongExists) return false;
            if (PlayerOneThrows()) throw new InvalidOperationException("players[0]");
            return true;
        }

        private static bool V2_AddCurrentState()
        {
            if (!GameWorld || !SongExists) return false;
            if (CurrentStateIsNone) return false;      // ← 新加的这一条把功能废掉了
            return true;
        }

        private static bool V3_GameWorldAndSong()
        {
            return GameWorld && SongExists;
        }

        /// <summary>现在的判据：场景名 + 歌曲。</summary>
        private static bool Current(string scene)
        {
            if (scene != SceneGame && scene != SceneEditor) return false;
            return SongExists;
        }

        internal static int Run()
        {
            Console.WriteLine("  ── 关卡探测判据 ──");
            int fail = 0;
            fail += NowDetectsLevel(SceneGame, "scnGame");
            fail += NowDetectsLevel(SceneEditor, "scnEditor");
            fail += OldMissesLevel("第一版 gameworld+playerOne",
                V1_GameWorldAndSongAndPlayer);
            fail += OldMissesLevel("第二版 +currentState",
                V2_AddCurrentState);
            fail += OldMissesLevel("第三版 gameworld+song",
                V3_GameWorldAndSong);
            fail += NowIgnoresMenuScenes();
            fail += PortedCodeUsesSceneName();
            return fail;
        }

        /// <summary>
        /// 移植后的主项目必须继续用**场景名**判断，不许退回布尔量探针。
        ///
        /// 判据落在源码形状上：Patch_Lifecycle 里要出现场景名比较，
        /// 且不许出现 `gameworld` 当"在不在关卡里"的依据。
        /// （ReplayPlayer 里那两处 gameworld 读的是别的语义——误差条与相机，
        ///  不是关卡探测，与这条判据无关，所以只查 Patch_Lifecycle。）
        /// </summary>
        private static int PortedCodeUsesSceneName()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetFullPath(@"..\..\SpectreReplay"),
                "Features", "Replay", "Patch_Lifecycle.cs");
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("    ✗ ④ 找不到 Patch_Lifecycle.cs");
                return 1;
            }
            string code = System.IO.File.ReadAllText(path);
            bool usesScene = code.Contains("GetActiveScene().name", StringComparison.Ordinal);
            bool usesGameworld = code.Contains("gameworld", StringComparison.Ordinal);
            bool ok = usesScene && !usesGameworld;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ④ 移植后的判据仍是场景名"
                + (usesGameworld ? "  ← 出现了 gameworld，退回旧错了三次的那条路" : ""));
            return ok ? 0 : 1;
        }

        private static int NowDetectsLevel(string scene, string label)
        {
            bool ok = Current(scene);
            Console.WriteLine($"    {(ok ? "✓" : "✗")} ① 当前判据认得出 {label}");
            if (!ok)
            {
                Console.WriteLine("      ← 玩家刚开始玩就探测不到，整局都不会录");
            }
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 三个旧判据在"刚进关"时都必须给出错误答案。
        ///
        /// 这条断言的写法是刻意的：它断言的是**旧做法错**。
        /// 哪天有人"优化"回去、改的是这三份复刻里的某一份，断言立刻红，
        /// 而不是等玩家再报一次"不录了"。
        ///
        /// 抛异常也算"没探测到"，而且更糟——真机上就是每帧一次异常。
        /// </summary>
        private static int OldMissesLevel(string label, Func<bool> probe)
        {
            bool missed;
            try
            {
                missed = !probe();
            }
            catch (InvalidOperationException)
            {
                missed = true;
            }
            Console.WriteLine($"    {(missed ? "✓" : "✗")} ② {label} 会漏掉刚进关"
                + (missed ? "" : "  ← 这个判据其实可用？"));
            return missed ? 0 : 1;
        }

        /// <summary>菜单/加载场景必须判为"不在关卡"，否则回主菜单也停不下来。</summary>
        private static int NowIgnoresMenuScenes()
        {
            string[] menus = { SceneLevelSelect, SceneLoading, SceneSplash, SceneCLS };
            bool all = true;
            foreach (string m in menus)
            {
                if (Current(m)) { all = false; Console.WriteLine($"      ← {m} 被误判成关卡"); }
            }
            Console.WriteLine($"    {(all ? "✓" : "✗")} ③ 当前判据认得出 4 个非游玩场景");
            return all ? 0 : 1;
        }
    }
}