using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpectreReplay;

// 主线程调度器：后台线程要动 Unity API（UI/GameObject/协程）时经 Run() 转回主线程执行。
// 泵由隐藏的常驻 MonoBehaviour 每 Update 驱动，mod 开启时 Install、关闭时 Uninstall。
// - 主线程调用 Run：直接同步执行（零开销路径）
// - 泵未建立（mod 未开启的早期阶段）：也直接执行，不排队不等待
// - 泵内每个 action 自带异常隔离：单个回调抛错不会杀死泵
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> _queue = new();
    private static PumpBehaviour _pump;

    internal static bool IsMainThread
        => _pump == null || _pump.ThreadRef == Thread.CurrentThread;

    internal static void Install()
    {
        if (_pump != null) return;
        GameObject go = new GameObject("SpectreMainThread");
        Object.DontDestroyOnLoad(go);
        _pump = go.AddComponent<PumpBehaviour>();
    }

    internal static void Uninstall()
    {
        if (_pump == null) return;
        Object.Destroy(_pump.gameObject);
        _pump = null;
        while (_queue.TryDequeue(out _)) { }
    }

    internal static void Enqueue(Action action) => Run(action);

    /// <summary>
    /// 延迟到下一拍执行（Quartz 的 RequestPagesRebuild 语义：不能在事件回调栈内拆 UI）。
    /// 泵在就排队下一帧；泵未建立（mod 未开启的极早期）退化为同步执行。
    /// </summary>
    internal static void Defer(Action action)
    {
        if (action == null) return;
        if (_pump != null)
        {
            _queue.Enqueue(action);
            return;
        }
        InvokeSafe(action);
    }

    internal static void Run(Action action)
    {
        if (action == null) return;
        if (IsMainThread)
        {
            InvokeSafe(action);
            return;
        }
        _queue.Enqueue(action);
    }

    // 任意线程安全启动协程（泵持有 MonoBehaviour；未安装时返回 null，调用方自行判空降级）
    internal static Coroutine StartCoroutine(IEnumerator routine)
        => _pump != null ? _pump.StartCoroutine(routine) : null;

    private static void InvokeSafe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    private sealed class PumpBehaviour : MonoBehaviour
    {
        internal readonly Thread ThreadRef = Thread.CurrentThread;

        // 每拍最多执行的入队动作数。Update 里 while(TryDequeue) 排空整个队列，在正常
        // 入队节奏下没问题，但一旦出现**持续入队的生产者**（后台线程在循环里 Run），
        // 队列永远排不空 → 这一帧的 Update 永不返回 → 游戏直接卡死。封顶后最坏情况是
        // 积压分摊到后续若干帧，而不是一帧里全部执行。
        // 256 远高于任何真实场景的一拍工作量（当前全部入队点都是一次性事件），
        // 正常情况下根本碰不到这个上限。
        private const int MaxActionsPerFrame = 256;

        // 积压告警阈值：到这个量级已经不是"稍后再处理"，而是有人在高频生产。
        // 只报一次（_backlogWarned），避免每帧刷屏把日志冲走。
        private const int BacklogWarnThreshold = 4096;
        private static bool _backlogWarned;

        private void Update()
        {
            // 只在可能积压时才取 Count：ConcurrentQueue<T>.Count 会遍历段数组求和，
            // 队列越长越贵。空队列时 TryPeek 一次即可短路，日常路径不做这个 O(段数) 遍历。
            if (!_queue.IsEmpty)
            {
                int backlog = _queue.Count;
                if (backlog > BacklogWarnThreshold && !_backlogWarned)
                {
                    _backlogWarned = true;
                    Debug.LogWarning("[SpectreReplay] 主线程队列积压 " + backlog
                        + " 项（阈值 " + BacklogWarnThreshold + "）：某处在高频入队，"
                        + "动作会被分摊到多帧执行；若卡顿请检查调用方");
                }
                else if (backlog < BacklogWarnThreshold / 2)
                {
                    _backlogWarned = false;
                }
            }

            int done = 0;
            while (done < MaxActionsPerFrame && _queue.TryDequeue(out Action action))
            {
                done++;
                InvokeSafe(action);
            }
        }

        private void OnDestroy()
        {
            if (_pump == this)
                _pump = null;
        }
    }
}
