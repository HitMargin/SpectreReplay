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
    /// stdin 写死会卡死 ffmpeg 时（磁盘满、编码器内部挂起），所以泵线程
    /// 带看门狗：超过阈值没有进展就放弃这次渲染并报失败，而不是永远挂着。
    /// </summary>
    internal sealed class VideoEncoder : IDisposable
    {
        private Process _proc;
        private System.Threading.Thread _pump;
        private readonly BlockingQueue _queue;
        private readonly FrameBufferPool _pool;
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        private volatile bool _stopRequested;
        private volatile bool _finished;
        private Exception _failure;
        private int _written;

        private const int WatchdogSeconds = 20;

        internal int FramesWritten => _written;
        internal string Error { get; private set; } = "";

        private VideoEncoder(FrameBufferPool pool)
        {
            _pool = pool;
            _queue = new BlockingQueue(pool, null);
        }

        internal static VideoEncoder Start(FrameBufferPool pool, int width, int height,
            int fps, EncoderMode encoder, QualityMode quality, ContainerMode container,
            bool flipRows, string outputPath, string ffmpegPath)
        {
            var e = new VideoEncoder(pool);
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

        /// <summary>主线程调用：把一帧像素交出去。非阻塞——队列满就丢这一帧。</summary>
        internal void Submit(byte[] pixels)
        {
            if (_stopRequested || _finished)
            {
                return;
            }
            _queue.Enqueue(pixels);
        }

        private void Pump()
        {
            try
            {
                while (!_stopRequested)
                {
                    byte[] frame = _queue.Dequeue(50);
                    if (frame == null)
                    {
                        // 空队列：检查看门狗。长时间没有新帧进来说明上游停了，
                        // 但 ffmpeg 还在等 stdin —— 此时需要收尾，否则永远挂着。
                        if (_watch.Elapsed.TotalSeconds > WatchdogSeconds
                            && _queue.Count == 0)
                        {
                            break;
                        }
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
    /// 有界阻塞队列。满了就丢（Recycle()），不让主线程等编码器——
    /// 视频掉几帧远好过把游戏卡住。
    /// </summary>
    internal sealed class BlockingQueue
    {
        private readonly System.Collections.Generic.Queue<byte[]> _q =
            new System.Collections.Generic.Queue<byte[]>(8);
        private readonly FrameBufferPool _pool;
        private readonly Action _onWrite;
        private readonly object _gate = new object();
        private const int Capacity = 4;

        internal BlockingQueue(FrameBufferPool pool, Action onWrite)
        {
            _pool = pool;
            _onWrite = onWrite;
        }

        internal int Count { get { lock (_gate) { return _q.Count; } } }

        internal void Enqueue(byte[] item)
        {
            lock (_gate)
            {
                if (_q.Count >= Capacity)
                {
                    // 队列满：这一帧追不上，直接还回池里。
                    // 这是"实时录制跟不上"的兜底路径，绝不阻塞主线程。
                    _pool.Return(item);
                    return;
                }
                _q.Enqueue(item);
            }
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
                        return _q.Dequeue();
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