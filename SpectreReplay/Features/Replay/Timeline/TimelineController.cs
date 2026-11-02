using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 时间轴的命令入口。UI 只调用这里，不直接改回放游标或渲染协程。
/// 选区 = 循环播放范围：seek 钳制在选区内，游标越过终点自动回到起点。
/// </summary>
internal static class TimelineController
{
    internal static readonly ReplayTimelineState State = new ReplayTimelineState();

    internal static bool InputCaptured { get; private set; }

    /// <summary>选区是否激活（非全范围）。激活时 seek 钳制且循环。</summary>
    internal static bool SelectionActive
    {
        get
        {
            var sel = State.Selection;
            var dur = State.Duration;
            return dur > 0 && (sel.Start > 0.01 || sel.End < dur - 0.01);
        }
    }

    /// <summary>当前游标对应的地板编号（0-based）。关卡未载入返回 -1。</summary>
    internal static int CurrentFloor
    {
        get
        {
            var floors = ADOBase.lm?.listFloors;
            if (floors == null || floors.Count == 0) return -1;
            double cursor = State.Cursor;
            int floor = 0;
            for (int i = 0; i < floors.Count; i++)
            {
                if (floors[i].entryTime <= cursor) floor = i;
                else break;
            }
            return floor;
        }
    }

    /// <summary>选区起点对应的地板编号（0-based）。关卡未载入返回 -1。</summary>
    internal static int SelectionStartFloor
    {
        get
        {
            var floors = ADOBase.lm?.listFloors;
            if (floors == null || floors.Count == 0) return -1;
            double start = State.Selection.Start;
            int floor = 0;
            for (int i = 0; i < floors.Count; i++)
            {
                if (floors[i].entryTime <= start) floor = i;
                else break;
            }
            return floor;
        }
    }

    /// <summary>选区终点对应的地板编号（0-based）。关卡未载入返回 -1。</summary>
    internal static int SelectionEndFloor
    {
        get
        {
            var floors = ADOBase.lm?.listFloors;
            if (floors == null || floors.Count == 0) return -1;
            double end = State.Selection.End;
            int floor = 0;
            for (int i = 0; i < floors.Count; i++)
            {
                if (floors[i].entryTime <= end) floor = i;
                else break;
            }
            return floor;
        }
    }

    /// <summary>总地板数。关卡未载入返回 0。</summary>
    internal static int TotalFloors => ADOBase.lm?.listFloors?.Count ?? 0;

    internal static void SetInputCaptured(bool value)
    {
        InputCaptured = value;
    }

    internal static void Load(ReplayData source, IReadOnlyList<double> floorTimes)
    {
        if (source == null)
        {
            State.Clear();
            return;
        }
        State.Load(source, floorTimes);
    }

    internal static void Clear()
    {
        State.Clear();
    }

    internal static void Tick(double songPosition)
    {
        State.SetCursor(songPosition);
        // 循环：选区激活且游标越过终点 → seek 回起点
        if (SelectionActive && SpectreState.is_playing)
        {
            var sel = State.Selection;
            if (songPosition >= sel.End)
                Seek(sel.Start);
        }
    }

    internal static void RefreshCurrentLevel()
    {
        if (State.Source == null)
            return;
        var floors = ADOBase.lm?.listFloors;
        if (floors == null || floors.Count == 0)
            return;
        var times = new List<double>(floors.Count);
        for (int i = 0; i < floors.Count; i++)
            times.Add(floors[i].entryTime);
        State.UpdateFloorTimes(times);
    }

    /// <summary>拖拽过程中只更新视觉游标，不触发实际 seek。</summary>
    internal static void PreviewSeek(double songPosition)
    {
        if (State.Source == null)
            return;
        double t = State.Index.Clamp(songPosition);
        if (SelectionActive)
            t = ClampToSelection(t);
        State.SetCursor(t);
    }

    /// <summary>
    /// 拖拽结束/点击后提交 seek。正在回放时通过 scrController.Restart 从目标砖重启；
    /// Start_Rewind 补丁的 TryStartPlayback 会调 FastForward(GCS.checkpointNum) 对齐回放。
    /// </summary>
    internal static bool Seek(double songPosition)
    {
        if (State.Source == null)
            return false;
        double target = State.Index.Clamp(songPosition);
        if (SelectionActive)
            target = ClampToSelection(target);
        State.SetCursor(target);

        var floors = ADOBase.lm?.listFloors;
        if (floors == null || floors.Count == 0)
            return false;

        int targetFloor = 0;
        for (int i = 0; i < floors.Count; i++)
        {
            if (floors[i].entryTime <= target)
                targetFloor = i;
            else
                break;
        }

        if (!SpectreState.is_playing)
        {
            ReplayPlayer.SeekToTime(target);
            return true;
        }

        // 通过延迟到下一帧执行，避免在 UI Update 内直接触发场景重载。
        // **按场景分支**：
        //   scnEditor → editor.SelectFloor + editor.Play()（不会卸载已载入的关卡）
        //   scnGame   → scrController.Restart()（游戏内重开，用 checkpointNum 对齐）
        // 绝对不能在 scnEditor 里调 scrController.Restart()——那会清掉已载入的关卡。
        int floor = targetFloor;
        MainThread.Defer(() =>
        {
            try
            {
                var fl = ADOBase.lm?.listFloors;
                if (fl == null || fl.Count == 0) return;
                int clamped = Mathf.Clamp(floor, 0, fl.Count - 1);
                GCS.checkpointNum = clamped;

                string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (sceneName == "scnEditor")
                {
                    if (ADOBase.editor != null && ADOBase.editor.floors != null
                        && ADOBase.editor.floors.Count > 1)
                    {
                        ADOBase.editor.SelectFloor(ADOBase.editor.floors[clamped], cameraJump: false);
                        ADOBase.editor.Play();
                    }
                }
                else
                {
                    // scnGame / 其他场景：用游戏自己的 Restart（走 checkpoint 机制）
                    if (scrController.instance != null)
                        scrController.instance.Restart();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] Timeline seek restart failed: " + ex.Message);
            }
        });
        return true;
    }

    /// <summary>把时间钳制到选区范围内。</summary>
    private static double ClampToSelection(double time)
    {
        var sel = State.Selection;
        return Math.Max(sel.Start, Math.Min(sel.End, time));
    }
}
