using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Domain.Infrastructure.Motion;
using UnityEditor;
using UnityEngine;

namespace Editor.EditorTools
{
     /// <summary>
    /// 批量根位移烘焙 —— 输出到 {角色}_rootmotion.json。
    ///
    /// 【改动】不再生成 C# 静态类。位移必须是 JSON，理由是硬的：
    ///   ① Go 服务器读不了 C# 静态类。位移参与权威模拟，必须跨语言。
    ///   ② HitboxEditor 要在编辑期读位移来算【逻辑原点】（判定框的坐标基准），
    ///      读 JSON 天经地义。
    ///
    /// 【与 boxes.json 分开】位移是自动生成的（随时可重烘），判定框是手工创作的
    /// （重画要几小时）。混在一个文件里，一次误重烘可能毁掉手工数据，
    /// 且 git diff 被几百行数字淹没，判定框的改动根本看不出来。
    ///
    /// 【采样帧语义】Motion[i] = pose((i+1)/60) - pose(i/60)，即第 i+1 帧【期间】的位移。
    /// 于是逻辑第 F 帧：位置 = sum(Motion[0..F-1]) = pose(F/60) - pose(0)，
    /// 动画 = pose(F/60)。运行时 FighterView 与 HitboxEditor 都按这个语义对齐。
    /// </summary>
    public sealed class BatchRootMotionBaker : EditorWindow
    {
        public enum ForwardAxis { Z, X }
 
        private const string LibraryFolder = "Assets/Resources/BoxData";
        private const int SampleRate = 60;
 
        private string characterId = "Frank";
        private GameObject rigPrefab;
        private ForwardAxis forwardAxis = ForwardAxis.Z;
        private float noiseThreshold = 0.005f;
 
        private readonly List<AnimationClip> clips = new List<AnimationClip>();
        private readonly List<BakeResult> results = new List<BakeResult>();
        private Vector2 listScroll;
 
        private string RootMotionPath => $"{LibraryFolder}/{characterId}_rootmotion.json";
 
        private sealed class BakeResult
        {
            public string ClipName;
            public Vector2[] Deltas;
            public float NetX, NetY, TravelX, TravelY;
            public bool HasMotion;
            public int Frames;
        }
 
        [MenuItem("FG/Batch Root Motion Baker")]
        private static void Open() => GetWindow<BatchRootMotionBaker>("批量位移烘焙");
 
        private void OnGUI()
        {
            EditorGUILayout.LabelField("① 设置", EditorStyles.boldLabel);
            characterId = EditorGUILayout.TextField("角色 ID", characterId);
            rigPrefab = (GameObject)EditorGUILayout.ObjectField(
                "角色 Prefab", rigPrefab, typeof(GameObject), false);
            forwardAxis = (ForwardAxis)EditorGUILayout.EnumPopup("前进轴", forwardAxis);
            noiseThreshold = EditorGUILayout.FloatField("噪声阈值(米)", noiseThreshold);
 
            EditorGUILayout.HelpBox(
                $"输出：{RootMotionPath}\n" +
                "与 {角色}_boxes.json 分开：位移是自动生成的，判定框是手工创作的。\n" +
                "前进轴：看 Clip 的 Average Velocity 哪一维非零。3D 角色通常是 Z。",
                MessageType.Info);
 
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("② 拖入 Clip / fbx / 文件夹", EditorStyles.boldLabel);
 
            Rect dropArea = GUILayoutUtility.GetRect(0, 55, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, clips.Count == 0
                ? "拖到这里"
                : $"已加入 {clips.Count} 个 Clip —— 可继续拖入追加");
            HandleDragAndDrop(dropArea);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空列表")) { clips.Clear(); results.Clear(); }
 
                using (new EditorGUI.DisabledScope(clips.Count == 0 || rigPrefab == null))
                {
                    if (GUILayout.Button($"批量烘焙 ({clips.Count})")) BakeAll();
                }
            }
 
            if (rigPrefab == null && clips.Count > 0)
                EditorGUILayout.HelpBox("需要角色 Prefab 才能采样。", MessageType.Warning);
 
            if (results.Count == 0) return;
 
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("③ 汇总表", EditorStyles.boldLabel);
            DrawSummary();
 
            EditorGUILayout.Space();
            GUI.backgroundColor = Color.yellow;
            if (GUILayout.Button(
                    $"写入 {characterId}_rootmotion.json（{results.Count(r => r.HasMotion)} 个有位移）",
                    GUILayout.Height(28)))
            {
                WriteJson();
            }
            GUI.backgroundColor = Color.white;
 
