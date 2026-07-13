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
 
        private void AlignSceneView()
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
 
            sv.LookAt(new Vector3(0.3f, 1f, 0f), Quaternion.Euler(0f, 0f, 0f), 2.5f);
            sv.orthographic = true;
            sv.Repaint();
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
 
            Rect ruler = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(ruler, new Color(0.16f, 0.16f, 0.16f));
            if (currentMove != null)
            {
                foreach (BoxTrack track in currentMove.Tracks)
                {
                    Color c = ColorOf(track.Kind);
                    c.a = 1f;
                    foreach (BoxKeyframe key in track.Keys)
                    {
                        float x = ruler.x + ruler.width * (key.Frame - 1f) / Mathf.Max(1, totalFrames - 1);
                        EditorGUI.DrawRect(new Rect(x - 1.5f, ruler.y + 3, 3, ruler.height - 6), c);
                    }
                }
            }
            float cursorX = ruler.x + ruler.width * (currentFrame - 1f) / Mathf.Max(1, totalFrames - 1);
            EditorGUI.DrawRect(new Rect(cursorX - 1, ruler.y, 2, ruler.height), Color.white);
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
 
            Vector3 origin = previewInstance.transform.position;
 
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
            GUI.Label(new Rect(10, 10, 400, 40),
                $"帧 {currentFrame} / {totalFrames}   {moveId}",
                EditorStyles.whiteLargeLabel);
            Handles.EndGUI();
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
                CharacterBoxData loaded = JsonUtility.FromJson<CharacterBoxData>(json);
                if (loaded != null) characterData = loaded;
            }
 
            loadedCharacterId = characterId;
            dirtyMoves.Clear();
            currentMove = null;
            selectedTrack = -1;
 
            if (!string.IsNullOrEmpty(moveId)) EnsureCurrentMove();
            SceneView.RepaintAll();
            Repaint();
        }
 
        /// <summary>
        /// 合并式保存：读磁盘上的库 → 用本次编辑过的招式覆盖同名项 → 写回。
        /// 未编辑过的招式原样保留，因此多次会话、多人协作都不会互相覆盖。
        /// （全量覆盖式保存的经典事故：只编了 1 个招式就保存，磁盘上另外 20 个全没了。）
        /// </summary>
        private void SaveLibrary()
        {
            Directory.CreateDirectory(LibraryFolder);
 
            // 以磁盘版本为基底
            var merged = new CharacterBoxData { CharacterId = characterId };
            if (File.Exists(LibraryPath))
            {
                CharacterBoxData onDisk = JsonUtility.FromJson<CharacterBoxData>(
                    File.ReadAllText(LibraryPath));
                if (onDisk?.Moves != null) merged.Moves.AddRange(onDisk.Moves);
            }
 
            // 只把本次编辑过的招式合并进去
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
 
            Debug.Log($"[HitboxEditor] 已合并保存 {saved} 个招式，库中共 {merged.Moves.Count} 个 → {LibraryPath}");
        }
 
        // ===================== 预览控制 =====================
 
        private void SetupPreview()
        {
            DestroyPreview();
            if (rigPrefab == null || clip == null) return;
 
            previewInstance = Instantiate(rigPrefab);
            previewInstance.name = "[HitboxEditor Preview]";
            previewInstance.hideFlags = HideFlags.DontSave;
            previewInstance.transform.SetPositionAndRotation(
                Vector3.zero, Quaternion.Euler(0f, previewRotationY, 0f));
 
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
 
        private void SampleToFrame()
        {
            if (previewInstance == null || clip == null) return;
 
            clip.SampleAnimation(previewInstance, (currentFrame - 1) / (float)SampleRate);
 
            // SampleAnimation 会把 root transform 覆盖成动画里的值，
            // 需要重新施加预览旋转，否则角色会转回朝 Z 的默认朝向
            previewInstance.transform.SetPositionAndRotation(
                Vector3.zero, Quaternion.Euler(0f, previewRotationY, 0f));
 
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