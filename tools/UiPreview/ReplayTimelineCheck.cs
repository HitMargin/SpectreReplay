using System;
using System.Collections.Generic;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    internal static class ReplayTimelineCheck
    {
        internal static int Run()
        {
            Console.WriteLine("  ── 回放时间轴索引与动态区间 ──");
            int fail = 0;
            fail += KeyLookup();
            fail += FloorLookup();
            fail += SelectionConstraints();
            fail += ViewState();
            if (fail == 0)
                Console.WriteLine("    ✓ 时间索引、区间约束与视图状态全部通过");
            return fail;
        }

        private static int KeyLookup()
        {
            var source = new ReplayData();
            source.Reset();
            source.KeyEvents.Add(new KeyEvent { KeyCode = 1, IsPressed = true, SongPosition = 1.0 });
            source.KeyEvents.Add(new KeyEvent { KeyCode = 2, IsPressed = false, SongPosition = 5.5 });
            source.KeyEvents.Add(new KeyEvent { KeyCode = 3, IsPressed = true, SongPosition = 9.0 });
            var index = new ReplayTimelineIndex(source, new[] { 0.0, 10.0, 20.0 });
            var bad = new List<string>();
            if (index.FindKeyIndexAtOrAfter(5.5) != 1) bad.Add("精确命中应返回原始索引 1");
            if (index.FindKeyIndexAtOrAfter(6.0) != 2) bad.Add("区间查询应返回下一条按键 2");
            if (index.FindKeyIndexAtOrAfter(99.0) != 3) bad.Add("越界应返回 Count");
            if (index.FindKeyIndexBefore(9.0) != 1) bad.Add("严格早于查询应返回索引 1");
            if (index.FindKeyIndexBefore(0.5) != -1) bad.Add("首条之前应返回 -1");
            return Report("⑴ 按键时间二分查询", bad);
        }

        private static int FloorLookup()
        {
            var index = new ReplayTimelineIndex(new ReplayData(), new[] { 0.0, 10.0, 20.0 });
            var bad = new List<string>();
            if (index.FindFloorIndexAtOrBefore(9.99) != 0) bad.Add("9.99 应属于第 0 楼层");
            if (index.FindFloorIndexAtOrBefore(10.0) != 1) bad.Add("10.0 边界应属于第 1 楼层");
            if (index.FindFloorIndexAtOrBefore(99.0) != 2) bad.Add("越界应回到最后楼层");
            return Report("⑵ 楼层时间边界查询", bad);
        }

        private static int SelectionConstraints()
        {
            var bad = new List<string>();
            var full = TimelineSelection.Full(20.0);
            if (full.Start != 0.0 || full.End != 20.0) bad.Add("Full 区间不正确");
            var tiny = new TimelineSelection(5.0, 5.0);
            if (tiny.Duration + 1e-9 < TimelineSelection.DefaultMinDuration) bad.Add("区间没有保留最小时长");
            var reversed = new TimelineSelection(15.0, 5.0);
            if (reversed.Start != 5.0 || reversed.End != 15.0) bad.Add("反向拖动未归一化");
            var moved = full.MoveStart(30.0, 20.0);
            if (moved.Start > moved.End - TimelineSelection.DefaultMinDuration) bad.Add("起点越界");
            return Report("⑶ 动态区间归一化与最小时长", bad);
        }

        private static int ViewState()
        {
            var source = new ReplayData();
            source.Reset();
            source.KeyEvents.Add(new KeyEvent { SongPosition = 20.0 });
            var state = new ReplayTimelineState();
            state.Load(source, new[] { 0.0, 10.0, 20.0 });
            var bad = new List<string>();
            if (state.Duration != 20.0) bad.Add("Duration 应覆盖楼层终点");
            double anchor = state.TimeToRatio(10.0);
            state.Zoom(0.5, 10.0);
            double after = state.TimeToRatio(10.0);
            if (Math.Abs(anchor - after) > 1e-9) bad.Add("缩放没有保持锚点时间");
            state.Pan(-100.0);
            if (state.ViewStart != 0.0) bad.Add("左边界没有夹住");
            state.Pan(100.0);
            if (state.ViewStart + state.ViewSpan > state.Duration + 1e-9) bad.Add("右边界没有夹住");

            state.Load(source, null);
            state.UpdateFloorTimes(new[] { 0.0, 10.0, 30.0 });
            if (state.Duration != 30.0 || state.Selection.End != 30.0)
                bad.Add("楼层时间补入后完整区间没有扩展到新终点");
            return Report("⑷ 视图缩放、平移与边界夹取", bad);
        }

        private static int Report(string name, List<string> bad)
        {
            if (bad.Count == 0)
            {
                Console.WriteLine("    ✓ " + name);
                return 0;
            }
            foreach (string item in bad)
                Console.WriteLine("    ✗ " + name + "：" + item);
            return bad.Count;
        }
    }
}
