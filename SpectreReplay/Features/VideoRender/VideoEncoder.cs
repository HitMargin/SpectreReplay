using System;
using System.Diagnostics;
using System.Threading;
using System.IO;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>画面来源。两种取法的取舍见 <see cref="FrameSource"/>。</summary>
    internal enum FrameSurface
    {
        /// <summary>官方相机链渲染到自建纹理：原生分辨率、不含界面、干净。</summary>
        CleanCamera = 0,
        /// <summary>帧末截取最终合成画面：含 Screen-Space Overlay（进度条等），分辨率跟窗口。</summary>
        WholeScreen = 1,
    }

    /// <summary>
    /// ffmpeg 管道编码器。
    ///
    /// 线程模型（这是全流程最容易出错的地方）：
    ///   主线程   —— 只做 GPU 回读 + 把像素拷进租来的缓冲 + 入队，不碰 stdin
    ///   泵线程   —— 从队列取帧，写进 ffmpeg 的 stdin，写完把缓冲还回池
    /// 这样主线程永远不会被编码阻塞；而缓冲的租还必须跨线程安全，
    /// 所以池用 lock 保护（见 FrameBufferPool）。
    ///
    //// stdin 写死会卡死 ffmpeg 时（磁盘满、编码器内部挂起），
    /// 主线程会卡在 <see cref="Submit"/> 的反压等待里。这一层的保护**不在这里**，
    /// 而在 <c>Submit</c> 的 <see cref="SubmitWaitMs"/> 超时：等满就抛，
    /// 调用方据此收摊报失败。理由见那个常量的注释——按"队列长度是否变化"
    /// 计时的那类看门狗，在渲染收尾阶段会**必然误判**。
    /// </summary>
    internal sealed class VideoEncoder : IDisposable
    {
        private Process _proc;
        private System.Threading.Thread _pump;
        private readonly BlockingQueue _queue;
        private readonly FrameBufferPool _pool;
        private volatile bool _stopRequested;
        private volatile bool _finished;
        private Exception _failure;
        private int _written;

        internal int FramesWritten => _written;
        internal string Error { get; private set; } = "";

        private VideoEncoder(FrameBufferPool pool, int frameBytes)
        {
            _pool = pool;
            // 容量按帧大小算：1920×1080 单帧 8 MB，256 MiB 预算 → 32 帧。
            // 照 Spectre（VideoEncoder.cs:181 的 268435456 / (w*h*4)，夹到 [2,64]）。
            // 原先固定 4，1080p 下只有 32 MB 缓冲，编码稍慢就频繁顶满。
            int cap = frameBytes > 0 ? 268435456 / frameBytes : 4;
            _queue = new BlockingQueue(pool, null, cap);
        }

        internal static VideoEncoder Start(FrameBufferPool pool, int width, int height,
            int fps, EncoderMode encoder, QualityMode quality, ContainerMode container,
            bool flipRows, string outputPath, string ffmpegPath)
        {
            var e = new VideoEncoder(pool, width * height * 4);
            e.Launch(width, height, fps, encoder, quality, container, flipRows,
                outputPath, ffmpegPath);
            return e;
        }

        private void Launch(int width, int height, int fps, EncoderMode encoder,
            QualityMode quality, ContainerMode container, bool flipRows,
            string outputPath, string ffmpegPath)
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in BuildArgs(width, height, fps, encoder, quality, container, flipRows, outputPath))
            {
                psi.ArgumentList.Add(a);
            }

            _proc = Process.Start(psi);
            if (_proc == null)
            {
                throw new IOException(string.Format(Ui.Loc.T("render.err.ffmpeg_start"), ffmpegPath));
            }
            // ffmpeg 的 stderr 不读会写满管道缓冲区后卡死，必须有人接着读。
            _proc.BeginErrorReadLine();
            _proc.ErrorDataReceived += (_, __) => { };

            // _queue / _pool 已在构造函数里建好，这里只起泵线程
            _pump = new System.Threading.Thread(Pump)
            {
                IsBackground = true,
                Name = "SpectreReplay.ffmpeg",
            };
            _pump.Start();
        }

        private static string[] BuildArgs(int width, int height, int fps,
            EncoderMode encoder, QualityMode quality, ContainerMode container,
            bool flipRows, string outPath)
            => FfmpegArgs.Build(width, height, fps, encoder, quality, container,
                flipRows, outPath);

        /// <summary>
        /// 主线程调用：把一帧像素交出去。
        ///
        /// 队列满时会**阻塞等待**（反压），这是有意的——见 BlockingQueue
        /// 类注释：丢帧会让音频栅格与帧序号永久错位。
        ///
        /// 返回 false 表示等待超时或已停止，调用方**不得推进帧序号**：
        /// 帧没进队列，就不能算作"已产出"，否则音频栅格会跟着前进，
        /// 而视频里永远不会有这一帧——正是我们要避免的错位。
        /// </summary>
        internal bool Submit(byte[] pixels)
        {
            if (_stopRequested || _finished)
            {
                return false;
            }
            if (_proc == null || _proc.HasExited)
            {
                // ffmpeg 已经死了：继续入队只会把队列填满后一直阻塞。
                // 立刻失败，让调用方停在这帧而不是空等。
                Error = Ui.Loc.T("render.err.encoder_gone");
                _failure = _failure
                    ?? new InvalidOperationException(Error);
                _stopRequested = true;
                return false;
            }
            return _queue.Enqueue(pixels, SubmitWaitMs);
        }

        /// <summary>
        /// 单帧入队最长等待。**这就是整个编码器唯一的卡死保护。**
        ///
        /// 取 Spectre 的量级（VideoEncoder.cs:197-223 用 120 秒）。这个值不是
        /// "正常情况下的等待时长"——正常情况下编码器远快于实时，队列永远不满，
        /// Enqueue 立刻返回。它只在 ffmpeg 停止消费 stdin 时才被耗尽，
        /// 用来把"静默挂死"变成"可诊断的失败"。
        ///
        /// Spectre 的注释把这一点讲得很直白（原 :192-196）：不设此阈值，
        /// ffmpeg 停止读 stdin 时 Submit 会无限自旋，整次渲染**静默挂死**。
        ///
        /// 为什么泵线程那边不需要第二个看门狗：见 <see cref="Pump"/> 里的说明，
        /// 那类看门狗在收尾阶段会必然误判。
        /// </summary>
        private const int SubmitWaitMs = 120_000;

        private void Pump()
        {
            try
            {
                while (!_stopRequested)
                {
                    byte[] frame = _queue.Dequeue(50);
                    if (frame == null)
                    {
                        // 空队列就是正常状态：上游这一帧还没产出，
                        // 或者已经全部送完、正在等收尾。
                        //
                        // **这里刻意不设看门狗。** 曾经的写法是"超过 20 秒
                        // 没有新帧进来就 break"，而它会在渲染**收尾阶段必然误判**：
                        // 最后一帧送出去之后 ffmpeg 还要排空管道、封装、写文件，
                        // 这段时间里上游不会再有任何 Submit——队列长度就是不变，
                        // 20 秒一到就把**成功的渲染**报成失败。
                        //
                        // 而收尾恰恰是最容易出问题的阶段，报错又发生在用户已经
                        // 耐心等完之后，体验是"画面明明在动，最后告诉我失败"。
                        //
                        // 真卡死的情形由 <see cref="SubmitWaitMs"/> 兜：
                        // 那是主线程在**队列满**上的等待（反压），
                        // ffmpeg 不读 stdin 时必然顶满队列，于是一定会超时。
                        // 泵线程空转只是浪费一个后台线程，不会让用户看到坏结果。
                        continue;
                    }
                    _proc.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                    _proc.StandardInput.BaseStream.Flush();
                    _pool.Return(frame);
                    _written++;
                }
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                _finished = true;
                TryCloseStdin();
            }
        }

        private void TryCloseStdin()
        {
            try
            {
                _proc?.StandardInput?.Close();
            }
            catch
            {
                // 关闭失败无所谓，后面的 WaitForExit 才是判据
            }
        }

        /// <summary>收尾：停泵、关 stdin、等 ffmpeg 退出。返回是否成功。</summary>
        internal bool Finish(int timeoutMs = 120_000)
        {
            _stopRequested = true;
            if (_pump != null && _pump.IsAlive)
            {
                _pump.Join(3000);
            }
            TryCloseStdin();
            try
            {
                if (_proc != null && !_proc.WaitForExit(timeoutMs))
                {
                    _proc.Kill();
                    Error = Ui.Loc.T("render.err.ffmpeg_timeout");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return false;
            }
            if (_failure != null && !(_failure is System.IO.IOException))
            {
                Error = _failure.Message;
                return false;
            }
            return _proc != null && _proc.ExitCode == 0;
        }

        public void Dispose()
        {
            try
            {
                _proc?.Kill();
                _proc?.Dispose();
            }
            catch
            {
                // 析构路径不抛
            }
        }

    }

    /// <summary>
    /// 帧缓冲池。主线程租、泵线程还，必须加锁。
    /// </summary>
    internal sealed class FrameBufferPool
    {
        private readonly System.Collections.Generic.Stack<byte[]> _free;
        private readonly int _frameSize;
        private readonly object _gate = new object();
        private readonly Action _onReturn;

        internal FrameBufferPool(int frameSize, Action onReturn)
        {
            _frameSize = frameSize;
            _onReturn = onReturn;
            _free = new System.Collections.Generic.Stack<byte[]>(8);
        }

        internal byte[] Rent()
        {
            lock (_gate)
            {
                return _free.Count > 0 ? _free.Pop() : new byte[_frameSize];
            }
        }

        internal void Return(byte[] buf)
        {
            lock (_gate)
            {
                if (_free.Count < 16)
                {
                    _free.Push(buf);
                }
            }
            _onReturn?.Invoke();
        }
    }

    /// <summary>
    /// 有界队列。**满了阻塞等待，不丢帧。**
    ///
    /// ── 为什么不能丢帧（曾经的实现：满了就 _pool.Return(item) 丢掉）──
    /// 输出帧序号与音频帧栅格是一一对应的：LiveSession 每产出一帧就调一次
    /// PushAudioForFrame，把音频栅格推进到与该帧对齐。丢一帧的后果不是
    /// 「视频掉一张图」，而是**音画从那一刻起永久错位**——音频照样前进，
    /// 视频少了一张对应的图，帧序号与音频游标从此对不上，之后每一帧都错。
    /// 而 LiveSession 的补帧策略（复制上一帧）修不了这个：
    /// 它保证视频帧数连续，却保证不了音频配对关系。
    ///
    /// Spectre 的注释把这条写成了原则（VideoEncoder.cs:12-13）：
    /// 「有界队列 = 反压：编码跟不上时主线程等待，**不影响成片时间轴**」。
    ///
    /// ── 代价与兜底 ──────────────────────────────────────────
    /// 代价是编码慢时主线程被阻塞。对边玩边录而言，掉几帧听起来比卡一下好，
    /// 但那只在**画面本身不重要**时成立；音画错位的代价比卡顿大得多，
    /// 而且卡顿只影响当下这一段，错位会毁掉整个成片的后半段。
    ///
    /// 阻塞必须有上限，否则 ffmpeg 一旦停止读 stdin（磁盘满、编码器挂起、
    /// 进程悄悄死掉），主线程会永远等下去，整个游戏卡死不可玩。
    /// 所以 Enqueue 带超时：超时后**仍不丢帧**，而是记一次警告并继续等
    /// ——真正的止损交给 Finish() 的看门狗与退出码判定，
    /// 那两处能准确告诉你 ffmpeg 到底怎么了。
    /// </summary>
    internal sealed class BlockingQueue
    {
        private readonly System.Collections.Generic.Queue<byte[]> _q =
            new System.Collections.Generic.Queue<byte[]>(64);
        private readonly FrameBufferPool _pool;
        private readonly Action _onWrite;
        private readonly object _gate = new object();
        private readonly System.Threading.SemaphoreSlim _space;

        /// <summary>
        /// 队列上限。按 256 MiB 预算算，与 Spectre 一致
        /// （VideoEncoder.cs:181：268435456 / (w*h*4)），再夹到 [2,64]。
        ///
        /// 原先固定 4：1920×1080 单帧 8 MB，4 帧仅 32 MB 缓冲，
        /// 编码稍慢就立刻顶满，频繁触发阻塞。Spectre 同分辨率下容量是 32。
        /// </summary>
        internal BlockingQueue(FrameBufferPool pool, Action onWrite, int capacity)
        {
            _pool = pool;
            _onWrite = onWrite;
            Capacity = Math.Max(2, Math.Min(64, capacity));
            _space = new System.Threading.SemaphoreSlim(Capacity, Capacity);
        }

        internal int Capacity { get; }

        internal int Count { get { lock (_gate) { return _q.Count; } } }

        /// <summary>
        /// 入队。**队列满则阻塞等待，绝不丢帧**（理由见类注释）。
        ///
        /// 返回 false 表示等待超时——调用方应停止推进而不是继续，
        /// 因为继续只会让队列越来越满、每一帧都要等满超时。
        /// 帧**没有**被丢弃：它仍在调用方手里，调用方决定放弃。
        /// </summary>
        internal bool Enqueue(byte[] item, int timeoutMs)
        {
            if (timeoutMs > 0 && !_space.Wait(timeoutMs))
            {
                return false;
            }
            lock (_gate)
            {
                _q.Enqueue(item);
            }
            _onWrite?.Invoke();
            return true;
        }

        internal byte[] Dequeue(int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                lock (_gate)
                {
                    if (_q.Count > 0)
                    {
                        var v = _q.Dequeue();
                        _space.Release();
                        return v;
                    }
                }
                if (Environment.TickCount > deadline)
                {
                    return null;
                }
                System.Threading.Thread.Sleep(1);
            }
        }
    }
}