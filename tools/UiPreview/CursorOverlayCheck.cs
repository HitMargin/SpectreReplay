// 鼠标指针合成的纯数学判据（2026-10-20，加「是否录制鼠标指针」选项时补的）。
//
// 这块的失败方式与其他界面判据不同：**没有异常、没有报错**，指针要么不出现、
// 要么出现在错误的角落，只有把成片翻出来看才发现。所以这里直接拿真源码
// （CursorOverlay.cs 已链接进本工程）往 byte[] 上真画一遍，再按像素验：
//
//   ① 热点对得上：Draw 的 mouseX/mouseY 是「箭尖」，箭身要落在箭尖的右下，
//      往左上画就是反的（原型阶段画错过一次，靠 ASCII 预览才发现）。
//   ② 行序：WholeScreen 顶行在前、CleanCamera 需要翻转（编码器只对后者 vflip）。
//      同一份形状在两种行序下必须落在**相反的半边**，否则渲染出来上下颠倒。
//   ③ 坐标缩放：CleanCamera 的输出分辨率与窗口无关，指针必须按比例落位。
//   ④ 边界：贴边/出界的指针不能越界写数组（越界写会直接把渲染线程打崩）。
//   ⑤ 开关与空输入：不画时像素必须**一个字节都没动**（不然等于强制开着）。
using System;
using SpectreReplay.Features.VideoRender;

namespace UiPreview
{
    internal static class CursorOverlayCheck
    {
        private const int W = 160;
        private const int H = 120;