            EditorGUILayout.HelpBox(
                "写入是【合并】：本次烘的招式覆盖同名项，文件里其他招式原样保留。\n" +
                "覆盖前自动备份 .bak。判定框在另一个文件里，完全不受影响。",
                MessageType.None);
        }
 
        private void DrawSummary()
        {
            int withMotion = results.Count(r => r.HasMotion);
            EditorGUILayout.HelpBox(
                $"共 {results.Count} 个 Clip，{withMotion} 个有位移，" +
                $"{results.Count - withMotion} 个无位移（原地动画或噪声）。\n" +
                "无位移的 Clip 不写入 JSON —— 运行时取不到就是原地不动，语义正确。",
                MessageType.Info);
 
            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.Height(200));
            foreach (BakeResult r in results.OrderByDescending(x => Mathf.Abs(x.NetX)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.color = r.HasMotion ? Color.white : new Color(1f, 1f, 1f, 0.4f);
                    EditorGUILayout.LabelField(r.ClipName, GUILayout.Width(240));
                    EditorGUILayout.LabelField($"{r.Frames}帧", GUILayout.Width(45));
                    EditorGUILayout.LabelField(
                        r.HasMotion ? $"前后 {r.NetX:F3}  上下 {r.NetY:F3}" : "无位移",
                        GUILayout.Width(200));
                    GUI.color = Color.white;
                }
            }
            EditorGUILayout.EndScrollView();
 
