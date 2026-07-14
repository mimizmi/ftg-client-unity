using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Motion;

namespace Editor.EditorTools
{
    /// <summary>
    /// 判定框可视化编辑器。判定框【必须】看着动画画——
    /// 你没法凭空想出"拳头第 6 帧伸到了 (0.55, 1.1)"，手写坐标根本做不准。
    ///
    /// 【两份 JSON，各司其职】
    ///   · {角色}_boxes.json      本编辑器【读写】：帧分割、判定框、无敌帧（手工创作）
    ///   · {角色}_rootmotion.json 本编辑器【只读】：每帧位移（BatchRootMotionBaker 自动生成）
    ///     位移用来算【逻辑原点】——判定框坐标的基准。
    ///
    /// 分开的理由：位移随时可重烘（机器产物），判定框重画要几小时（人的劳动）。
    /// 混在一起时，一次误重烘可能连带毁掉手工数据，且 git diff 被几百行数字淹没。
    ///
    /// 【帧语义 —— 三方必须一致，否则判定框会差一帧的位移】
    ///   Motion[i] = pose((i+1)/60) - pose(i/60)     ← 第 i+1 帧【期间】的位移
    ///   逻辑第 F 帧：位置 = sum(Motion[0..F-1])，动画 = pose(F/60)
    /// 所以预览采样点是 currentFrame/60（不是 (currentFrame-1)/60），
    /// 且角色钉在逻辑原点（不是世界零点）—— 这是"幽灵框"的根治。
    ///
    /// 【工作流】必须先烘位移，再画判定框。未烘位移时逻辑原点恒为零点，
    /// 你会对着漂移后的角色画框，坐标里混进漂移量，游戏里框就偏到前/后方。
    ///
    /// 形状用矩形 AABB：格斗游戏的绝对主流（街霸/GG/KOF 全是），且天然定点数友好——
    /// AABB 只需四次整数比较，跨语言逐位一致；OBB 要算投影轴、胶囊要开平方，
    /// 浮点误差会让 Unity 与将来的 Go 服务器判定对不上。
    /// </summary>
    public sealed class HitboxEditor : EditorWindow
    {
        // ---- 数据文件 ----
        private const string LibraryFolder = "Assets/Resources/BoxData";
        private string BoxPath => $"{LibraryFolder}/{characterId}_boxes.json";
        private string RootMotionPath => $"{LibraryFolder}/{characterId}_rootmotion.json";
 
        private string characterId = "Frank";
        private string loadedCharacterId;
 
        /// <summary>手工数据（读写）</summary>
        private CharacterBoxData boxData = new CharacterBoxData();
 
        /// <summary>位移数据（只读，用于算逻辑原点）</summary>
        private CharacterRootMotion rootMotionData = new CharacterRootMotion();
 
        /// <summary>本次会话编辑过的招式。保存时只有它们会覆盖磁盘上的同名招式。</summary>
        private readonly HashSet<string> dirtyMoves = new HashSet<string>();
 
        // ---- Clip 列表 ----
        private readonly List<AnimationClip> clipLibrary = new List<AnimationClip>();
        private Vector2 clipListScroll;
        private bool showOnlyUnfinished;
 
        // ---- 编辑目标 ----
        private GameObject rigPrefab;
        private AnimationClip clip;
        private string moveId = "";
 
        // ---- 预览状态 ----
        private GameObject previewInstance;
        private int currentFrame = 1;
        private int totalFrames = 1;
        private bool playing;
        private double lastPlayTime;
 
        // ---- 当前编辑的数据 ----
        private MoveBoxData currentMove;
        private int selectedTrack = -1;
        private int copyFromIndex;
 
        // ---- 显示 ----
        private const int SampleRate = 60;
 
        /// <summary>
        /// 预览时角色的 Y 轴旋转。3D 角色的模型前向是局部 Z，而战斗平面的"前"是世界 X ——
        /// 旋转 90° 让两者对齐，角色侧对摄像机、拳头朝世界 +X 打出，
        /// 判定框才和拳头在同一平面上。
        /// </summary>
        private float previewRotationY = 90f;
 
        /// <summary>
        /// 判定框的视觉厚度。【仅用于显示，不进碰撞数据】。
        /// 本作是 2.5D 格斗（3D 模型 + 2D 玩法平面），碰撞判定是纯 2D 的。
        /// </summary>
        private float visualDepth = 0.4f;
 
        private BatchRootMotionBaker.ForwardAxis forwardAxis = BatchRootMotionBaker.ForwardAxis.Z;
 
        private static readonly Color HitColor = new Color(1f, 0.25f, 0.25f, 0.35f);
        private static readonly Color HurtColor = new Color(0.3f, 0.9f, 0.4f, 0.3f);
        private static readonly Color PushColor = new Color(0.4f, 0.6f, 1f, 0.25f);
 
        private static readonly Color StartupColor = new Color(0.95f, 0.75f, 0.2f, 0.35f);  // 黄=前摇
        private static readonly Color ActiveColor = new Color(0.95f, 0.3f, 0.3f, 0.4f);     // 红=判定
        private static readonly Color RecoveryColor = new Color(0.35f, 0.6f, 0.95f, 0.35f); // 蓝=后摇
        private static readonly Color InvulnColor = new Color(1f, 1f, 1f, 0.5f);            // 白=无敌
 
        private Vector2 scroll;
 
