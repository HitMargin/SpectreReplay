using System;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 回读的"在飞"闸门：一次只允许一路回读，回调到达才放开。
    ///
    /// **为什么单独抽成一个不碰引擎的类。**
    /// 这里守的是一条纯时序性质，源码里怎么看都"像对的"：
    ///
    ///   整屏取帧那条路（ReadPixels）的回调是**同步**的 ——
    ///   <c>FrameSource.RequestWholeScreen()</c> 还没返回，
    ///   回调 <c>OnWholeScreenReady</c> 已经在同一个栈上跑完了。
    ///
    /// 于是"先发请求、再按结果置位"的写法（正是原来的写法）必然出事：
    /// 回调里先把在飞标记清掉，回到发起处才把它置上 —— 置位落在清位之后，
    /// 再没有任何人清它。下一次请求看到"还在飞"就直接放弃，
    /// 从此一帧都排不上，而**不报错、不停机**：
    ///   · Live 模式成片是**第一帧的静止画面**，时长与音轨都对
    ///     （看着能用、内容全错）；
    ///   · 离线模式编码器只收到 1 帧。
    ///
    /// 正确顺序只有一个：**先置位，再发请求**，请求返回"没排上"时
    /// 再把置位收回去。这样同步回调与异步回调两种时序都成立。
    ///
    /// 抽出来还为了能**实测**：时序问题没法靠读源码断言，
    /// 只能真的按同步顺序跑一遍（tools\UiPreview 链接本文件，
    /// 用真代码验"同步回调之后闸门是不是已经放开"）。
    /// </summary>
    internal sealed class ReadbackGate
    {
        private bool _pending;

        /// <summary>当前是否有一次回读在飞。</summary>
        internal bool Pending
        {
            get { return _pending; }
        }

        /// <summary>
        /// 发起一次回读。<paramref name="request"/> 返回"这次真的排上了吗"
        /// （相机列表为空、窗口尺寸为 0 时它会返回 false，此时并没有排任何回读）。
        ///
        /// 返回 true 表示这次排上了、调用方应当等回调；返回 false 表示
        /// 上一路还没回来或这次没排上，调用方该跳过这一帧而不是干等。
        /// </summary>
        internal bool TryStart(Func<bool> request)
        {
            if (request == null || _pending)
            {
                return false;
            }

            // **先置位，再发请求。** 顺序反了就是上面说的那个 bug：
            // 同步回调会在 request() 内部就把闸门放开，
            // 置位再发生就等于永久锁死。
            _pending = true;

            bool queued;
            try
            {
                queued = request();
            }
            catch
            {
                // 请求本身抛异常 = 这次什么都没排上，别把闸门留在锁死状态
                _pending = false;
                throw;
            }

            if (!queued)
            {
                _pending = false;
            }
            return queued;
        }

        /// <summary>回调到达时调用（同步或异步都算）。</summary>
        internal void Complete()
        {
            _pending = false;
        }
    }
}
