using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SpectreReplay.Ui
{
    /// <summary>
    /// 挡住 IMGUI 窗口后方的游戏输入。
    ///
    /// <para>IMGUI 的 OnGUI 画在屏幕最上层，但它**完全不参与 Unity 的
    /// EventSystem 射线检测**。玩家在设置窗口里点开关/滑条/按钮时，那一击
    /// 会同时穿透到下面的游戏 UI：可能按到游戏里的暂停、重开，甚至被判定
    /// 成打拍输入。</para>
    ///
    /// <para>做法：建一个 ScreenSpaceOverlay Canvas（sortingOrder 32000，
    /// 高于游戏 UI、低于 UMM），在窗口矩形上放一张全透明的
    /// <see cref="Image"/>，raycastTarget = true。它不可见，但会让 uGUI 的
    /// 射线检测在窗口范围内吃掉点击，游戏自己的按钮就收不到这一击。
    /// 窗口外不铺，游戏照常可操作。</para>
    ///
    /// <para>每帧只在矩形变化时更新 RectTransform（uGUI 每改一次都要重建
    /// 布局，OnGUI 一帧会跑好几趟）。</para>
    /// </summary>
    internal static class UiInputBlock
    {
        private static GameObject _root;
        private static Image _image;
        private static Rect _last;
        private static bool _placed;

        /// <summary>设置窗口矩形（IMGUI 坐标：左上为 0）。width/height ≤ 0 = 撤除。</summary>
        internal static void Place(Rect guiRect)
        {
            if (guiRect.width <= 0f || guiRect.height <= 0f)
            {
                Clear();
                return;
            }
            try
            {
                EnsureRoot();
                if (_root == null || _image == null) return;
                if (!_root.activeSelf) _root.SetActive(true);
                // uGUI 的 (0,0) 在左下角；IMGUI 在左上角。转一次坐标。
                var pos = new Vector2(guiRect.x, Screen.height - guiRect.yMax);
                var size = new Vector2(guiRect.width, guiRect.height);
                if (_placed && _last.position == pos && _last.size == size) return;
                _last = new Rect(pos, size);
                _placed = true;
                var rt = (RectTransform)_root.transform;
                rt.anchoredPosition = pos;
                rt.sizeDelta = size;
            }
            catch { }
        }

        internal static void Clear()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            _placed = false;
        }

        internal static void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
                _image = null;
            }
            _placed = false;
        }

        private static void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("SpectreReplay.InputBlock");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 高于游戏内所有 UI（都在几百以内），低于 UMM（32767）：
            // UMM 自己的面板仍然能盖在上面并被点中。
            canvas.sortingOrder = 32000;
            _root.AddComponent<GraphicRaycaster>();
            // 根上就是那张透明挡板：整块 RectTransform 直接覆盖窗口范围。
            var rt = (RectTransform)_root.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
            _image = _root.AddComponent<Image>();
            _image.color = new Color(0f, 0f, 0f, 0f);
            _image.raycastTarget = true;
            // 完全透明也要参与射线：cullTransparentMesh 只在 alpha==0 时
            // 跳过绘制，不影响 GraphicRaycaster 的命中判定。
            _image.canvasRenderer.cullTransparentMesh = true;
        }
    }
}
