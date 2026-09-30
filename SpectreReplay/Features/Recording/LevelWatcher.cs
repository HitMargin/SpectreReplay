using UnityEngine;

namespace SpectreReplay.Features.Recording
{
    /// <summary>
    /// 进关/出关的检测器。
    ///
    /// 为什么不用"钩某个方法"：首次进入关卡这条路径在游戏不同版本里落点不同
    /// （编辑器里点开始、从主菜单进、试玩…），钩错地方就完全不触发。
    /// 而"现在正在玩一首歌"这个状态本身是稳定的——由 (gameworld, 是否已有 song,
    /// song 实例) 三个条件唯一确定。所以每帧比对一次，状态翻转即触发，
    /// 比赌一个具体入口可靠。
    ///
    /// 成本：几个字段比较，每帧一次，可以忽略。
    /// </summary>
    internal sealed class LevelWatcher : MonoBehaviour
    {
        private bool _wasInLevel;

        internal static void Install()
        {
            var go = new GameObject("SpectreReplayLevelWatcher");
            DontDestroyOnLoad(go);
            go.AddComponent<LevelWatcher>();
        }

        private void Update()
        {
            bool inLevel = ProbeLevel();
            if (inLevel == _wasInLevel)
            {
                return;
            }
            _wasInLevel = inLevel;
            if (inLevel)
            {
                Patch_Recording.BeginIfEnabled();
            }
            else if (KeyRecorder.IsRecording)
            {
                // 掉出关卡（切菜单/切谱）也当成一次结束，否则会留下半截数据
                KeyRecorder.End(Settings.AutoSave);
                VideoRender.RenderService.OnLevelEnd();
            }
        }

        private static bool ProbeLevel()
        {
            var controller = scrController.instance;
            if (controller == null || controller.gameworld != true)
            {
                return false;
            }
            var conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null)
            {
                return false;
            }
            return controller.playerOne != null;
        }
    }
}