// UiFonts 的桩：真实实现要从场景里的 RDConstants 取字体，桩里什么都不做。
// 预览只关心几何，不关心字形。
using TMPro;

namespace SpectreReplay.Ui
{
    internal static class UiFonts
    {
        internal static void Invalidate() { }
        internal static void Apply(TMP_Text text) { }
    }
}