        internal static int Run()
        {
            Console.WriteLine("  ── 鼠标指针合成 ──");
            int fail = 0;

            // ① 热点在箭尖，箭身在箭尖的右侧与下方（屏幕坐标 y 向下）。
            {
                // 指针放在画面正中，鼠标坐标与屏幕尺寸相同 ⇒ 1:1 映射。
                byte[] buf = Frame();
                CursorOverlay.Draw(buf, W, H, rowsBottomUp: false,
                    W / 2f, H / 2f, W, H);
                InkedCenterOfMass(buf, out float cx, out float cy);
                // 箭尖在 (80,60)：箭身应往右（cx>80）、往下（cy>60）。
                bool right = cx > W / 2f + 2f;
                bool down = cy > H / 2f + 2f;
                if (!right || !down)
                {
                    Console.WriteLine("    ✗ ① 箭身画到了箭尖的另一侧：质心=(" +
                        cx.ToString("0.0") + "," + cy.ToString("0.0") + ")，应在右下");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ① 箭身落在箭尖右下方（热点就是箭尖）");
                }
            }

            // ② 行序：同一指针位置，两种行序下墨迹质心应关于画面水平中线镜像。
            {
                byte[] top = Frame();
                CursorOverlay.Draw(top, W, H, rowsBottomUp: false, W / 2f, H * 0.25f, W, H);
                InkedCenterOfMass(top, out float _, out float cyTop);

                byte[] bottom = Frame();
                CursorOverlay.Draw(bottom, W, H, rowsBottomUp: true, W / 2f, H * 0.25f, W, H);
                InkedCenterOfMass(bottom, out float _, out float cyBottom);

                // 屏幕 y=0.25H（靠下四分之一，因为 Input.mousePosition 原点在左下）
                // 在 top-down 行序下应落在文件靠下方 => cyTop 大；
                // 在 bottom-up 行序下同一屏幕位置反过来 => cyBottom 小。
                if (!(cyTop > H * 0.5f && cyBottom < H * 0.5f))
                {
                    Console.WriteLine("    ✗ ② 行序没生效：top-down 质心 y=" +
                        cyTop.ToString("0.0") + "，bottom-up 质心 y=" + cyBottom.ToString("0.0") +
                        "（应分居中线两侧）");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ② 两种行序把同一屏幕位置落在相反半边（编码器 vflip 对得上）");
                }
            }

            // ③ 分辨率缩放：屏幕坐标不变、输出分辨率翻倍 ⇒ **箭尖**必须落在 (W,H)。
            //    **判据只能用箭尖，不能用质心**：箭身长在热点右下方，质心天然带偏移；
            //    而且指针尺寸在低于 720p 时被 Math.Max(1f, …) 钳住，尺寸并不翻倍——
            //    第一版拿质心乘 2 当期望值，假红两次（偏移量被多乘了一遍）。
            {
                bool ok = true;
                string detail = "";
                int[] dims = { 1, 2, 3 };
                foreach (int k in dims)
                {
                    int w = W * k, h = H * k;
                    byte[] buf = new byte[w * h * 4];
                    for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
                    CursorOverlay.Draw(buf, w, h, rowsBottomUp: false, W / 2f, H / 2f, W, H);
                    InkedBounds(buf, w, out int bx0, out int by0, out int _, out int _);
                    // 箭尖 = 形状的左上极值；抗锯齿会让它偏 1~2 px
                    if (Math.Abs(bx0 - W * k / 2) > 2 || Math.Abs(by0 - H * k / 2) > 2)
                    {
                        ok = false;
                        detail = k + " 倍输出时箭尖在 (" + bx0 + "," + by0 + ")，应为 (" +
                            (W * k / 2) + "," + (H * k / 2) + ")";
                        break;
                    }
                }
                if (!ok)
                {
                    Console.WriteLine("    ✗ ③ " + detail);
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ③ 箭尖按输出分辨率等比落位（纯净相机的超采样对得上）");
                }
            }

            // ④ 贴边与出界：四个角 + 完全出界，都不许抛异常也不许写越界。
            {
                bool ok = true;
                float[] xs = { 0f, W - 1f, 0f, W - 1f, -5000f, 5000f };
                float[] ys = { 0f, 0f, H - 1f, H - 1f, -5000f, 5000f };
                for (int k = 0; k < xs.Length; k++)
                {
                    try
                    {
                        byte[] buf = Frame();
                        CursorOverlay.Draw(buf, W, H, rowsBottomUp: false, xs[k], ys[k], W, H);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("    ✗ ④ 指针在 (" + xs[k] + "," + ys[k] +
                            ") 时抛异常：" + ex.GetType().Name + " " + ex.Message);
                        ok = false;
                        break;
                    }
                }
                if (!ok) fail++;
                else Console.WriteLine("    ✓ ④ 贴边与完全出界的指针都不越界、不抛异常");
            }

            // ⑤ 不该画的时候，像素必须一个字节都没动。
            {
                bool ok = true;

                byte[] buf = Frame();
                byte[] before = (byte[])buf.Clone();
                CursorOverlay.Draw(buf, W, H, rowsBottomUp: false, W / 2f, H / 2f, 0f, H);
                if (!Same(buf, before)) ok = false;

                buf = Frame();
                before = (byte[])buf.Clone();
                CursorOverlay.Draw(null, W, H, rowsBottomUp: false, W / 2f, H / 2f, W, H);
                CursorOverlay.Draw(buf, 0, H, rowsBottomUp: false, W / 2f, H / 2f, W, H);
                CursorOverlay.Draw(buf, W, H, rowsBottomUp: false, W / 2f, H / 2f, 0f, 0f);
                if (!Same(buf, before)) ok = false;

                if (!ok)
                {
                    Console.WriteLine("    ✗ ⑤ 不该画的情况改动了像素（屏幕尺寸/空缓冲/零尺寸任一没早退）");
                    fail++;
                }
                else
                {
                    Console.WriteLine("    ✓ ⑤ 不画时像素一个字节都没动（开关关掉真的什么都不做）");
                }
            }

            return fail;
        }

        private static byte[] Frame()
        {
            byte[] buf = new byte[W * H * 4];
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            return buf;
        }

        private static void InkedCenterOfMass(byte[] buf, out float cx, out float cy)
            => InkedCenterOfMass(buf, W, out cx, out cy);

        // 判定「被画过」的标准：RGB 任一通道非零（背景是全黑）。
        private static void InkedCenterOfMass(byte[] buf, int width, out float cx, out float cy)
        {
            long n = 0, sx = 0, sy = 0;
            int height = buf.Length / (width * 4);
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int i = rowBase + x * 4;
                    if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0)
                    {
                        n++;
                        sx += x;
                        sy += y;
                    }
                }
            }
            cx = n == 0 ? 0f : sx / (float)n;
            cy = n == 0 ? 0f : sy / (float)n;
        }

        // 被画过的像素的外接框（背景全黑，判据 = RGB 任一通道非零）。
        private static void InkedBounds(byte[] buf, int width,
            out int x0, out int y0, out int x1, out int y1)
        {
            x0 = int.MaxValue; y0 = int.MaxValue; x1 = -1; y1 = -1;
            int height = buf.Length / (width * 4);
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int i = rowBase + x * 4;
                    if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
            if (x1 < 0) { x0 = y0 = x1 = y1 = -1; }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}