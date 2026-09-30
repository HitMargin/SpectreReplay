// EventSystem 唯一性验证。
//
// 用户报过"设置菜单点不动"。面板是 DontDestroyOnLoad 的常驻对象，
// 而游戏的 EventSystem 是场景/预制体里的对象（ADOBase 里是序列化字段，
// 没有代码创建它）。两者生命周期对不上，就会出现两个 EventSystem 并存：
// Unity 报 "There can be only one active Event System"，之后谁处理输入
// 就不确定了——表现就是"有时候点得动有时候点不动"。
//
// 真实 Unity 的 EventSystem 只允许一个 active，且 Engine 维护
// EventSystem.current 作为权威。这里照同样的规则验三种时序。
using System;
using SpectreReplay.Ui;
using UnityEngine.EventSystems;

namespace UiPreview
{
    internal static class EventCheck
    {
        internal static int Run()
        {
            int fail = 0;
            Console.WriteLine("  ── EventSystem 唯一性 ──");
            fail += Case("① 场景里没有 → 面板自建一个",
                () =>
                {
                    EventSystem.current = null;
                    PanelHost.Open();
                    return EventSystem.current != null;
                },
                true);

            fail += Case("② 游戏那个后来到位 → 面板的自建要被拆掉",
                () =>
                {
                    var mine = EventSystem.current;
                    // 游戏在换场景后建了自己的
                    var gameGo = new UnityEngine.GameObject("GameEventSystem");
                    gameGo.AddComponent<EventSystem>();
                    PanelHost.Open();          // 再开一次面板
                    // 场上只应剩游戏那个
                    return ReferenceEquals(EventSystem.current, gameGo.transform
                        .gameObject.GetComponent<EventSystem>())
                        && !ReferenceEquals(mine, EventSystem.current);
                },
                true);

            fail += Case("③ 面板关闭 → 自建的被拆掉",
                () =>
                {
                    EventSystem.current = null;
                    PanelHost.Open();
                    PanelHost.Close();
                    return EventSystem.current == null;
                },
                true);

            EventSystem.current = null;
            return fail;
        }

        private static int Case(string label, Func<bool> act, bool expect)
        {
            bool got;
            try
            {
                got = act();
            }
            catch (Exception e)
            {
                Console.WriteLine($"    ✗ {label,-42} 抛异常：{e.GetType().Name}");
                return 1;
            }
            bool ok = got == expect;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {label,-42} → {got}");
            return ok ? 0 : 1;
        }
    }
}
