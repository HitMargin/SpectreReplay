namespace SpectreReplay.Features.Playback
{
    /// <summary>
    /// 回放引擎门面。回放由录制时的按键序列驱动：把按键按 songposition 重放，
    /// 判定因此与当初完全一致。
    /// 本轮先落占位，数据模型与编码器接入后填实。
    /// </summary>
    internal static class PlaybackEngine
    {
        internal static bool IsPlaying { get; private set; }

        internal static void TryStart()
        {
            // 依赖 Features\Recording 的回放数据；尚未接入时明确不做"假启动"，
            // 否则用户点了没反应又没有任何提示。
        }

        internal static void Stop()
        {
            IsPlaying = false;
        }
    }
}