        [MenuItem("FG/Hitbox Editor")]
        private static void Open()
        {
            var w = GetWindow<HitboxEditor>("判定框编辑器");
            w.minSize = new Vector2(380, 640);
        }
 
        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += OnEditorUpdate;
            LoadAll(); // 开窗即加载，杜绝"忘了 Load 就 Save 导致覆盖"
        }
 
        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= OnEditorUpdate;
            DestroyPreview();
        }
 
        // ===================== 主面板 =====================
 
        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
 
            DrawHeader();
            EditorGUILayout.Space();
            DrawClipList();
 
            if (previewInstance != null)
            {
                EditorGUILayout.Space();
                DrawViewSettings();
                EditorGUILayout.Space();
                DrawTimeline();
                EditorGUILayout.Space();
                DrawFrameData();
                EditorGUILayout.Space();
                DrawTrackList();
            }
 
            EditorGUILayout.Space();
            DrawIO();
 
            EditorGUILayout.EndScrollView();
        }
 
        private void DrawHeader()
        {
            EditorGUILayout.LabelField("① 角色", EditorStyles.boldLabel);
 
            EditorGUI.BeginChangeCheck();
            characterId = EditorGUILayout.TextField("角色 ID", characterId);
            if (EditorGUI.EndChangeCheck() && characterId != loadedCharacterId)
            {
                if (dirtyMoves.Count > 0 &&
                    !EditorUtility.DisplayDialog("未保存的修改",
                        $"当前有 {dirtyMoves.Count} 个招式未保存，切换角色会丢弃它们。继续？",
                        "丢弃并切换", "取消"))
                {
                    characterId = loadedCharacterId;
                }
                else
                {
                    LoadAll();
                }
            }
 
            rigPrefab = (GameObject)EditorGUILayout.ObjectField(
                "角色 Prefab", rigPrefab, typeof(GameObject), false);
 
            int done = boxData.Moves.Count(m => m.Tracks.Count > 0);
            int motions = rootMotionData.Moves.Count;
            string dirtyTag = dirtyMoves.Count > 0 ? $"　● {dirtyMoves.Count} 未保存" : "";
 
            EditorGUILayout.HelpBox(
                $"手工数据: {done} 个招式有判定框{dirtyTag}\n" +
                $"位移数据: {motions} 个招式有位移（由 Batch Root Motion Baker 产出，本窗口只读）",
                dirtyMoves.Count > 0 ? MessageType.Warning : MessageType.Info);
        }
 
        private void DrawClipList()
        {
            EditorGUILayout.LabelField("② Clip 列表", EditorStyles.boldLabel);
 
            Rect dropArea = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, clipLibrary.Count == 0
                ? "拖入 fbx / 文件夹 / Clip（可多选）"
                : $"已载入 {clipLibrary.Count} 个 Clip —— 可继续拖入追加");
            HandleDragAndDrop(dropArea);
 
            if (clipLibrary.Count == 0) return;
 
            using (new EditorGUILayout.HorizontalScope())
            {
                showOnlyUnfinished = EditorGUILayout.ToggleLeft(
                    "只看未完成", showOnlyUnfinished, GUILayout.Width(90));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("清空列表", GUILayout.Width(70)))
                {
                    clipLibrary.Clear();
                    return;
                }
            }
 
            clipListScroll = EditorGUILayout.BeginScrollView(clipListScroll, GUILayout.Height(150));
            foreach (AnimationClip c in clipLibrary)
            {
                MoveBoxData data = boxData.Find(c.name);
                int tracks = data?.Tracks.Count ?? 0;
                bool finished = tracks > 0;
                bool hasMotion = rootMotionData.Find(c.name) != null;
 
                if (showOnlyUnfinished && finished) continue;
 
                bool isCurrent = clip == c;
                using (new EditorGUILayout.HorizontalScope(
                           isCurrent ? EditorStyles.helpBox : GUIStyle.none))
                {
                    // ○=未配框 ●=改过未存 ✓=已配框
                    string mark = !finished ? "○" : dirtyMoves.Contains(c.name) ? "●" : "✓";
                    GUI.color = !finished ? Color.gray
                        : dirtyMoves.Contains(c.name) ? Color.yellow : Color.green;
                    EditorGUILayout.LabelField(mark, GUILayout.Width(16));
                    GUI.color = Color.white;
 
                    if (GUILayout.Button(c.name, EditorStyles.label))
                        SelectClip(c);
 
                    // 有位移的招式标出来——它们必须先烘位移再画框
                    GUI.color = hasMotion ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 1f, 1f, 0.3f);
                    EditorGUILayout.LabelField(hasMotion ? "↔" : "·", GUILayout.Width(14));
                    GUI.color = Color.white;
 
                    EditorGUILayout.LabelField(finished ? $"{tracks} 框" : "—", GUILayout.Width(42));
                }
            }
            EditorGUILayout.EndScrollView();
 
            EditorGUILayout.LabelField("↔ = 已有位移数据", EditorStyles.miniLabel);
 
            if (rigPrefab == null)
                EditorGUILayout.HelpBox("请先指定角色 Prefab，再点列表里的 Clip 开始编辑。", MessageType.Warning);
        }
 
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
                clipLibrary.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
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
            if (!clipLibrary.Contains(c)) clipLibrary.Add(c);
        }
 
        private void SelectClip(AnimationClip c)
        {
            if (rigPrefab == null)
            {
                EditorUtility.DisplayDialog("缺少 Prefab", "请先指定角色 Prefab。", "好");
                return;
            }
            clip = c;
            SetupPreview();
        }
 
        private void DrawViewSettings()
        {
            EditorGUILayout.LabelField("③ 显示设置", EditorStyles.boldLabel);
 
            EditorGUI.BeginChangeCheck();
            previewRotationY = EditorGUILayout.Slider("角色朝向 (Y 旋转)", previewRotationY, 0f, 360f);
            if (EditorGUI.EndChangeCheck()) SampleToFrame();
 
            visualDepth = EditorGUILayout.Slider("判定框视觉厚度", visualDepth, 0.05f, 1.5f);
 
            if (GUILayout.Button("对齐战斗平面视角")) AlignSceneView();
 
            EditorGUILayout.HelpBox(
                "判定是【纯 2D】的：X=前后, Y=上下。厚度仅用于显示，不进碰撞数据。",
                MessageType.None);
        }
 
        private void AlignSceneView()
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
 
            Vector3 focus = LogicOriginAt(currentFrame) + new Vector3(0.3f, 1f, 0f);
            sv.LookAt(focus, Quaternion.Euler(0f, 0f, 0f), 2.5f);
            sv.orthographic = true;
            sv.Repaint();
        }
 
        // ===================== 时间轴 =====================
 
        private void DrawTimeline()
        {
            EditorGUILayout.LabelField($"④ 时间轴 —— {moveId}", EditorStyles.boldLabel);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀", GUILayout.Width(30))) StepFrame(-1);
                if (GUILayout.Button(playing ? "⏸" : "▶", GUILayout.Width(30)))
                {
                    playing = !playing;
                    lastPlayTime = EditorApplication.timeSinceStartup;
                }
                if (GUILayout.Button("▶", GUILayout.Width(30))) StepFrame(1);
 
                EditorGUI.BeginChangeCheck();
                currentFrame = EditorGUILayout.IntSlider(currentFrame, 1, totalFrames);
                if (EditorGUI.EndChangeCheck()) SampleToFrame();
 
                EditorGUILayout.LabelField($"/ {totalFrames}", GUILayout.Width(45));
            }
 
            Rect ruler = GUILayoutUtility.GetRect(0, 30, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(ruler, new Color(0.16f, 0.16f, 0.16f));
 
            // 相位着色：黄=前摇 红=判定 蓝=后摇。分割对不对一眼看出
            if (currentMove != null && currentMove.HasFrameSplit)
            {
                DrawPhaseBand(ruler, 1, currentMove.Startup, StartupColor);
                DrawPhaseBand(ruler, currentMove.Startup + 1,
                    currentMove.Startup + currentMove.Active, ActiveColor);
                DrawPhaseBand(ruler, currentMove.Startup + currentMove.Active + 1,
                    currentMove.TotalFrames, RecoveryColor);
            }
 
            // 无敌帧：顶部一条白带
            if (currentMove != null && currentMove.InvulnTo > 0)
            {
                var top = new Rect(ruler.x, ruler.y, ruler.width, 4f);
                DrawPhaseBand(top, currentMove.InvulnFrom, currentMove.InvulnTo, InvulnColor);
            }
 
            // 关键帧标记
            if (currentMove != null)
            {
                foreach (BoxTrack track in currentMove.Tracks)
                {
                    Color c = ColorOf(track.Kind);
                    c.a = 1f;
                    foreach (BoxKeyframe key in track.Keys)
                    {
                        float x = FrameToX(ruler, key.Frame);
                        EditorGUI.DrawRect(new Rect(x - 1.5f, ruler.y + 6, 3, ruler.height - 12), c);
                    }
                }
            }
 
            EditorGUI.DrawRect(
                new Rect(FrameToX(ruler, currentFrame) - 1, ruler.y, 2, ruler.height), Color.white);
        }
 
        private float FrameToX(Rect rect, int frame)
            => rect.x + rect.width * (frame - 1f) / Mathf.Max(1, totalFrames - 1);
 
        private void DrawPhaseBand(Rect rect, int from, int to, Color color)
        {
            if (to < from) return;
            float x0 = FrameToX(rect, Mathf.Max(1, from));
            float x1 = FrameToX(rect, Mathf.Min(totalFrames, to));
            EditorGUI.DrawRect(new Rect(x0, rect.y, Mathf.Max(2f, x1 - x0), rect.height), color);
        }
 
        // ===================== 帧分割 + 位移 =====================
 
        private void DrawFrameData()
        {
            EnsureCurrentMove();
 
            EditorGUILayout.LabelField("⑤ 帧分割 (Startup / Active / Recovery)", EditorStyles.boldLabel);
 
            // 自动推导：攻击招式的 Active 段 = 有 Hit 框的帧范围。
            // 你画完攻击框，帧分割就已经确定了——不需要再数一遍
            bool hasHit = currentMove.Tracks.Exists(t => t.Kind == BoxKind.Hit && t.Keys.Count > 0);
            using (new EditorGUI.DisabledScope(!hasHit))
            {
                if (GUILayout.Button(hasHit
                        ? "从攻击框自动推导（Active = 有 Hit 框的帧范围）"
                        : "无攻击框，无法自动推导（移动类招式请手动设置）"))
                {
                    DeriveFrameSplitFromHitboxes();
                }
            }
 
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("把当前帧设为", GUILayout.Width(80));
                if (GUILayout.Button($"Startup 末帧 ({currentFrame})")) SetStartupEnd(currentFrame);
                if (GUILayout.Button($"Active 末帧 ({currentFrame})")) SetActiveEnd(currentFrame);
            }
 
            EditorGUI.BeginChangeCheck();
            int startup = EditorGUILayout.IntField("Startup (前摇)", currentMove.Startup);
            int active = EditorGUILayout.IntField("Active (判定/腾空)", currentMove.Active);
            int recovery = EditorGUILayout.IntField("Recovery (后摇)", currentMove.Recovery);
            if (EditorGUI.EndChangeCheck())
            {
                currentMove.Startup = Mathf.Max(0, startup);
                currentMove.Active = Mathf.Max(0, active);
                currentMove.Recovery = Mathf.Max(0, recovery);
                MarkDirty();
            }
 
            int sum = currentMove.Startup + currentMove.Active + currentMove.Recovery;
            if (sum != totalFrames)
            {
                EditorGUILayout.HelpBox(
                    $"三段之和 {sum} ≠ 总帧数 {totalFrames}。\n" +
                    "帧数据是权威，动画服从它——不一致时运行期动画会被拉伸/压缩播放。",
                    MessageType.Warning);
                if (GUILayout.Button($"把差额 {totalFrames - sum} 补进 Recovery"))
                {
                    currentMove.Recovery = Mathf.Max(0, currentMove.Recovery + (totalFrames - sum));
                    MarkDirty();
                }
            }
 
            // 无敌帧
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("无敌帧", GUILayout.Width(45));
                EditorGUI.BeginChangeCheck();
                currentMove.InvulnFrom = EditorGUILayout.IntField(currentMove.InvulnFrom, GUILayout.Width(40));
                EditorGUILayout.LabelField("→", GUILayout.Width(15));
                currentMove.InvulnTo = EditorGUILayout.IntField(currentMove.InvulnTo, GUILayout.Width(40));
                if (EditorGUI.EndChangeCheck()) MarkDirty();
 
                if (GUILayout.Button("清除", GUILayout.Width(45)))
                {
                    currentMove.InvulnFrom = 0;
                    currentMove.InvulnTo = 0;
                    MarkDirty();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("0 = 无（升龙 / 后跃步用）", EditorStyles.miniLabel);
            }
 
            DrawRootMotionSection();
        }
 
        /// <summary>
        /// 位移区块。位移【存在另一个 JSON 里】，本编辑器只读它来算逻辑原点；
        /// "重烘"只是个便捷入口，实际写的是 rootmotion.json。
        /// </summary>
        private void DrawRootMotionSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("位移 (Root Motion) —— 存于 rootmotion.json", EditorStyles.boldLabel);
 
            MoveRootMotion rm = CurrentRootMotion;
 
            using (new EditorGUILayout.HorizontalScope())
            {
                forwardAxis = (BatchRootMotionBaker.ForwardAxis)
                    EditorGUILayout.EnumPopup("前进轴", forwardAxis, GUILayout.Width(170));
 
                if (GUILayout.Button("重烘当前招式")) RebakeCurrentMove();
            }
 
            if (rm?.Motion != null && rm.Motion.Length > 0)
            {
                Vector2 net = rm.AccumulatedTo(rm.Motion.Length);
                EditorGUILayout.LabelField(
                    $"已烘焙 {rm.Motion.Length} 帧　净位移 前后 {net.x:F3} / 上下 {net.y:F3}",
                    EditorStyles.miniLabel);
 
                if (rm.Motion.Length != totalFrames)
                {
                    EditorGUILayout.HelpBox(
                        $"位移 {rm.Motion.Length} 帧 ≠ Clip {totalFrames} 帧。动画改过？请重烘。",
                        MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "此招式无位移数据（原地招式，或尚未烘焙）。\n\n" +
                    "⚠ 若这招【带位移】，务必先烘焙再画判定框：\n" +
                    "判定框坐标相对【逻辑原点】，而逻辑原点是从位移累加出来的。" +
                    "未烘焙时逻辑原点恒为零点，你会对着漂移后的角色画框，" +
                    "坐标里混进漂移量，游戏里框会偏到角色前/后方（幽灵框）。",
                    MessageType.Info);
            }
        }
 
        /// <summary>
        /// 重烘当前招式 → 写入 rootmotion.json。
        /// 采样与降噪【复用 BatchRootMotionBaker 的实现】——同一逻辑写两遍必然走样。
        /// </summary>
        private void RebakeCurrentMove()
        {
            if (clip == null || rigPrefab == null)
            {
                EditorUtility.DisplayDialog("缺少资源", "需要 Clip 和角色 Prefab。", "好");
                return;
            }
 
            Vector2[] deltas = BatchRootMotionBaker.Sample(clip, rigPrefab, forwardAxis);
            bool hasMotion = BatchRootMotionBaker.Denoise(deltas, 0.005f);
 
            LoadRootMotion(); // 以磁盘为基底，避免覆盖其他招式
            rootMotionData.CharacterId = characterId;
            rootMotionData.ForwardAxis = forwardAxis.ToString();
 
            rootMotionData.Moves.RemoveAll(m => m.MoveId == moveId);
            if (hasMotion)
            {
                rootMotionData.Moves.Add(new MoveRootMotion
                {
                    MoveId = moveId,
                    Frames = deltas.Length,
                    Motion = deltas,
                });
            }
            rootMotionData.Moves.Sort((a, b) => string.CompareOrdinal(a.MoveId, b.MoveId));
 
            Directory.CreateDirectory(LibraryFolder);
            if (File.Exists(RootMotionPath))
                File.Copy(RootMotionPath, RootMotionPath + ".bak", overwrite: true);
            File.WriteAllText(RootMotionPath, JsonUtility.ToJson(rootMotionData, true));
            AssetDatabase.Refresh();
 
            SampleToFrame(); // 逻辑原点变了，重新摆位
            SceneView.RepaintAll();
 
            Debug.Log(hasMotion
                ? $"[HitboxEditor] {moveId} 位移已重烘（{deltas.Length} 帧）→ {RootMotionPath}"
                : $"[HitboxEditor] {moveId} 无位移（原地招式），已从位移库移除。" +
                  "若 Clip 的 Average Velocity 非零，检查前进轴是否选错。");
        }
 
        private void DeriveFrameSplitFromHitboxes()
        {
            int first = int.MaxValue, last = 0;
            foreach (BoxTrack t in currentMove.Tracks)
            {
                if (t.Kind != BoxKind.Hit || t.Keys.Count == 0) continue;
                first = Mathf.Min(first, t.FromFrame);
                last = Mathf.Max(last, t.ToFrame);
            }
            if (last == 0) return;
 
            currentMove.Startup = first - 1;
            currentMove.Active = last - first + 1;
            currentMove.Recovery = totalFrames - last;
            MarkDirty();
 
            Debug.Log($"[HitboxEditor] {moveId} 帧分割推导：" +
                      $"{currentMove.Startup} + {currentMove.Active} + {currentMove.Recovery} = {totalFrames}");
        }
 
        private void SetStartupEnd(int frame)
        {
            currentMove.Startup = frame;
            currentMove.Active = Mathf.Max(0, totalFrames - currentMove.Startup - currentMove.Recovery);
            MarkDirty();
        }
 
        private void SetActiveEnd(int frame)
        {
            int activeEnd = Mathf.Max(frame, currentMove.Startup);
            currentMove.Active = activeEnd - currentMove.Startup;
            currentMove.Recovery = totalFrames - activeEnd;
            MarkDirty();
        }
 
        // ===================== 判定框轨道 =====================
 
        private void DrawTrackList()
        {
            EditorGUILayout.LabelField("⑥ 判定框轨道", EditorStyles.boldLabel);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ 攻击框 Hit")) AddTrack(BoxKind.Hit);
                if (GUILayout.Button("+ 受击框 Hurt")) AddTrack(BoxKind.Hurt);
                if (GUILayout.Button("+ 推挡框 Push")) AddTrack(BoxKind.Push);
            }
 
            DrawCopyFrom();
 
            if (currentMove == null || currentMove.Tracks.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "还没有判定框。点上面的按钮添加轨道，或从其他招式复制。\n" +
                    "提示：只在关键帧画框（30 帧的招画 3~4 个即可），中间帧自动插值。",
                    MessageType.Info);
                return;
            }
 
            for (int i = 0; i < currentMove.Tracks.Count; i++)
            {
                BoxTrack track = currentMove.Tracks[i];
                bool selected = selectedTrack == i;
 
                using (new EditorGUILayout.VerticalScope(selected ? EditorStyles.helpBox : GUIStyle.none))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUI.color = ColorOf(track.Kind, 1f);
                        if (GUILayout.Button("■", GUILayout.Width(24))) selectedTrack = i;
                        GUI.color = Color.white;
 
                        if (GUILayout.Button(
                                $"{track.Kind}  帧 {track.FromFrame}-{track.ToFrame}  ({track.Keys.Count} 关键帧)",
                                EditorStyles.label))
                        {
                            selectedTrack = i;
                        }
 
                        if (GUILayout.Button("×", GUILayout.Width(24)))
                        {
                            currentMove.Tracks.RemoveAt(i);
                            selectedTrack = -1;
                            MarkDirty();
                            return;
                        }
                    }
 
                    if (!selected) continue;
 
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("生效帧", GUILayout.Width(45));
                        EditorGUI.BeginChangeCheck();
                        track.FromFrame = Mathf.Clamp(
                            EditorGUILayout.IntField(track.FromFrame, GUILayout.Width(40)), 1, totalFrames);
                        EditorGUILayout.LabelField("→", GUILayout.Width(15));
                        track.ToFrame = Mathf.Clamp(
                            EditorGUILayout.IntField(track.ToFrame, GUILayout.Width(40)),
                            track.FromFrame, totalFrames);
                        if (EditorGUI.EndChangeCheck()) MarkDirty();
 
                        if (GUILayout.Button($"在帧 {currentFrame} 打关键帧"))
                            AddKeyframe(track, currentFrame);
                    }
 
                    BoxKeyframe here = track.Keys.FirstOrDefault(k => k.Frame == currentFrame);
 
                    // 数值精调：Scene 里拖拽是粗调，这里补精确输入
                    if (track.TryEvaluate(currentFrame, out Box evaluated))
                    {
                        EditorGUI.BeginChangeCheck();
                        float x = EditorGUILayout.FloatField("X (前后)", evaluated.X);
                        float y = EditorGUILayout.FloatField("Y (上下)", evaluated.Y);
                        float w = EditorGUILayout.FloatField("W (宽)", evaluated.W);
                        float h = EditorGUILayout.FloatField("H (高)", evaluated.H);
                        if (EditorGUI.EndChangeCheck())
                        {
                            UpsertKeyframe(track, new BoxKeyframe
                            {
                                Frame = currentFrame,
                                X = x, Y = y,
                                W = Mathf.Max(0.02f, w),
                                H = Mathf.Max(0.02f, h),
                            });
                            SceneView.RepaintAll();
                        }
                    }
 
                    if (here != null && track.Keys.Count > 1)
                    {
                        if (GUILayout.Button($"删除帧 {currentFrame} 的关键帧"))
                        {
                            track.Keys.Remove(here);
                            MarkDirty();
                        }
                    }
                }
            }
        }
 
        /// <summary>
        /// 从库里其他招式复制 Hurt/Push。这两类框在招式间高度相似
        /// （尤其 Push 就是那根"身体柱子"，基本不变），复制起稿远快于从零画。
        /// </summary>
        private void DrawCopyFrom()
        {
            var sources = boxData.Moves
                .Where(m => m.Tracks.Count > 0 && m.MoveId != moveId)
                .ToList();
            if (sources.Count == 0) return;
 
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("从招式复制", GUILayout.Width(70));
                copyFromIndex = EditorGUILayout.Popup(
                    copyFromIndex,
                    sources.Select(m => $"{m.MoveId} ({m.Tracks.Count})").ToArray());
 
                if (GUILayout.Button("复制 Hurt/Push", GUILayout.Width(110)))
                {
                    EnsureCurrentMove();
                    MoveBoxData src = sources[Mathf.Clamp(copyFromIndex, 0, sources.Count - 1)];
                    foreach (BoxTrack t in src.Tracks.Where(t => t.Kind != BoxKind.Hit))
                        currentMove.Tracks.Add(CloneTrack(t, totalFrames));
                    MarkDirty();
                    SceneView.RepaintAll();
                }
            }
        }
 
        private static BoxTrack CloneTrack(BoxTrack src, int maxFrames)
        {
            var t = new BoxTrack
            {
                Kind = src.Kind,
                FromFrame = Mathf.Clamp(src.FromFrame, 1, maxFrames),
                ToFrame = Mathf.Clamp(src.ToFrame, 1, maxFrames),
            };
            foreach (BoxKeyframe k in src.Keys)
            {
                if (k.Frame > maxFrames) continue;
                t.Keys.Add(new BoxKeyframe { Frame = k.Frame, X = k.X, Y = k.Y, W = k.W, H = k.H });
            }
            if (t.Keys.Count == 0 && src.Keys.Count > 0)
            {
                BoxKeyframe f = src.Keys[0];
                t.Keys.Add(new BoxKeyframe { Frame = 1, X = f.X, Y = f.Y, W = f.W, H = f.H });
            }
            return t;
        }
 
        private void DrawIO()
        {
            EditorGUILayout.LabelField("⑦ 存取", EditorStyles.boldLabel);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = dirtyMoves.Count > 0;
                GUI.backgroundColor = dirtyMoves.Count > 0 ? Color.yellow : Color.white;
                if (GUILayout.Button($"保存判定框 ({dirtyMoves.Count})", GUILayout.Height(26)))
                    SaveBoxes();
                GUI.backgroundColor = Color.white;
                GUI.enabled = true;
 
                if (GUILayout.Button("重新加载", GUILayout.Height(26)))
                {
                    if (dirtyMoves.Count == 0 || EditorUtility.DisplayDialog("放弃修改",
                            $"{dirtyMoves.Count} 个招式未保存，重新加载会丢弃它们。", "丢弃", "取消"))
                    {
                        LoadAll();
                    }
                }
            }
 
            EditorGUILayout.HelpBox(
                "保存是【合并】：只有本次编辑过的招式会写入 boxes.json，其他招式原样保留，" +
                "覆盖前自动备份 .bak。\n位移在另一个文件（rootmotion.json）里，本按钮不碰它。",
                MessageType.None);
        }
 
        // ===================== Scene 视图 =====================
 
        private void OnSceneGUI(SceneView sceneView)
        {
            if (previewInstance == null || currentMove == null) return;
 
            // previewInstance 就摆在逻辑原点上（见 SampleToFrame）
            Vector3 origin = previewInstance.transform.position;
 
            DrawOriginMarker(origin);
            DrawMotionPath();
 
            for (int i = 0; i < currentMove.Tracks.Count; i++)
            {
                BoxTrack track = currentMove.Tracks[i];
                if (!track.TryEvaluate(currentFrame, out Box box)) continue;
 
                bool isSelected = selectedTrack == i;
                bool isKeyframe = track.Keys.Any(k => k.Frame == currentFrame);
 
                DrawBox(origin, box, ColorOf(track.Kind), isSelected, isKeyframe);
 
                if (isSelected) EditBox(origin, track, box);
            }
 
            Handles.BeginGUI();
            GUI.Label(new Rect(10, 10, 480, 40),
                $"帧 {currentFrame} / {totalFrames}   {moveId}   " +
                $"逻辑原点 ({origin.x:F3}, {origin.y:F3})",
                EditorStyles.whiteLargeLabel);
            Handles.EndGUI();
        }
 
        /// <summary>
        /// 逻辑原点标记（角色脚下的黄十字）。判定框的坐标全部相对它——
        /// 让它可见能省掉大量困惑：你能直接看到角色的逻辑位置往前走了多少。
        /// </summary>
        private static void DrawOriginMarker(Vector3 origin)
        {
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);
            const float s = 0.12f;
            Handles.DrawLine(origin + Vector3.left * s, origin + Vector3.right * s);
            Handles.DrawLine(origin, origin + Vector3.up * s * 1.5f);
            Handles.DrawWireDisc(origin, Vector3.forward, s * 0.35f);
        }
 
        /// <summary>整段招式的逻辑位移轨迹，一眼看出这招往前推了多少。</summary>
        private void DrawMotionPath()
        {
            MoveRootMotion rm = CurrentRootMotion;
            if (rm?.Motion == null || rm.Motion.Length < 2) return;
 
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.35f);
            Vector3 prev = Vector3.zero;
            Vector2 accum = Vector2.zero;
 
            foreach (Vector2 d in rm.Motion)
            {
                accum += d;
                var p = new Vector3(accum.x, accum.y, 0f);
                Handles.DrawLine(prev, p);
                prev = p;
            }
        }
 
        private void DrawBox(Vector3 origin, Box box, Color color, bool selected, bool isKeyframe)
        {
            Vector3 center = origin + new Vector3(box.X, box.Y, 0f);
            var size = new Vector3(box.W, box.H, visualDepth);
 
            Handles.color = color;
            Handles.DrawSolidRectangleWithOutline(
                FacePoints(center, size),
                color,
                selected ? Color.white : new Color(color.r, color.g, color.b, 0.9f));
 
            Handles.color = selected ? Color.white : new Color(color.r, color.g, color.b, 0.7f);
            Handles.DrawWireCube(center, size);
 
            if (!isKeyframe) return;
 
            Handles.color = Color.white;
            const float s = 0.06f;
            Vector3 tl = center + new Vector3(-size.x * 0.5f, size.y * 0.5f, 0f);
            Vector3 br = center + new Vector3(size.x * 0.5f, -size.y * 0.5f, 0f);
            Handles.DrawLine(tl, tl + Vector3.right * s);
            Handles.DrawLine(tl, tl + Vector3.down * s);
            Handles.DrawLine(br, br + Vector3.left * s);
            Handles.DrawLine(br, br + Vector3.up * s);
        }
 
        private static Vector3[] FacePoints(Vector3 center, Vector3 size)
        {
            float hw = size.x * 0.5f, hh = size.y * 0.5f;
            return new[]
            {
                center + new Vector3(-hw, -hh, 0f),
                center + new Vector3(-hw,  hh, 0f),
                center + new Vector3( hw,  hh, 0f),
                center + new Vector3( hw, -hh, 0f),
            };
        }
 
        /// <summary>
        /// 拖拽编辑。手柄【约束在战斗平面（XY）】——用 Slider2D 而非 FreeMoveHandle，
        /// 否则在 3D 视图里拖拽会引入 Z 轴漂移，而 Z 在判定数据里根本不存在。
        /// 任何改动自动在当前帧打关键帧——所见即所得。
        /// </summary>
        private void EditBox(Vector3 origin, BoxTrack track, Box box)
        {
            EditorGUI.BeginChangeCheck();
 
            Vector3 center = origin + new Vector3(box.X, box.Y, 0f);
            float handleSize = HandleUtility.GetHandleSize(center) * 0.09f;
 
            Handles.color = Color.white;
            Vector3 newCenter = Handles.Slider2D(
                center, Vector3.forward, Vector3.right, Vector3.up,
                handleSize, Handles.DotHandleCap, 0f);
 
            Vector3 rightHandle = center + new Vector3(box.W * 0.5f, 0f, 0f);
            Vector3 topHandle = center + new Vector3(0f, box.H * 0.5f, 0f);
 
            Handles.color = Color.yellow;
            Vector3 newRight = Handles.Slider2D(
                rightHandle, Vector3.forward, Vector3.right, Vector3.up,
                handleSize * 0.75f, Handles.DotHandleCap, 0f);
            Vector3 newTop = Handles.Slider2D(
                topHandle, Vector3.forward, Vector3.right, Vector3.up,
                handleSize * 0.75f, Handles.DotHandleCap, 0f);
 
            if (!EditorGUI.EndChangeCheck()) return;
 
            UpsertKeyframe(track, new BoxKeyframe
            {
                Frame = currentFrame,
                X = newCenter.x - origin.x,   // 相对逻辑原点 —— 与游戏里的坐标基准一致
                Y = newCenter.y - origin.y,
                W = Mathf.Max(0.02f, (newRight.x - newCenter.x) * 2f),
                H = Mathf.Max(0.02f, (newTop.y - newCenter.y) * 2f),
            });
            Repaint();
        }
 
        // ===================== 数据操作 =====================
 
        private void MarkDirty()
        {
            if (!string.IsNullOrEmpty(moveId)) dirtyMoves.Add(moveId);
        }
 
        private void AddTrack(BoxKind kind)
        {
            EnsureCurrentMove();
 
            var track = new BoxTrack
            {
                Kind = kind,
                FromFrame = currentFrame,
                ToFrame = Mathf.Min(currentFrame + 2, totalFrames),
            };
            track.Keys.Add(new BoxKeyframe
            {
                Frame = currentFrame,
                X = kind == BoxKind.Hit ? 0.5f : 0f,
                Y = kind == BoxKind.Push ? 0.9f : 1.0f,
                W = kind == BoxKind.Hit ? 0.6f : 0.5f,
                H = kind == BoxKind.Push ? 1.8f : 0.4f,
            });
 
            currentMove.Tracks.Add(track);
            selectedTrack = currentMove.Tracks.Count - 1;
            MarkDirty();
            SceneView.RepaintAll();
        }
 
        private void AddKeyframe(BoxTrack track, int frame)
        {
            if (!track.TryEvaluate(frame, out Box box))
                box = track.Keys.Count > 0 ? track.Keys[0].ToBox() : new Box(0.5f, 1f, 0.5f, 0.4f);
 
            UpsertKeyframe(track, new BoxKeyframe
            {
                Frame = frame, X = box.X, Y = box.Y, W = box.W, H = box.H,
            });
        }
 
        private void UpsertKeyframe(BoxTrack track, BoxKeyframe key)
        {
            BoxKeyframe existing = track.Keys.FirstOrDefault(k => k.Frame == key.Frame);
            if (existing != null)
            {
                existing.X = key.X; existing.Y = key.Y;
                existing.W = key.W; existing.H = key.H;
            }
            else
            {
                track.Keys.Add(key);
                track.Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            }
 
            // 关键帧超出生效范围时自动扩展——避免"画了框却不生效"的困惑
            if (key.Frame < track.FromFrame) track.FromFrame = key.Frame;
            if (key.Frame > track.ToFrame) track.ToFrame = key.Frame;
 
            MarkDirty();
        }
 
        private void EnsureCurrentMove()
        {
            if (currentMove != null && currentMove.MoveId == moveId) return;
 
            currentMove = boxData.Find(moveId);
            if (currentMove == null)
            {
                currentMove = new MoveBoxData { MoveId = moveId, TotalFrames = totalFrames };
                boxData.Moves.Add(currentMove);
            }
            currentMove.TotalFrames = totalFrames;
        }
 
        /// <summary>当前招式的位移数据。null = 原地招式（逻辑原点恒为零点）。</summary>
        private MoveRootMotion CurrentRootMotion =>
            string.IsNullOrEmpty(moveId) ? null : rootMotionData.Find(moveId);
 
        // ===================== 加载与保存 =====================
 
        private void LoadAll()
        {
            LoadBoxes();
            LoadRootMotion();
 
            loadedCharacterId = characterId;
            dirtyMoves.Clear();
            currentMove = null;
            selectedTrack = -1;
 
            if (!string.IsNullOrEmpty(moveId)) EnsureCurrentMove();
            SampleToFrame();
            SceneView.RepaintAll();
            Repaint();
        }
 
        private void LoadBoxes()
        {
            boxData = new CharacterBoxData { CharacterId = characterId };
            if (!File.Exists(BoxPath)) return;
 
            try
            {
                var loaded = JsonUtility.FromJson<CharacterBoxData>(File.ReadAllText(BoxPath));
                if (loaded != null) boxData = Migrate(loaded);
            }
            catch (System.Exception e)
            {
                // 解析失败绝不能静默——否则接下来的保存会用空数据覆盖掉整个文件
                EditorUtility.DisplayDialog("加载失败",
                    $"boxes.json 解析出错，为避免覆盖，编辑器不会加载：\n{e.Message}\n\n{BoxPath}", "好");
            }
        }
 
        private void LoadRootMotion()
        {
            rootMotionData = new CharacterRootMotion { CharacterId = characterId };
            if (!File.Exists(RootMotionPath)) return;
 
            try
            {
                var loaded = JsonUtility.FromJson<CharacterRootMotion>(File.ReadAllText(RootMotionPath));
                if (loaded != null) rootMotionData = loaded;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HitboxEditor] rootmotion.json 解析失败：{e.Message}");
            }
        }
 
        /// <summary>版本迁移。v1/v2 → v3 是纯增量+移除（RootMotion 搬走），无需转换。</summary>
        private static CharacterBoxData Migrate(CharacterBoxData data)
        {
            int from = data.Version; // 旧文件无此字段 → 0，视为 v1
 
            if (from > 0 && from < CharacterBoxData.CurrentVersion)
            {
                Debug.Log($"[HitboxEditor] 检测到 v{from} 数据，已按 v{CharacterBoxData.CurrentVersion} 读入。" +
                          "判定框原样保留；位移已搬到 rootmotion.json，" +
                          "请用 FG/Batch Root Motion Baker 重新烘焙。");
            }
 
            if (from > CharacterBoxData.CurrentVersion)
            {
                EditorUtility.DisplayDialog("版本过新",
                    $"文件是 v{from}，编辑器只认到 v{CharacterBoxData.CurrentVersion}。" +
                    "继续编辑可能丢失新字段，请先更新工具。", "好");
            }
 
            data.Version = CharacterBoxData.CurrentVersion;
            return data;
        }
 
        /// <summary>
        /// 合并式保存 + 自动备份。
        ///   ① 合并：只用本次编辑过的招式覆盖磁盘上的同名项，其他原样保留
        ///      （全量覆盖的经典事故：只编了 1 个招式就保存，磁盘上另外 20 个全没了）
        ///   ② 备份：覆盖前先复制 .bak。判定框是几小时的手工劳动，值得这一行
        /// </summary>
        private void SaveBoxes()
        {
            Directory.CreateDirectory(LibraryFolder);
 
            var merged = new CharacterBoxData
            {
                CharacterId = characterId,
                Version = CharacterBoxData.CurrentVersion,
            };
 
            if (File.Exists(BoxPath))
            {
                File.Copy(BoxPath, BoxPath + ".bak", overwrite: true);
 
                try
                {
                    var onDisk = JsonUtility.FromJson<CharacterBoxData>(File.ReadAllText(BoxPath));
                    if (onDisk?.Moves != null) merged.Moves.AddRange(Migrate(onDisk).Moves);
                }
                catch (System.Exception e)
                {
                    if (!EditorUtility.DisplayDialog("磁盘文件损坏",
                            $"无法解析磁盘上的 boxes.json：{e.Message}\n\n" +
                            "继续保存会丢失文件里的其他招式（已备份到 .bak）。是否继续？",
                            "继续保存", "取消"))
                    {
                        return;
                    }
                }
            }
 
            foreach (string id in dirtyMoves)
            {
                MoveBoxData mine = boxData.Find(id);
                if (mine == null) continue;
 
                merged.Moves.RemoveAll(m => m.MoveId == id);
                merged.Moves.Add(mine);
            }
 
            merged.Moves.Sort((a, b) => string.CompareOrdinal(a.MoveId, b.MoveId));
            File.WriteAllText(BoxPath, JsonUtility.ToJson(merged, true));
            AssetDatabase.Refresh();
 
            int saved = dirtyMoves.Count;
            boxData = merged;
            currentMove = null;
            dirtyMoves.Clear();
            if (!string.IsNullOrEmpty(moveId)) EnsureCurrentMove();
 
            Debug.Log($"[HitboxEditor] 已合并保存 {saved} 个招式，库中共 {merged.Moves.Count} 个 → {BoxPath}");
        }
 
        // ===================== 预览控制 =====================
 
        private void SetupPreview()
        {
            DestroyPreview();
            if (rigPrefab == null || clip == null) return;
 
            previewInstance = Instantiate(rigPrefab);
            previewInstance.name = "[HitboxEditor Preview]";
            previewInstance.hideFlags = HideFlags.DontSave;
 
            // 与 Baker 保持一致：预览看到的姿态 == 烘焙时采样的姿态。
            // （预览其实不依赖这个开关——SampleToFrame 会把 position 覆盖成逻辑原点——
            //   但保持一致能杜绝"编辑器里好好的、烘出来是 0"这类困惑。）
            var animator = previewInstance.GetComponent<Animator>()
                           ?? previewInstance.GetComponentInChildren<Animator>();
            if (animator != null) animator.applyRootMotion = true;
 
            totalFrames = Mathf.Max(1, Mathf.RoundToInt(clip.length * SampleRate));
            currentFrame = 1;
            moveId = clip.name;
            selectedTrack = -1;
            currentMove = null;
 
            EnsureCurrentMove();
            SampleToFrame();
            AlignSceneView();
        }
 
        private void DestroyPreview()
        {
            if (previewInstance != null) DestroyImmediate(previewInstance);
            previewInstance = null;
        }
 
        /// <summary>
        /// 当前帧的【逻辑原点】= 游戏里 FighterState.Position 在这一帧的值。
        ///
        /// 帧语义：Motion[i] = pose((i+1)/60) - pose(i/60)，
        /// 所以逻辑第 F 帧的位置 = sum(Motion[0..F-1]) = pose(F/60) - pose(0)。
        ///
        /// 判定框的坐标【永远相对逻辑原点】。预览若把角色钉在世界零点，
        /// 你画的框会把动画漂移一起吃进去 → 游戏里偏成"幽灵框"。
        /// </summary>
        private Vector3 LogicOriginAt(int frame)
        {
            MoveRootMotion rm = CurrentRootMotion;
            if (rm == null) return Vector3.zero;
 
            Vector2 accum = rm.AccumulatedTo(frame);
            // 逻辑空间 (前, 上) → 战斗平面世界空间 (X, Y)，与 FighterView 的映射一致
            return new Vector3(accum.x, accum.y, 0f);
        }
 
        /// <summary>
        /// 把动画采样到当前帧，并把角色摆到【这一帧的逻辑位置】。
        ///
        /// 采样点是 currentFrame/60 —— 与帧语义严格对应（逻辑第 F 帧 ⇔ pose(F/60)）。
        /// 写成 (F-1)/60 会让 pose 比逻辑原点慢一帧，判定框差一帧的位移。
        /// </summary>
        private void SampleToFrame()
        {
            if (previewInstance == null || clip == null) return;
 
            clip.SampleAnimation(previewInstance, currentFrame / (float)SampleRate);
 
            // 钉回逻辑原点（不是世界零点），同时重新施加预览旋转
            // ——SampleAnimation 会把根 transform 覆盖成动画里的值
            previewInstance.transform.SetPositionAndRotation(
                LogicOriginAt(currentFrame),
                Quaternion.Euler(0f, previewRotationY, 0f));
 
            SceneView.RepaintAll();
        }
 
        private void StepFrame(int delta)
        {
            currentFrame = Mathf.Clamp(currentFrame + delta, 1, totalFrames);
            SampleToFrame();
        }
 
        private void OnEditorUpdate()
        {
            if (!playing || previewInstance == null) return;
 
            double now = EditorApplication.timeSinceStartup;
            if (now - lastPlayTime < 1.0 / SampleRate) return;
 
            lastPlayTime = now;
            currentFrame = currentFrame >= totalFrames ? 1 : currentFrame + 1;
            SampleToFrame();
            Repaint();
        }
 
        private static Color ColorOf(BoxKind kind, float alpha = -1f)
        {
            Color c = kind == BoxKind.Hit ? HitColor
                : kind == BoxKind.Hurt ? HurtColor
                : PushColor;
            if (alpha >= 0f) c.a = alpha;
            return c;
        }
    }
}