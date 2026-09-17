// Assets/Editor/ExplodeParts.cs
// 선택한 모델 루트의 직계 자식(파트)을 모델 중심에서 바깥으로 균등하게 벌리는 에디터 창.
// 메뉴: Tools > Explode Parts
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class ExplodeParts : EditorWindow
{
    enum Mode { UniformGap, ScaleFromCenter }

    Mode mode = Mode.UniformGap;
    float gap = 0.15f;      // UniformGap: 모델 최대 변 길이의 배수 (모델 크기에 무관하게 같은 비율)
    float scale = 1.3f;     // ScaleFromCenter: 중심→파트 벡터 배율 (1 = 원위치)

    // 처음 Apply 했을 때의 localPosition. Reset 시 복원.
    readonly Dictionary<Transform, Vector3> original = new Dictionary<Transform, Vector3>();

    [MenuItem("Tools/Explode Parts")]
    static void Open() => GetWindow<ExplodeParts>("Explode Parts");

    void OnSelectionChange() => Repaint();

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "모델 루트를 선택하세요. Renderer를 가진 직계 자식 하나가 파트 하나로 취급됩니다.\n" +
            "슬라이더를 움직이면 즉시 반영되고, Reset으로 원위치로 돌아갑니다. Ctrl+Z도 됩니다.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        mode = (Mode)EditorGUILayout.EnumPopup("Mode", mode);
        if (mode == Mode.UniformGap)
            gap = EditorGUILayout.Slider(new GUIContent("Gap (x model size)", "모든 파트를 같은 거리만큼 바깥으로 이동"), gap, 0f, 1f);
        else
            scale = EditorGUILayout.Slider(new GUIContent("Scale", "중심에서의 거리를 배율로 확대"), scale, 1f, 3f);
        bool changed = EditorGUI.EndChangeCheck();

        var roots = Roots();
        int partCount = roots.Sum(r => Parts(r).Count);
        EditorGUILayout.LabelField($"Selected roots: {roots.Count}, parts: {partCount}");

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(partCount < 2))
                if (GUILayout.Button("Apply")) Apply(roots);
            using (new EditorGUI.DisabledScope(original.Count == 0))
                if (GUILayout.Button("Reset")) ResetAll();
        }

        if (changed && original.Count > 0) Apply(roots);
    }

    static List<Transform> Roots() =>
        Selection.GetTransforms(SelectionMode.TopLevel | SelectionMode.Editable).ToList();

    static List<Transform> Parts(Transform root)
    {
        var parts = new List<Transform>();
        foreach (Transform child in root)
            if (child.GetComponentInChildren<Renderer>(true) != null) parts.Add(child);
        return parts;
    }

    static Bounds WorldBounds(Transform t)
    {
        var rs = t.GetComponentsInChildren<Renderer>(true);
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    void Apply(List<Transform> roots)
    {
        foreach (var root in roots)
        {
            var parts = Parts(root);
            if (parts.Count < 2) continue;

            // 원위치 기록 후 일단 원위치로 되돌려서 누적 이동을 막는다.
            foreach (var p in parts)
            {
                if (!original.ContainsKey(p)) original[p] = p.localPosition;
                Undo.RecordObject(p, "Explode Parts");
                p.localPosition = original[p];
            }

            // 원위치 기준으로 파트별 중심과 모델 전체 바운드를 계산한다.
            var centers = parts.Select(p => WorldBounds(p).center).ToList();
            var all = WorldBounds(parts[0]);
            for (int i = 1; i < parts.Count; i++) all.Encapsulate(WorldBounds(parts[i]));
            Vector3 modelCenter = all.center;
            float size = Mathf.Max(all.size.x, all.size.y, all.size.z);
            if (size <= 0f) continue;

            for (int i = 0; i < parts.Count; i++)
            {
                Vector3 dir = centers[i] - modelCenter;
                Vector3 offset;
                if (mode == Mode.UniformGap)
                {
                    if (dir.magnitude < 1e-4f * size) continue; // 중심에 있는 파트는 그대로 둔다.
                    offset = dir.normalized * (gap * size);
                }
                else
                {
                    offset = dir * (scale - 1f);
                }

                var p = parts[i];
                Vector3 localOffset = p.parent != null ? p.parent.InverseTransformVector(offset) : offset;
                p.localPosition = original[p] + localOffset;
            }
        }
    }

    void ResetAll()
    {
        foreach (var kv in original)
        {
            if (kv.Key == null) continue;
            Undo.RecordObject(kv.Key, "Reset Explode Parts");
            kv.Key.localPosition = kv.Value;
        }
        original.Clear();
    }
}
