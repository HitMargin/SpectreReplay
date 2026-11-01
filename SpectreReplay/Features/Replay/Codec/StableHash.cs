using System;
using System.Text;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 跨运行时稳定的哈希（FNV-1a 32 位）。
///
/// <para><b>为什么不能只用 string.GetHashCode()。</b>那是运行时实现细节：
/// Unity/Mono 升级后算法可能整体更换（.NET Core 已经按进程随机化），届时
/// 旧回放里存的哈希与新版运行时现算的必然对不上——所有存量回放被误判成
/// 「关卡数据错误」。FNV-1a 的规范是公开且冻结的：任何运行时、任何语言
/// 算出的值恒等。</para>
///
/// <para><b>归一化先于哈希。</b>浮点必须先按 InvariantCulture 格式化成文本
/// 再哈希（与旧算法同一前置），否则跨机器的浮点表示差异会进入哈希。
/// 本类只负责「文本 → 稳定 int」，归一化归调用方。</para>
///
/// <para><b>旧回放迁移。</b>哈希值随文件落盘，无法就地换算法——所以稳定哈希
/// 走**新的尾部可选段**（<see cref="ReplayConstants.MagicHashesV2"/>）：新录制
/// 双写（旧字段保持运行时哈希，供同运行的旧构建读取；新段供跨运行时校验），
/// 播放侧段存在则用稳定哈希、缺席则回落旧字段比较（旧回放行为不变）。</para>
/// </summary>
internal static class StableHash
{
    // FNV-1a 32 位参数（规范值，勿改——改了等于换算法，存量文件全部失效）
    private const uint OffsetBasis = 2166136261u;
    private const uint Prime = 16777619u;

    /// <summary>对 UTF-8 字节做 FNV-1a 32 位，结果按有符号 int 交付
    /// （与文件格式里的 Int32 字段同语义）。</summary>
    public static int Of(string text)
    {
        uint h = OffsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h *= Prime;
        }
        return unchecked((int)h);
    }
}
