using UnityEngine;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 主题：颜色与尺寸常量。
    ///
    /// 这一版的全部尺寸都写死在这里，没有一处交给布局系统去算。
    /// 原因很实际：Unity 的 LayoutGroup + LayoutElement 嵌套两层以上时，
    /// 子件尺寸会被布局系统在布局过程中回写，结果不可预期——实测出现过
    /// 子件被算成 0 高、行被内容撑开、控件铺满整行。既然算不准，
    /// 就不让它算：所有矩形由常量直接给出。
    /// </summary>
    internal static class Theme
    {
        // ── 颜色 ────────────────────────────────────────────────
        internal static readonly Color Overlay = new Color(0f, 0f, 0f, 0.80f);
        internal static readonly Color Panel = new Color(0.105f, 0.105f, 0.125f, 1f);
        internal static readonly Color Sidebar = new Color(0.135f, 0.135f, 0.165f, 1f);
        internal static readonly Color Header = new Color(0.175f, 0.175f, 0.215f, 1f);

        internal static readonly Color RowA = new Color(1f, 1f, 1f, 0.030f);
        internal static readonly Color RowB = new Color(1f, 1f, 1f, 0.062f);

        internal static readonly Color NavIdle = new Color(1f, 1f, 1f, 0.075f);
        internal static readonly Color NavHover = new Color(1f, 1f, 1f, 0.14f);
        internal static readonly Color NavActive = new Color(0.29f, 0.60f, 0.88f, 1f);
        internal static readonly Color GroupHead = new Color(1f, 1f, 1f, 0.045f);

        internal static readonly Color Control = new Color(0.255f, 0.255f, 0.305f, 1f);
        internal static readonly Color ControlHover = new Color(0.33f, 0.33f, 0.39f, 1f);
        internal static readonly Color Track = new Color(0.21f, 0.21f, 0.26f, 1f);
        internal static readonly Color Accent = new Color(0.29f, 0.60f, 0.88f, 1f);
        internal static readonly Color AccentDim = new Color(0.19f, 0.30f, 0.42f, 1f);

        internal static readonly Color Text = new Color(0.93f, 0.93f, 0.95f, 1f);
        internal static readonly Color TextMuted = new Color(0.58f, 0.58f, 0.65f, 1f);
        internal static readonly Color TextFaint = new Color(0.42f, 0.42f, 0.48f, 1f);
        internal static readonly Color TextOnAccent = Color.white;

        // ── 尺寸 ────────────────────────────────────────────────
        internal const float PanelW = 1080f;
        /// <summary>面板高度的**下限**。实际高度按最高的那一页量出来。</summary>
        internal const float PanelH = 720f;
        /// <summary>面板高度下限，免得页面太少时缩成一个奇怪的窄条。</summary>        internal const float PanelMinH = 320f;        /// <summary>面板高度上限，免得长页面把面板顶出屏幕。</summary>
        internal const float MaxPanelH = 1120f;
        /// <summary>内容区与面板底边之间留的余量。</summary>
        internal const float ContentPad = 16f;        /// <summary>内容区与顶栏之间的留白。第一行紧贴顶栏会像浮在标题上。</summary>        internal const float ContentTopPad = 10f;
        /// <summary>页面区宽度（侧栏之外的部分）。</summary>
        internal const float PageAreaW = PanelW - SidebarW;
        internal const float SidebarW = 250f;
        internal const float HeaderH = 78f;

        internal const float PadX = 24f;
        internal const float RowH = 56f;
        internal const float RowGap = 4f;
        internal const float NoteH = 68f;   // 放得下两行说明文字
        internal const float GroupHeadH = 44f;

        internal const float CtlToggleW = 56f;
        internal const float CtlToggleH = 32f;
        internal const float CtlButtonH = 38f;
        internal const float CtlSliderW = 320f;      // 只是轨道宽
        internal const float CtlSliderValueW = 88f;   // 数值段
        internal const float CtlSliderH = 28f;
        internal const float CtlDropW = 300f;
        internal const float CtlDropH = 40f;
        internal const float CtlInputH = 40f;
        internal const float CtlInputW = 420f;

        internal const float NavH = 52f;
        internal const float FontTitle = 30f;
        internal const float FontBody = 24f;
        internal const float FontSmall = 20f;
        internal const float FontTiny = 18f;

        /// <summary>标签与控件之间的固定间隙。</summary>
        internal const float LabelGap = 16f;

        internal static float TextScale = 1f;

        /// <summary>隔行底色。行一多，没有交替色就分不清行边界。</summary>
        internal static Color Zebra(int index)
            => (index & 1) == 0 ? RowA : RowB;
    }
}