            if (results.All(r => !r.HasMotion))
            {
                EditorGUILayout.HelpBox(
                    "全部无位移。若 Clip 的 Average Velocity 明明非零，多半是【前进轴选错】：\n" +
                    "(0.000, 0.000, 0.006) → 位移在第 3 个数 = Z 轴\n" +
                    "(0.006, 0.000, 0.000) → 位移在第 1 个数 = X 轴",
                    MessageType.Warning);
            }
        }
 
        // ===================== 烘焙（采样逻辑保持不变，它是对的）=====================
 
        private void BakeAll()
        {
            results.Clear();
 
            for (int i = 0; i < clips.Count; i++)
            {
                AnimationClip c = clips[i];
                EditorUtility.DisplayProgressBar("批量烘焙", c.name, i / (float)clips.Count);
 
                Vector2[] deltas = Sample(c, rigPrefab, forwardAxis);
                var r = new BakeResult { ClipName = c.name, Deltas = deltas, Frames = deltas.Length };
 
                foreach (Vector2 d in deltas)
                {
                    r.NetX += d.x; r.NetY += d.y;
                    r.TravelX += Mathf.Abs(d.x); r.TravelY += Mathf.Abs(d.y);
                }
 
                // 按累积位移判断（噪声不累积，真位移会累积），而非单帧阈值——
                // 单帧阈值会把小位移动画（轻踢重心前压，每帧仅 0.0002）整段误杀成 0
                r.HasMotion =
                    Mathf.Abs(r.NetX) >= noiseThreshold || r.TravelX >= noiseThreshold * 2f ||
                    Mathf.Abs(r.NetY) >= noiseThreshold || r.TravelY >= noiseThreshold * 2f;
 
                results.Add(r);
            }
 
            EditorUtility.ClearProgressBar();
        }
 
        /// <summary>
        /// 采样。public static 以便 HitboxEditor 复用同一份实现 ——
        /// 两处各写一遍必然走样（之前 HitboxEditor 那份就有 off-by-one：
        /// 采样点写成 (f-1)/60，导致 deltas[0] 恒为零、整体错位一帧、末帧位移丢失）。
        ///
        /// 帧语义：deltas[f-1] = pose(f/60) - pose((f-1)/60)，即第 f 帧【期间】的位移。
        /// </summary>
        public static Vector2[] Sample(AnimationClip clip, GameObject rigPrefab, ForwardAxis axis)
        {
            GameObject sampler = rigPrefab != null
                ? Instantiate(rigPrefab)
                : new GameObject("Sampler");
            try
            {
                // 强制开启——这是烘焙能读到根位移的前提，不要指望 Prefab 上勾对了
                var animator = sampler.GetComponent<Animator>();
                if (animator == null) animator = sampler.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    animator.applyRootMotion = true;
                }
                else
                {
                    Debug.LogWarning(
                        $"[Baker] {rigPrefab?.name} 上找不到 Animator。" +
                        "Generic 动画可能仍能采样，但若烘出全 0，先检查这里。");
                }
 
                sampler.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
 
                int frames = Mathf.Max(1, Mathf.RoundToInt(clip.length * SampleRate));
                var deltas = new Vector2[frames];
 
                clip.SampleAnimation(sampler, 0f);
                Vector3 prev = sampler.transform.position;
 
                for (int f = 1; f <= frames; f++)
                {
                    clip.SampleAnimation(sampler, f / (float)SampleRate);
                    Vector3 pos = sampler.transform.position;
                    float horizontal = axis == ForwardAxis.Z ? pos.z - prev.z : pos.x - prev.x;
                    deltas[f - 1] = new Vector2(horizontal, pos.y - prev.y);
                    prev = pos;
                }
                return deltas;
            }
            finally
            {
                DestroyImmediate(sampler);
            }
        }
 
        /// <summary>降噪：整轴累积位移可忽略 → 整轴归零。public static 供 HitboxEditor 复用。</summary>
        public static bool Denoise(Vector2[] deltas, float threshold)
        {
            float netX = 0f, netY = 0f, travelX = 0f, travelY = 0f;
            foreach (Vector2 d in deltas)
            {
                netX += d.x; netY += d.y;
                travelX += Mathf.Abs(d.x); travelY += Mathf.Abs(d.y);
            }
 
            bool keepX = Mathf.Abs(netX) >= threshold || travelX >= threshold * 2f;
            bool keepY = Mathf.Abs(netY) >= threshold || travelY >= threshold * 2f;
 
            for (int i = 0; i < deltas.Length; i++)
                deltas[i] = new Vector2(keepX ? deltas[i].x : 0f, keepY ? deltas[i].y : 0f);
 
            return keepX || keepY;
        }
 
        // ===================== 写入 JSON =====================
 
        private void WriteJson()
        {
            Directory.CreateDirectory(LibraryFolder);
 
            if (File.Exists(RootMotionPath))
                File.Copy(RootMotionPath, RootMotionPath + ".bak", overwrite: true);
 
            CharacterRootMotion data = Load();
            data.CharacterId = characterId;
            data.ForwardAxis = forwardAxis.ToString();
 
            int updated = 0, created = 0;
 
            foreach (BakeResult r in results)
            {
                // 无位移的招式不写入 —— 运行时取不到就是原地不动，语义正确，
                // 也让 JSON 只包含真正有位移的招式，文件小、可读
                if (!r.HasMotion)
                {
                    data.Moves.RemoveAll(m => m.MoveId == r.ClipName); // 重烘后没位移了 → 清掉旧的
                    continue;
                }
 
                MoveRootMotion move = data.Find(r.ClipName);
                if (move == null)
                {
                    move = new MoveRootMotion { MoveId = r.ClipName };
                    data.Moves.Add(move);
                    created++;
                }
                else
                {
                    updated++;
                }
 
                move.Frames = r.Frames;
                move.Motion = r.Deltas;
            }
 
            data.Moves.Sort((a, b) => string.CompareOrdinal(a.MoveId, b.MoveId));
            File.WriteAllText(RootMotionPath, JsonUtility.ToJson(data, true));
            AssetDatabase.Refresh();
 
            Debug.Log($"[BatchRootMotionBaker] 写入 {RootMotionPath}：" +
                      $"更新 {updated}、新建 {created}，共 {data.Moves.Count} 个有位移的招式。" +
                      "判定框在 boxes.json 里，未受影响。");
        }
 
        private CharacterRootMotion Load()
        {
            if (!File.Exists(RootMotionPath))
                return new CharacterRootMotion { CharacterId = characterId };
 
            try
            {
                var data = JsonUtility.FromJson<CharacterRootMotion>(File.ReadAllText(RootMotionPath));
                return data ?? new CharacterRootMotion { CharacterId = characterId };
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BatchRootMotionBaker] 解析失败，将新建：{e.Message}");
                return new CharacterRootMotion { CharacterId = characterId };
            }
        }
 
        // ===================== 拖拽收集 =====================
 
        private void HandleDragAndDrop(Rect area)
        {
            Event evt = Event.current;
            if (!area.Contains(evt.mousePosition)) return;
 
            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (Object obj in DragAndDrop.objectReferences)
                    CollectClips(obj);
                clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                evt.Use();
            }
        }
 
        private void CollectClips(Object obj)
        {
            if (obj is AnimationClip direct)
            {
                AddClip(direct);
                return;
            }
 
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return;
 
            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { path }))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(p))
                        if (sub is AnimationClip c) AddClip(c);
                }
                return;
            }
 
            foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(path))
                if (sub is AnimationClip c) AddClip(c);
        }
 
        private void AddClip(AnimationClip c)
        {
            if (c == null || c.name.StartsWith("__preview__")) return;
            if (!clips.Contains(c)) clips.Add(c);
        }
    }
}