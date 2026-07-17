using UnityEngine;

namespace Domain.View.FX
{
    /// <summary>
    /// 角色整体换材驱动器：受击闪白（换 FTG/Flash 数帧再换回）与 KO 溶解
    /// （换 FTG/Dissolve 并把 _Threshold 推向 1）。
    /// 整体换材对原 Shader 零侵入——不要求角色材质带任何约定属性，任何角色通用。
    /// 纯表现层：用 Time.time 计时，不碰模拟。由 BattleFxController 按需 AddComponent。
    /// </summary>
    public sealed class RendererFlasher : MonoBehaviour
    {
        private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");

        private Renderer[] renderers;
        private Material[][] originalMaterials;
        private Material[][] flashMaterials;    // 每个 renderer 一份等长数组，元素全是 flashMaterial
        private Material[][] dissolveMaterials;
        private Material flashMaterial;
        private Material dissolveMaterial;

        private bool flashing;
        private float flashUntil;
        private bool dissolving;
        private float dissolveStart;
        private float dissolveDuration;

        private void Awake()
        {
            renderers = GetComponentsInChildren<Renderer>(true);

            Shader flashShader = Resources.Load<Shader>("FX/FTG_Flash");
            Shader dissolveShader = Resources.Load<Shader>("FX/FTG_Dissolve");
            if (flashShader == null || dissolveShader == null)
            {
                Debug.LogError("[RendererFlasher] 找不到 FX Shader（Resources/FX/FTG_Flash|FTG_Dissolve）", this);
                enabled = false;
                return;
            }
            flashMaterial = new Material(flashShader);
            dissolveMaterial = new Material(dissolveShader);

            originalMaterials = new Material[renderers.Length][];
            flashMaterials = new Material[renderers.Length][];
            dissolveMaterials = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] shared = renderers[i].sharedMaterials;
                originalMaterials[i] = shared;
                flashMaterials[i] = Filled(shared.Length, flashMaterial);
                dissolveMaterials[i] = Filled(shared.Length, dissolveMaterial);
            }
        }

        private static Material[] Filled(int length, Material mat)
        {
            var array = new Material[length];
            for (int i = 0; i < length; i++) array[i] = mat;
            return array;
        }

        /// <summary>闪色数秒（0.05~0.08 ≈ 3~5 帧的经典手感）。溶解中不打断溶解。</summary>
        public void Flash(float seconds, Color color)
        {
            if (dissolving || !enabled) return;
            flashMaterial.SetColor(FlashColorId, color);
            Apply(flashMaterials);
            flashing = true;
            flashUntil = Time.time + seconds;
        }

        /// <summary>KO 溶解：duration 秒内从完整推到完全消失。优先级高于闪白。</summary>
        public void StartDissolve(float duration)
        {
            if (!enabled) return;
            flashing = false;
            dissolving = true;
            dissolveStart = Time.time;
            dissolveDuration = Mathf.Max(0.01f, duration);
            dissolveMaterial.SetFloat(ThresholdId, 0f);
            Apply(dissolveMaterials);
        }

        /// <summary>恢复原貌（新回合开局）。</summary>
        public void ResetVisual()
        {
            flashing = false;
            dissolving = false;
            if (enabled) Apply(originalMaterials);
        }

        private void Update()
        {
            if (flashing && Time.time >= flashUntil)
            {
                flashing = false;
                Apply(originalMaterials);
            }

            if (dissolving)
            {
                float t = Mathf.Clamp01((Time.time - dissolveStart) / dissolveDuration);
                dissolveMaterial.SetFloat(ThresholdId, t);
            }
        }

        private void Apply(Material[][] sets)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].sharedMaterials = sets[i];
            }
        }

        private void OnDestroy()
        {
            if (flashMaterial != null) Destroy(flashMaterial);
            if (dissolveMaterial != null) Destroy(dissolveMaterial);
        }
    }
}
