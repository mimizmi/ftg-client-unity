using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Editor.EditorTools
{
     /// <summary>
    /// 批量根位移烘焙工具。
    ///
    /// 相比单个烘焙(RootMotionBaker)，本工具：
    /// - 支持一次拖入多个 Clip / 整个 fbx（自动取出全部子 clip）/ 整个文件夹
    /// - 生成【静态数据类】而非巨型 switch：每个 clip 一个字段 + 字典查表。
    ///   理由：switch 版本会随招式增长膨胀成几千行、每次改动画都要改代码，
    ///   且把"数据"硬编进"代码"，违背数据所有权归仓库的原则。
    ///   静态类 + 字典的形式既能直接用，将来迁移 ScriptableObject 也只需换 Provider。
    /// - 输出【汇总表】：一眼看出哪些 clip 有位移、总位移多少、是否只是噪声，
    ///   这是批量处理后最需要的信息（几十个 clip 摆在面前，得先知道哪些值得用）。
    ///
    /// 前进轴：3D 角色通常是局部 Z（Unity 标准前向）。局部空间的位移与场景里
    /// 模型转了多少度无关——Rotation Y=90 只改变世界朝向，不改变 clip 内的数据。
    /// </summary>
    public sealed class BatchRootMotionBaker : EditorWindow
    {
        public enum ForwardAxis { Z, X }
 
        private readonly List<AnimationClip> clips = new List<AnimationClip>();
        private GameObject rigPrefab;
        private ForwardAxis forwardAxis = ForwardAxis.Z;
        private string className = "FrankRootMotion";
        private float noiseThreshold = 0.005f;
 
        private const int SampleRate = 60;
 
        private readonly List<BakeResult> results = new List<BakeResult>();
        private Vector2 listScroll, codeScroll;
        private string generatedCode = "";
 
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
            rigPrefab = (GameObject)EditorGUILayout.ObjectField(
                "Rig Prefab (Humanoid 必填)", rigPrefab, typeof(GameObject), false);
            forwardAxis = (ForwardAxis)EditorGUILayout.EnumPopup("前进轴", forwardAxis);
            className = EditorGUILayout.TextField("生成类名", className);
            noiseThreshold = EditorGUILayout.FloatField("噪声阈值(米)", noiseThreshold);
 
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("② 拖入 Clip / fbx / 文件夹", EditorStyles.boldLabel);
 
            // 拖拽区：接受 clip、fbx（自动展开子 clip）、文件夹（递归搜索）
            Rect dropArea = GUILayoutUtility.GetRect(0, 60, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, $"拖到这里（已加入 {clips.Count} 个 Clip）");
            HandleDragAndDrop(dropArea);
 
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空列表")) { clips.Clear(); results.Clear(); generatedCode = ""; }
                using (new EditorGUI.DisabledScope(clips.Count == 0))
                {
                    if (GUILayout.Button($"批量烘焙 ({clips.Count})")) BakeAll();
                }
            }
 
            if (results.Count == 0) return;
 
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("③ 汇总表", EditorStyles.boldLabel);
            DrawSummary();
 
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("④ 生成代码", EditorStyles.boldLabel);
                if (GUILayout.Button("复制到剪贴板", GUILayout.Width(120)))
                    EditorGUIUtility.systemCopyBuffer = generatedCode;
                if (GUILayout.Button("保存为 .cs", GUILayout.Width(100)))
                    SaveToFile();
            }
 
            codeScroll = EditorGUILayout.BeginScrollView(codeScroll, GUILayout.Height(200));
            EditorGUILayout.TextArea(generatedCode, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
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
                evt.Use();
            }
        }
 
        /// <summary>从拖入对象收集 Clip：直接的 clip、fbx 里的子 clip、文件夹里递归找。</summary>
        private void CollectClips(Object obj)
        {
            string path = AssetDatabase.GetAssetPath(obj);
 
            if (obj is AnimationClip directClip)
            {
                AddClip(directClip);
                return;
            }
 
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
            {
                // 文件夹：递归找所有 clip
                string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { path });
                foreach (string guid in guids)
                {
                    string clipPath = AssetDatabase.GUIDToAssetPath(guid);
                    foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(clipPath))
                        if (sub is AnimationClip c) AddClip(c);
                }
                return;
            }
 
            // fbx / 其他资源：取出内部所有 clip（Unity 的 __preview__ 内部 clip 要排除）
            if (!string.IsNullOrEmpty(path))
            {
                foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (sub is AnimationClip c) AddClip(c);
            }
        }
 
        private void AddClip(AnimationClip c)
        {
            if (c == null) return;
            if (c.name.StartsWith("__preview__")) return; // Unity 内部预览 clip
            if (clips.Contains(c)) return;
            clips.Add(c);
        }
 
        private void BakeAll()
        {
            results.Clear();
 
            for (int i = 0; i < clips.Count; i++)
            {
                AnimationClip c = clips[i];
                EditorUtility.DisplayProgressBar("批量烘焙", c.name, i / (float)clips.Count);
 
                Vector2[] deltas = Sample(c);
                var r = new BakeResult { ClipName = c.name, Deltas = deltas, Frames = deltas.Length };
 
                foreach (Vector2 d in deltas)
                {
                    r.NetX += d.x; r.NetY += d.y;
                    r.TravelX += Mathf.Abs(d.x); r.TravelY += Mathf.Abs(d.y);
                }
 
                // 按累积位移判断（噪声不累积，真位移会累积），而非单帧阈值——
                // 单帧阈值会把小位移动画（轻踢重心前压）整段误杀成 0
                r.HasMotion =
                    Mathf.Abs(r.NetX) >= noiseThreshold || r.TravelX >= noiseThreshold * 2f ||
                    Mathf.Abs(r.NetY) >= noiseThreshold || r.TravelY >= noiseThreshold * 2f;
 
                results.Add(r);
            }
 
            EditorUtility.ClearProgressBar();
            generatedCode = GenerateClass();
        }
 
        private Vector2[] Sample(AnimationClip c)
        {
            GameObject sampler = rigPrefab != null
                ? Instantiate(rigPrefab)
                : new GameObject("Sampler");
            try
            {
                sampler.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                int frames = Mathf.Max(1, Mathf.RoundToInt(c.length * SampleRate));
                var deltas = new Vector2[frames];
 
                c.SampleAnimation(sampler, 0f);
                Vector3 prev = sampler.transform.position;
 
                for (int f = 1; f <= frames; f++)
                {
                    c.SampleAnimation(sampler, f / (float)SampleRate);
                    Vector3 pos = sampler.transform.position;
                    float horizontal = forwardAxis == ForwardAxis.Z ? pos.z - prev.z : pos.x - prev.x;
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
 
        private void DrawSummary()
        {
            int withMotion = results.Count(r => r.HasMotion);
            EditorGUILayout.HelpBox(
                $"共 {results.Count} 个 Clip，其中 {withMotion} 个有位移，" +
                $"{results.Count - withMotion} 个无位移（原地动画或噪声）。\n" +
                "无位移的 Clip 不会写进生成的数据类——它们不需要 RootMotion 字段。",
                MessageType.Info);
 
            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.Height(180));
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
        }
 
        /// <summary>
        /// 生成静态数据类：每个 clip 一个【方法】+ 懒加载字典查表。
        /// 用法：MoveData.RootMotion = FrankRootMotion.Get("Frank_FS4_Attack_Kick_L_02");
        ///
        /// 【为什么是方法而不是 static readonly 字段】
        /// 几百个静态数组初始化器会被编译器全部塞进同一个 .cctor，每个数组字面量
        /// 在同一方法体内展开局部临时变量 → 栈帧随 clip 数线性膨胀，
        /// 招式一多就会撞上方法体大小上限甚至 stack overflow。
        /// 拆成"BuildTable + 每 clip 一个方法"后：BuildTable 只有 N 条调用语句（栈帧很小），
        /// 每个 XxxClip() 独立进出栈、互不叠加。顺带也消除了静态字段的初始化顺序问题。
        /// </summary>
        private string GenerateClass()
        {
            var withMotion = results.Where(r => r.HasMotion).ToList();
 
            var sb = new StringBuilder(8192);
            sb.AppendLine("// ⚠ 本文件由 FG/Batch Root Motion Baker 自动生成，请勿手改——重烘会覆盖。");
            sb.AppendLine("// 手调位移请在 FighterDefinition 里覆写 MoveData.RootMotion。");
            sb.AppendLine($"// 前进轴: 局部 {forwardAxis}；采样率: {SampleRate}Hz");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine("namespace Domain.Infrastructure.Auto");
            sb.AppendLine("{");
            sb.AppendLine($"    public static class {className}");
            sb.AppendLine("    {");
 
            // 懒加载：首次 Get 时才建表，避免类型加载即付出全部构造成本
            sb.AppendLine("        private static Dictionary<string, Vector2[]> table;");
            sb.AppendLine("        private static Dictionary<string, Vector2[]> Table => table ??= BuildTable();");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>按 Clip 名取位移数据；无位移的招式返回 null（角色原地不动）。</summary>");
            sb.AppendLine("        public static Vector2[] Get(string clipName)");
            sb.AppendLine("            => Table.TryGetValue(clipName, out Vector2[] m) ? m : null;");
            sb.AppendLine();
 
            // BuildTable：只有 N 条方法调用，栈帧恒定小
            sb.AppendLine("        private static Dictionary<string, Vector2[]> BuildTable() => new Dictionary<string, Vector2[]>");
            sb.AppendLine("        {");
            int pad = withMotion.Count == 0 ? 0 : withMotion.Max(r => r.ClipName.Length) + 3;
            foreach (BakeResult r in withMotion)
            {
                string key = $"\"{r.ClipName}\",".PadRight(pad + 1);
                sb.AppendLine($"            {{ {key} {MethodName(r.ClipName)}() }},");
            }
            sb.AppendLine("        };");
            sb.AppendLine();
 
            // 每个 clip 一个独立方法：调完即出栈，互不叠加
            foreach (BakeResult r in withMotion)
            {
                sb.AppendLine($"        // {r.ClipName}: {r.Frames} 帧, 净位移 前后 {r.NetX:F4} / 上下 {r.NetY:F4}");
                sb.AppendLine($"        private static Vector2[] {MethodName(r.ClipName)}() => new[]");
                sb.AppendLine("        {");
                for (int i = 0; i < r.Deltas.Length; i++)
                {
                    Vector2 d = r.Deltas[i];
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "            new Vector2({0:F5}f, {1:F5}f), // 帧{2}", d.x, d.y, i + 1));
                }
                sb.AppendLine("        };");
                sb.AppendLine();
            }
 
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }
 
        /// <summary>
        /// Clip 名 → 合法的 C# 方法名。去掉常见的角色前缀让方法名更短，
        /// 并保证首字符是字母（数字开头的名字不是合法标识符）。
        /// </summary>
        private static string MethodName(string clipName)
        {
            var sb = new StringBuilder(clipName.Length);
            foreach (char ch in clipName)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
 
            string name = sb.ToString();
            if (name.Length == 0 || char.IsDigit(name[0])) name = "_" + name;
            return name;
        }
 
        private void SaveToFile()
        {
            string path = EditorUtility.SaveFilePanel(
                "保存生成的数据类", "Assets/Domain/Infrastructure/Auto", className + ".cs", "cs");
            if (string.IsNullOrEmpty(path)) return;
 
            File.WriteAllText(path, generatedCode);
            AssetDatabase.Refresh();
            Debug.Log($"[BatchRootMotionBaker] 已保存: {path}");
        }
    }
}