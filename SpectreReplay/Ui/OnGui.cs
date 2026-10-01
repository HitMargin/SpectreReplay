using System;
using System.Collections.Generic;
using UnityEngine;
using UnityModManagerNet;
using ModEntry = UnityModManagerNet.UnityModManager.ModEntry;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// UMM 自带设置界面（IMGUI / GUILayout）。
    ///
    /// **这一整套是推倒重写的。** 之前用自建 Canvas + RectTransform 画了
    /// 100KB 的 UI（Gfx / PageBuilder / Theme / UiFonts / PanelHost），
    /// 踩的坑一个接一个：EventSystem 与游戏的撞车、ScrollRect 归位时机、
    /// raycast 标志、RectMask2D 裁剪、TMP 字体回退……每一个都要单独调，
    /// 而每一个的失败方式都长得像"界面坏了"。
    ///
    /// 换成 UMM 的 OnGUI 之后，这些**整类问题都不存在**了：
    ///   · 画布、EventSystem、GraphicRaycaster 全是 UMM 管的
    ///   · 点击与滚轮由 IMGUI 自己处理，不吃射线
    ///   · 字体用 UMM 的皮肤，中文由 UMM 自己解决（不再需要 TMP 资源）
    ///   · 布局用 GUILayout，不存在锚点算错、sizeDelta 恒为 0 这类坑
    ///
    /// 代价是外观朴素（UMM 的默认皮肤），但**功能与可维护性上的收益
    /// 远大于外观上的损失**——设置页能稳定点得动，比好看重要。
    /// </summary>
    internal static class OnGui
    {
        /// <summary>UMM 选中本模组并显示设置时为真。画界面前置条件。</summary>
        private static bool _visible;

        /// <summary>
        /// 上一次画的是哪一页。0 起始。
        ///
        /// 必须存在静态字段里：OnGUI 每帧被调用好几遍（IMGUI 的
        /// Layout/Repaint 两趟），任何"当帧新建"的局部状态都会在
        /// Layout 与 Repaint 之间丢掉，控件就会跳位置。
        /// </summary>
        private static int _tab;

        /// <summary>
        /// 文本输入框的当前值。TextField 自己不保存内容——每帧传进去什么
        /// 就显示什么，所以必须存在这里，并且只在**值真的变了**时写回配置，
        /// 否则玩家打字打到一半焦点一丢就回滚。
        /// </summary>
        private static string _outputDir;

        /// <summary>玩家正在改的文本（可能还没提交）。</summary>
        private static string _textDraft;

        /// <summary>
        /// 挂失败的补丁数。由 Main 注入。
        ///
        /// UI 层不直接引 PatchLoader：编译隔离，也让 OnGui.cs 不依赖
        /// Harmony。挂不上的补丁 = 对应功能静默消失，而设置页看起来
        /// 一切正常——必须在设置页顶部明说，否则玩家只会觉得
        /// "这个开关点了没反应"，去翻日志的不到。
        /// </summary>
        internal static int PatchFailures;

        private static readonly string[] TabKeys =
        {
            "page.record", "page.playback", "page.render", "page.about",
        };

        /// <summary>
        /// 选中标签页的底色。原本在 Theme.cs 里——那一整套跟着自建
        /// Canvas 一起删了，只剩这一个颜色还有用，就近定义。
        /// </summary>
        private static readonly Color Accent = new Color(0.24f, 0.62f, 0.92f);

        internal static void OnShow(ModEntry modEntry)
        {
            _visible = true;
            // 每次打开都重取一次：目录里的 .srp 可能刚被录出来。
            _outputDir = Settings.OutputDir;
        }

        internal static void OnHide(ModEntry modEntry)
        {
            _visible = false;
            // 关窗口时提交文本框里还没按回车的内容——UMM 的窗口一关，
            // 玩家看不到任何"未保存"的提示，不提交就等于白打。
            CommitText();
        }

        /// <summary>UMM 每帧调用。mod.Active 为假时根本不画。</summary>
        internal static void OnGUI(ModEntry modEntry)
        {
            if (!_visible)
            {
                return;
            }
            Loc.RefreshLanguage();
            // 面板刚打开时先把文本框填上，之后由玩家编辑
            if (_textDraft == null)
            {
                _textDraft = Settings.OutputDir;
            }

            GUILayout.Space(4f);
            DrawPatchWarning();
            DrawTabs();
            GUILayout.Space(8f);
            DrawPage();
        }

        /// <summary>
        /// 补丁挂不上时，在设置页顶部说一次。
        ///
        /// **这一步不能省。** 挂不上的补丁 = 对应功能静默消失：玩家看到的是
        /// "某个开关点了没反应"，根本猜不到是补丁没挂上，而原因只在日志里。
        /// Spectre 专门为这件事做了 ShowApplyFailureSummary，注释写得很直白。
        ///
        /// 早先那版是弹一条横幅，跟着自建 Canvas 界面一起删掉了；这里改成
        /// 常驻在设置页顶部——UMM 的 OnGUI 没有独立的提示层，而设置页是玩家
        /// 唯一会主动打开的地方，在这里提示比藏在日志里强得多。
        /// </summary>
        private static void DrawPatchWarning()
        {
            if (PatchFailures <= 0)
            {
                return;
            }
            Color old = GUI.backgroundColor;
            // 用醒目的底色：这不是能划过去的信息，玩家必须看见
            GUI.backgroundColor = new Color(0.78f, 0.24f, 0.24f);
            GUILayout.Label(
                Loc.T("ui.patchFailed") + "  (" + PatchFailures + ")",
                GUILayout.Height(24f));
            GUI.backgroundColor = old;
        }

        // ── 分页 ────────────────────────────────────────────────

        private static void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabKeys.Length; i++)
            {
                Color old = GUI.backgroundColor;
                // 选中的页用强调色，不选中的压暗——比只靠按钮高亮更容易分辨
                GUI.backgroundColor = i == _tab
                    ? new Color(0.24f, 0.62f, 0.86f)
                    : new Color(1f, 1f, 1f, 0.72f);
                if (GUILayout.Button(Loc.T(TabKeys[i]),
                        GUILayout.Height(26f), GUILayout.ExpandWidth(true)))
                {
                    _tab = i;
                }
                GUI.backgroundColor = old;
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawPage()
        {
            GUILayout.BeginVertical();
            switch (_tab)
            {
                case 0:
                    Pages.Pages.DrawRecord();
                    break;
                case 1:
                    Pages.Pages.DrawPlayback();
                    break;
                case 2:
                    Pages.Pages.DrawRender();
                    break;
                default:
                    Pages.Pages.DrawAbout();
                    break;
            }
            GUILayout.EndVertical();
        }

        // ── 控件 ────────────────────────────────────────────────
        // 都排成"标签在左、控件在右"的一行。用固定标签宽度而不是
        // GUILayout.Label + 自动排布：不同语言下标签长度差很多，
        // 自动排布会让控件列左右跳动，拖滑条时尤其明显。

        internal const float LabelW = 190f;

        /// <summary>一行开关。</summary>
        internal static void Row(string labelKey, bool value, Action<bool> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            bool now = GUILayout.Toggle(value, GUIContent.none, GUILayout.Width(20f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (now != value)
            {
                onChange(now);
            }
        }

        /// <summary>一行整数滑条 + 右侧当前值。</summary>
        internal static void RowSlider(string labelKey, float value, float min, float max,
            Func<float, string> fmt, Action<float> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            float now = GUILayout.HorizontalSlider(value, min, max,
                GUILayout.Width(200f));
            GUILayout.Label(fmt(now), GUILayout.Width(90f));
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(now, value))
            {
                onChange(now);
            }
        }

        /// <summary>一行下拉。选项是已本地化的文字。</summary>
        internal static void RowDropdown(string labelKey, string[] options, int current,
            Action<int> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            int now = GUILayout.SelectionGrid(current, options, 1,
                GUILayout.Width(200f), GUILayout.Height(24f));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (now != current)
            {
                onChange(now);
            }
        }

        /// <summary>一段说明文字。</summary>
        internal static void Note(string labelKey)
        {
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW + 300f));
        }

        /// <summary>一段不带标签键的说明文字（内容已经本地化过了）。</summary>
        internal static void NoteText(string text)
        {
            GUILayout.Label(text, GUILayout.Width(LabelW + 300f));
        }

        /// <summary>分组标题。</summary>
        internal static void Group(string labelKey)
        {
            GUILayout.Space(6f);
            GUILayout.Label(Loc.T(labelKey));
        }

        /// <summary>一整行按钮。</summary>
        internal static void Button(string labelKey, Action onClick, bool wide)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T(labelKey),
                    GUILayout.Height(28f), GUILayout.Width(wide ? 260f : 140f)))
            {
                onClick();
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>文本输入行。改动在失焦或按回车时提交。</summary>
        internal static void RowInput(string labelKey, Action<string> onChange)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(labelKey), GUILayout.Width(LabelW));
            string now = GUILayout.TextField(_textDraft ?? "", GUILayout.Width(380f));
            if (now != _textDraft)
            {
                _textDraft = now;
                onChange(now);
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>提交文本框内容。关窗口时也会调。</summary>
        internal static void CommitText()
        {
            if (_textDraft != null && _textDraft != Settings.OutputDir)
            {
                Settings.OutputDir = _textDraft;
                Settings.Commit();
            }
        }

        /// <summary>供 Pages 在改了设置之后重画用。IMGUI 每帧重画，本身无需重画。</summary>
        internal static void Refresh()
        {
        }

        /// <summary>预留给未来的"当前状态"文本。</summary>
        internal static string Describe() => string.Empty;
    }
}