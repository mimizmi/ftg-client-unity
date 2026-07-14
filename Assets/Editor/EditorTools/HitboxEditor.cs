using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Domain.Infrastructure.Battle;

namespace Editor.EditorTools
{
    /// <summary>
    /// 判定框可视化编辑器。判定框【必须】看着动画画——
    /// 你没法凭空想出"拳头第 6 帧伸到了 (0.55, 1.1)"，手写坐标根本做不准。
    ///
    /// 【角色库模式】编辑器围绕"一个角色一个 JSON 库"工作，而不是"一个 clip 一个文件"：
    ///   · 打开窗口 → 按角色 ID 自动加载已有库（Assets/Resources/BoxData/{id}_boxes.json）
    ///   · 拖入整个 fbx / 文件夹 → Clip 列表，点一下切换编辑对象，数据全程累加在库里
    ///   · 保存 → 与磁盘上的库【合并】：本次编辑过的招式覆盖，未碰过的原样保留
    ///
    /// 合并式保存解决的是一个真实的数据丢失场景：全量覆盖式保存下，
    /// 打开窗口只编了 1 个招式就保存，会把磁盘上已有的另外 20 个招式全部清掉。
    ///
    /// 形状用矩形 AABB：格斗游戏的绝对主流（街霸/GG/KOF 全是），
    /// 且天然定点数友好——AABB 只需四次整数比较，跨语言逐位一致；
    /// OBB 要算投影轴、胶囊要开平方，浮点误差会让 Unity 与 Go 的判定对不上。
    /// </summary>
    public sealed class HitboxEditor : EditorWindow
    {
        // ---- 角色库 ----
        private const string LibraryFolder = "Assets/Resources/BoxData";
        private string characterId = "Frank";
        private string loadedCharacterId; // 已加载的库对应的角色 ID，用于检测切换
        private CharacterBoxData characterData = new CharacterBoxData();
 
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
 
        // ---- 数据 ----
        private MoveBoxData currentMove;
        private int selectedTrack = -1;
 
        // ---- 显示 ----
        private const int SampleRate = 60;
 
        /// <summary>
        /// 预览时角色的 Y 轴旋转。3D 角色的模型前向是局部 Z，
        /// 而战斗平面的"前"是世界 X —— 旋转 90° 让两者对齐，
        /// 这样角色侧对摄像机、拳头朝世界 +X 打出，判定框才和拳头在同一平面上。
        /// </summary>
        private float previewRotationY = 90f;
 
        /// <summary>
        /// 判定框的视觉厚度。【仅用于显示，不进碰撞数据】。
        /// 本作是 2.5D 格斗（3D 模型 + 2D 玩法平面），碰撞判定是纯 2D 的。
        /// </summary>
        private float visualDepth = 0.4f;
 
        private static readonly Color HitColor = new Color(1f, 0.25f, 0.25f, 0.35f);
        private static readonly Color HurtColor = new Color(0.3f, 0.9f, 0.4f, 0.3f);
        private static readonly Color PushColor = new Color(0.4f, 0.6f, 1f, 0.25f);
        
        private static readonly Color StartupColor  = new Color(0.95f, 0.75f, 0.2f, 0.35f); // 黄=前摇
        private static readonly Color ActiveColor   = new Color(0.95f, 0.3f, 0.3f, 0.4f);   // 红=判定
        private static readonly Color RecoveryColor = new Color(0.35f, 0.6f, 0.95f, 0.35f); // 蓝=后摇
        private static readonly Color InvulnColor   = new Color(1f, 1f, 1f, 0.5f);          // 白=无敌
 
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
            LoadLibrary(); // 开窗即加载，杜绝"忘了 Load 就 Save 导致覆盖"
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
 
            DrawLibraryHeader();
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
 
        private void DrawLibraryHeader()
        {
            EditorGUILayout.LabelField("① 角色库", EditorStyles.boldLabel);
 
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
                    LoadLibrary();
                }
            }
 
            rigPrefab = (GameObject)EditorGUILayout.ObjectField(
                "角色 Prefab", rigPrefab, typeof(GameObject), false);
 
