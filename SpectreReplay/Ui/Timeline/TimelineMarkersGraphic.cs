using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SpectreReplay.Ui.Timeline;

/// <summary>
/// 时间轴标记的批量 UGUI 图形。标记数量大时只生成一次 Mesh，不为每条事件创建 GameObject。
/// </summary>
internal sealed class TimelineMarkersGraphic : MaskableGraphic
{
    private readonly List<Rect> _rects = new List<Rect>();
    private readonly List<Rect> _previous = new List<Rect>();
    private Color _markerColor = Color.white;

    internal TimelineMarkersGraphic()
    {
        raycastTarget = false;
        useLegacyMeshGeneration = false;
    }

    internal void SetMarkers(List<Rect> rects, Color color)
    {
        bool changed = _markerColor != color || rects.Count != _previous.Count;
        if (!changed)
        {
            for (int i = 0; i < rects.Count; i++)
            {
                if (rects[i] != _previous[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
            return;

        _markerColor = color;
        _rects.Clear();
        _rects.AddRange(rects);
        _previous.Clear();
        _previous.AddRange(rects);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        UIVertex vertex = UIVertex.simpleVert;
        vertex.color = _markerColor;
        for (int i = 0; i < _rects.Count; i++)
        {
            Rect rect = _rects[i];
            int start = vh.currentVertCount;
            vertex.position = new Vector3(rect.xMin, rect.yMin, 0f);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMax, rect.yMin, 0f);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMax, rect.yMax, 0f);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMin, rect.yMax, 0f);
            vh.AddVert(vertex);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
