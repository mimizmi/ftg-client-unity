using UnityEngine;

namespace Domain.View.FX
{
    /// <summary>
    /// 创伤式相机震动（trauma model）：命中累积 trauma，按平方映射成振幅，随时间衰减。
    /// 平方映射是手感关键——轻击几乎不晃，重击/拼招明显，连续命中自然叠加但有上限。
    /// Perlin 噪声驱动偏移（连续、无跳变）。纯表现层，只动本地坐标，不碰模拟。
    /// 挂在 Main Camera 上。将来换 Cinemachine Impulse 时，本组件即可删除。
    /// </summary>
    public sealed class CameraShaker : MonoBehaviour
    {
        [Tooltip("trauma 每秒衰减量")]
        [SerializeField] private float decay = 2.5f;
        [Tooltip("满 trauma 时的最大位移（世界单位）")]
        [SerializeField] private float maxOffset = 0.22f;
        [Tooltip("噪声频率：越大越\"抖\"，越小越\"晃\"")]
        [SerializeField] private float frequency = 22f;

        private Vector3 basePosition;
        private float trauma;

        private void Awake() => basePosition = transform.localPosition;

        /// <summary>叠加震动（0~1；轻击 0.25 / 重击拼招 0.5 量级）。</summary>
        public void Shake(float amount) => trauma = Mathf.Clamp01(trauma + amount);

        private void LateUpdate()
        {
            if (trauma <= 0f)
            {
                transform.localPosition = basePosition;
                return;
            }

            trauma = Mathf.Max(0f, trauma - decay * Time.deltaTime);
            float shake = trauma * trauma; // 平方映射：小值更小，大值保留冲击

            float t = Time.time * frequency;
            var offset = new Vector3(
                (Mathf.PerlinNoise(t, 0.5f) * 2f - 1f),
                (Mathf.PerlinNoise(0.5f, t) * 2f - 1f),
                0f);

            transform.localPosition = basePosition + offset * (shake * maxOffset);
        }
    }
}