            int done = characterData.Moves.Count(m => m.Tracks.Count > 0);
            string dirtyTag = dirtyMoves.Count > 0 ? $"  ● {dirtyMoves.Count} 未保存" : "";
            EditorGUILayout.HelpBox(
                $"库: {LibraryPath}\n已有 {done} 个招式的判定框{dirtyTag}",
                dirtyMoves.Count > 0 ? MessageType.Warning : MessageType.Info);
        }
 
        private string LibraryPath => $"{LibraryFolder}/{characterId}_boxes.json";
 
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
                MoveBoxData data = characterData.Find(c.name);
                int tracks = data?.Tracks.Count ?? 0;
                bool finished = tracks > 0;
 
                if (showOnlyUnfinished && finished) continue;
 
                bool isCurrent = clip == c;
                using (new EditorGUILayout.HorizontalScope(
                           isCurrent ? EditorStyles.helpBox : GUIStyle.none))
                {
                    // 状态标记：✓=已配框, ●=本次改过未存, ○=未配
                    string mark = !finished ? "○"
                        : dirtyMoves.Contains(c.name) ? "●" : "✓";
                    GUI.color = !finished ? Color.gray
                        : dirtyMoves.Contains(c.name) ? Color.yellow : Color.green;
                    EditorGUILayout.LabelField(mark, GUILayout.Width(16));
                    GUI.color = Color.white;
 
                    if (GUILayout.Button(c.name, EditorStyles.label))
                        SelectClip(c);
 
                    EditorGUILayout.LabelField(
                        finished ? $"{tracks} 框" : "—",
                        GUILayout.Width(45));
                }
            }
            EditorGUILayout.EndScrollView();
 
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
 
        /// <summary>从拖入对象收集 Clip：直接的 clip、fbx 里的子 clip、文件夹里递归找。</summary>
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
 
            // ---- 相位着色：黄=前摇 红=判定 蓝=后摇。一眼看出帧分割对不对 ----
            if (currentMove != null && currentMove.HasFrameSplit)
            {
                DrawPhaseBand(ruler, 1, currentMove.Startup, StartupColor);
                DrawPhaseBand(ruler, currentMove.Startup + 1,
                    currentMove.Startup + currentMove.Active, ActiveColor);
                DrawPhaseBand(ruler, currentMove.Startup + currentMove.Active + 1,
                    currentMove.TotalFrames, RecoveryColor);
            }
 
            // ---- 无敌帧：顶部一条白带 ----
            if (currentMove != null && currentMove.InvulnTo > 0)
            {
                Rect top = new Rect(ruler.x, ruler.y, ruler.width, 4f);
                DrawPhaseBand(top, currentMove.InvulnFrom, currentMove.InvulnTo, InvulnColor);
            }
 
            // ---- 关键帧标记 ----
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
 
            // ---- 当前帧游标 ----
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
 
        private void DrawTrackList()
        {
            EditorGUILayout.LabelField("⑤ 判定框轨道", EditorStyles.boldLabel);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ 攻击框 Hit")) AddTrack(BoxKind.Hit);
                if (GUILayout.Button("+ 受击框 Hurt")) AddTrack(BoxKind.Hurt);
                if (GUILayout.Button("+ 推挡框 Push")) AddTrack(BoxKind.Push);
            }
 
            // 从已完成的招式复制框——Hurt/Push 在招式间高度相似，
            // 从相近招式复制再微调，比每次从零画快得多
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
                            EditorGUILayout.IntField(track.ToFrame, GUILayout.Width(40)), track.FromFrame, totalFrames);
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
        
        
        private void DrawFrameData()
        {
            EnsureCurrentMove();
 
            EditorGUILayout.LabelField("⑤ 帧分割 (Startup / Active / Recovery)", EditorStyles.boldLabel);
 
            // ---- 自动推导：攻击招式的 Active 段 = 有 Hit 框的帧范围 ----
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
 
            // ---- 手动设置：把当前帧设为分割点 ----
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("把当前帧设为", GUILayout.Width(80));
                if (GUILayout.Button($"Startup 末帧 ({currentFrame})"))
                    SetStartupEnd(currentFrame);
                if (GUILayout.Button($"Active 末帧 ({currentFrame})"))
                    SetActiveEnd(currentFrame);
            }
 
            // ---- 数值精调 ----
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
 
            // ---- 无敌帧（升龙、后跃步）----
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
 
            // ---- 位移烘焙 ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("位移 (Root Motion)", EditorStyles.boldLabel);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                forwardAxis = (ForwardAxis)EditorGUILayout.EnumPopup("前进轴", forwardAxis, GUILayout.Width(180));
 
                if (GUILayout.Button("从当前 Clip 烘焙位移"))
                    BakeRootMotion();
 
                if (currentMove.RootMotion != null && currentMove.RootMotion.Length > 0
                    && GUILayout.Button("清除", GUILayout.Width(45)))
                {
                    currentMove.RootMotion = null;
                    MarkDirty();
                }
            }
 
            if (currentMove.RootMotion != null && currentMove.RootMotion.Length > 0)
            {
                Vector2 net = Vector2.zero;
                foreach (Vector2 d in currentMove.RootMotion) net += d;
                EditorGUILayout.LabelField(
                    $"已烘焙 {currentMove.RootMotion.Length} 帧　净位移 前后 {net.x:F3} / 上下 {net.y:F3}",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "未烘焙位移。走路/冲刺/跳跃靠它移动——位移就在动画里，" +
                    "凭空写速度必然与动画对不上，导致脚下打滑。\n" +
                    "3D 角色前进轴通常是 Z（看 Clip 的 Average Velocity 哪一维非零）。",
                    MessageType.Info);
            }
        }
 
        /// <summary>
        /// 从攻击框推导帧分割：Active = 有 Hit 框的帧范围。
        /// 你已经看着动画把攻击框画在了拳头伸出的那几帧上——那几帧就是判定帧，
        /// 分割点因此是免费的，不需要再数一遍。
        /// </summary>
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
            // 保持总帧数不变：Active 吸收变化
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
 
        // ---- 位移烘焙（与 BatchRootMotionBaker 同一套采样逻辑）----
 
        public enum ForwardAxis { Z, X }
        private ForwardAxis forwardAxis = ForwardAxis.Z;
 
        /// <summary>
        /// 以 60Hz 采样当前 clip 的根位移，直接写进 JSON。
        /// 注意：Humanoid 的根运动不写进 transform.position，只经 Animator.deltaPosition
        /// 输出——本编辑器用的 Prefab 实例带 Animator，走 Transform 采样即可覆盖 Generic；
        /// 若烘出全 0 且 Clip 的 Average Velocity 非零，多半是前进轴选错了。
        /// </summary>
        private void BakeRootMotion()
        {
            if (clip == null || rigPrefab == null) return;
 
            GameObject sampler = Instantiate(rigPrefab);
            try
            {
                sampler.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var deltas = new Vector2[totalFrames];
 
                clip.SampleAnimation(sampler, 0f);
                Vector3 prev = sampler.transform.position;
 
                for (int f = 1; f <= totalFrames; f++)
                {
                    clip.SampleAnimation(sampler, (f - 1) / (float)SampleRate);
                    Vector3 pos = sampler.transform.position;
                    float horizontal = forwardAxis == ForwardAxis.Z
                        ? pos.z - prev.z
                        : pos.x - prev.x;
                    deltas[f - 1] = new Vector2(horizontal, pos.y - prev.y);
                    prev = pos;
                }
 
                // 按【整轴累积位移】降噪：噪声不累积，真位移会累积。
                // 不能用固定阈值砍单帧——小位移动画（轻踢的重心前压）会被整段误杀成 0
                float netX = 0f, netY = 0f;
                foreach (Vector2 d in deltas) { netX += d.x; netY += d.y; }
 
                const float noiseThreshold = 0.005f;
                bool keepX = Mathf.Abs(netX) >= noiseThreshold;
                bool keepY = Mathf.Abs(netY) >= noiseThreshold;
 
                for (int i = 0; i < deltas.Length; i++)
                    deltas[i] = new Vector2(keepX ? deltas[i].x : 0f, keepY ? deltas[i].y : 0f);
 
                currentMove.RootMotion = deltas;
                MarkDirty();
 
                Debug.Log($"[HitboxEditor] {moveId} 位移烘焙完成：{totalFrames} 帧，" +
                          $"净位移 前后 {netX:F4} / 上下 {netY:F4}" +
                          (!keepX && !keepY ? "　⚠ 两轴均为零：检查前进轴是否选错（看 Clip 的 Average Velocity）" : ""));
            }
            finally
            {
                DestroyImmediate(sampler);
            }
        }
 
        private int copyFromIndex;
 
        /// <summary>从库里其他招式复制判定框。Hurt/Push 在招式间高度相似，复制起稿远快于从零画。</summary>
        private void DrawCopyFrom()
        {
            var sources = characterData.Moves
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
 
        /// <summary>深拷贝轨道，并把帧号钳制到目标招式的长度内（源招式可能更长）。</summary>
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
            EditorGUILayout.LabelField("⑥ 存取", EditorStyles.boldLabel);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = dirtyMoves.Count > 0;
                GUI.backgroundColor = dirtyMoves.Count > 0 ? Color.yellow : Color.white;
                if (GUILayout.Button($"保存到库 ({dirtyMoves.Count})", GUILayout.Height(26)))
                    SaveLibrary();
                GUI.backgroundColor = Color.white;
                GUI.enabled = true;
 
                if (GUILayout.Button("重新加载库", GUILayout.Height(26)))
                {
                    if (dirtyMoves.Count == 0 || EditorUtility.DisplayDialog("放弃修改",
                            $"{dirtyMoves.Count} 个招式未保存，重新加载会丢弃它们。", "丢弃", "取消"))
                    {
                        LoadLibrary();
                    }
                }
            }
 
            EditorGUILayout.HelpBox(
                "保存是【合并】而非覆盖：只有本次编辑过的招式会写入，\n" +
                "磁盘上其他招式原样保留——多人协作/多次会话都不会互相覆盖。",
                MessageType.None);
        }
 
        // ===================== Scene 视图 =====================
 
        private void OnSceneGUI(SceneView sceneView)
        {
            if (previewInstance == null || currentMove == null) return;
 
            // previewInstance 现在就摆在逻辑原点上，所以这行不用改
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
            GUI.Label(new Rect(10, 10, 460, 40),
                $"帧 {currentFrame} / {totalFrames}   {moveId}   " +
                $"逻辑原点 ({origin.x:F3}, {origin.y:F3})",
                EditorStyles.whiteLargeLabel);
            Handles.EndGUI();
        }
        
        /// <summary>
        /// 画出逻辑原点（角色脚下的十字）。判定框的坐标全部相对它，
        /// 让它可见能省掉大量困惑——你能直接看到"角色的逻辑位置"往前走了多少。
        /// </summary>
        private static void DrawOriginMarker(Vector3 origin)
        {
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);
            const float s = 0.12f;
            Handles.DrawLine(origin + Vector3.left * s, origin + Vector3.right * s);
            Handles.DrawLine(origin, origin + Vector3.up * s * 1.5f);
            Handles.DrawWireDisc(origin, Vector3.forward, s * 0.35f);
        }
 
        /// <summary>画出整段招式的逻辑位移轨迹，一眼看出这招往前推了多少。</summary>
        private void DrawMotionPath()
        {
            Vector2[] rm = currentMove?.RootMotion;
            if (rm == null || rm.Length < 2) return;
 
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.35f);
            Vector3 prev = Vector3.zero;
            Vector2 accum = Vector2.zero;
 
            for (int i = 0; i < rm.Length; i++)
            {
                accum += rm[i];
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
                X = newCenter.x - origin.x,
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
 
            if (key.Frame < track.FromFrame) track.FromFrame = key.Frame;
            if (key.Frame > track.ToFrame) track.ToFrame = key.Frame;
 
            MarkDirty();
        }
 
        private void EnsureCurrentMove()
        {
            if (currentMove != null && currentMove.MoveId == moveId) return;
 
            currentMove = characterData.Find(moveId);
            if (currentMove == null)
            {
                currentMove = new MoveBoxData { MoveId = moveId, TotalFrames = totalFrames };
                characterData.Moves.Add(currentMove);
            }
            currentMove.TotalFrames = totalFrames;
        }
 
        // ===================== 库的加载与合并保存 =====================
 
        private void LoadLibrary()
        {
            characterData = new CharacterBoxData { CharacterId = characterId };
 
            if (File.Exists(LibraryPath))
            {
                string json = File.ReadAllText(LibraryPath);
                CharacterBoxData loaded = null;
 
                try
                {
                    loaded = JsonUtility.FromJson<CharacterBoxData>(json);
                }
                catch (System.Exception e)
                {
                    // 解析失败绝不能静默——否则接下来的保存会用空数据覆盖掉整个文件
                    EditorUtility.DisplayDialog("加载失败",
                        $"JSON 解析出错，为避免覆盖，编辑器不会加载这个文件：\n{e.Message}\n\n" +
                        $"文件：{LibraryPath}", "好");
                    return;
                }
 
                if (loaded != null)
                {
                    characterData = Migrate(loaded);
                }
            }
 
            loadedCharacterId = characterId;
            dirtyMoves.Clear();
            currentMove = null;
            selectedTrack = -1;
 
            if (!string.IsNullOrEmpty(moveId)) EnsureCurrentMove();
            SceneView.RepaintAll();
            Repaint();
        } private static CharacterBoxData Migrate(CharacterBoxData data)
        {
            int from = data.Version; // 旧文件无此字段 → 0，视为 v1
 
            if (from <= 1)
            {
                // v1 → v2：只是多了 Startup/Active/Recovery/Invuln/RootMotion 几个字段。
                // JsonUtility 已把它们填成 0/null，语义上就是"尚未设置"，
                // BoxDataLoader 见到 HasFrameSplit=false / RootMotion=null 会跳过覆盖，
                // 代码里的值继续生效 —— 降级是优雅的，不需要转换。
                Debug.Log($"[HitboxEditor] 检测到 v{Mathf.Max(1, from)} 数据，" +
                          $"已按 v{CharacterBoxData.CurrentVersion} 读入" +
                          "（判定框原样保留；帧分割与位移显示为未设置，可在编辑器里补）。");
            }
 
            if (from > CharacterBoxData.CurrentVersion)
            {
                EditorUtility.DisplayDialog("版本过新",
                    $"这个文件是 v{from}，而当前编辑器只认到 v{CharacterBoxData.CurrentVersion}。\n" +
                    "继续编辑可能丢失新版本的字段。请先更新工具。", "好");
            }
 
            data.Version = CharacterBoxData.CurrentVersion;
            return data;
        }
        
        /// <summary>
        /// 当前帧的【逻辑原点】——把烘焙位移累加到这一帧的结果。
        /// 这正是游戏里 FighterState.Position 在这一帧的值：
        /// 逻辑每帧吃一格 RootMotion，累加出来就是角色所在。
        ///
        /// 判定框的坐标【永远相对逻辑原点】，所以预览必须把角色摆在这里，
        /// 否则你画的框会连动画漂移一起吃进去，游戏里就偏成"幽灵框"。
        /// </summary>
        private Vector3 LogicOriginAt(int frame)
        {
            Vector2[] rm = currentMove?.RootMotion;
            if (rm == null || rm.Length == 0) return Vector3.zero;
 
            Vector2 accum = Vector2.zero;
            int n = Mathf.Min(frame, rm.Length);
            for (int i = 0; i < n; i++) accum += rm[i];
 
            // 逻辑空间 (前, 上) → 战斗平面世界空间 (X, Y)，与 FighterView 的映射一致
            return new Vector3(accum.x, accum.y, 0f);
        }
 
        /// <summary>
        /// 合并式保存 + 自动备份。
        ///
        /// 两道保险：
        ///   ① 合并：读磁盘上的库 → 只用本次编辑过的招式覆盖同名项 → 写回。
        ///      未编辑过的招式原样保留，多次会话/多人协作不会互相覆盖。
        ///   ② 备份：覆盖前先把旧文件复制成 .bak。判定框是几小时的手工劳动，
        ///      一次误操作的代价太大，值得这一行代码。
        /// </summary>
        private void SaveLibrary()
        {
            Directory.CreateDirectory(LibraryFolder);
 
            // ---- 以磁盘版本为基底 ----
            var merged = new CharacterBoxData
            {
                CharacterId = characterId,
                Version = CharacterBoxData.CurrentVersion,
            };
 
            if (File.Exists(LibraryPath))
            {
                // 备份：覆盖前先留一份。判定框是几小时的手工劳动，值得这一行
                string backup = LibraryPath + ".bak";
                File.Copy(LibraryPath, backup, overwrite: true);
 
                try
                {
                    CharacterBoxData onDisk = JsonUtility.FromJson<CharacterBoxData>(
                        File.ReadAllText(LibraryPath));
                    if (onDisk?.Moves != null) merged.Moves.AddRange(Migrate(onDisk).Moves);
                }
                catch (System.Exception e)
                {
                    if (!EditorUtility.DisplayDialog("磁盘文件损坏",
                            $"无法解析磁盘上的库文件：{e.Message}\n\n" +
                            "继续保存会丢失文件里的其他招式（已备份到 .bak）。是否继续？",
                            "继续保存", "取消"))
                    {
                        return;
                    }
                }
            }
 
            // ---- 只把本次编辑过的招式合并进去 ----
            foreach (string id in dirtyMoves)
            {
                MoveBoxData mine = characterData.Find(id);
                if (mine == null) continue;
 
                merged.Moves.RemoveAll(m => m.MoveId == id);
                merged.Moves.Add(mine);
            }
 
            merged.Moves.Sort((a, b) => string.CompareOrdinal(a.MoveId, b.MoveId));
            File.WriteAllText(LibraryPath, JsonUtility.ToJson(merged, true));
            AssetDatabase.Refresh();
 
            int saved = dirtyMoves.Count;
            characterData = merged;
            currentMove = null;
            dirtyMoves.Clear();
            if (!string.IsNullOrEmpty(moveId)) EnsureCurrentMove();
 
            Debug.Log($"[HitboxEditor] 已合并保存 {saved} 个招式，" +
                      $"库中共 {merged.Moves.Count} 个 → {LibraryPath}");
        }
 
        // ===================== 预览控制 =====================
 
        private void SetupPreview()
        {
            DestroyPreview();
            if (rigPrefab == null || clip == null) return;
 
            previewInstance = Instantiate(rigPrefab);
            previewInstance.name = "[HitboxEditor Preview]";
            previewInstance.hideFlags = HideFlags.DontSave;
 
            totalFrames = Mathf.Max(1, Mathf.RoundToInt(clip.length * SampleRate));
            currentFrame = 1;
            moveId = clip.name;
            selectedTrack = -1;
            currentMove = null;
 
            EnsureCurrentMove();
            SampleToFrame();   // 内部会摆到逻辑原点
            AlignSceneView();
        }
 
        /// <summary>把 Scene 视图摆正到战斗平面（正交、正对 XY）。</summary>
        private void AlignSceneView()
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
 
            sv.LookAt(new Vector3(0.3f, 1f, 0f), Quaternion.Euler(0f, 0f, 0f), 2.5f);
            sv.orthographic = true;
            sv.Repaint();
        }
 
        private void DestroyPreview()
        {
            if (previewInstance != null) DestroyImmediate(previewInstance);
            previewInstance = null;
        }
 
        private void SampleToFrame()
        {
            if (previewInstance == null || clip == null) return;
 
            clip.SampleAnimation(previewInstance, (currentFrame - 1) / (float)SampleRate);
 
            // 关键：钉回【逻辑原点】，不是世界零点。
            // 这一步同时也重新施加预览旋转——SampleAnimation 会把根 transform
            // 覆盖成动画里的值，不重设的话角色会转回默认朝向。
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