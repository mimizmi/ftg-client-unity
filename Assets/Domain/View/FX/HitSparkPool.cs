using System.Collections.Generic;
using UnityEngine;

namespace Domain.View.FX
{
    /// <summary>
    /// 命中火花对象池。取用 → 播放 → 粒子自然停止时经 OnParticleSystemStopped
    /// 回调自动归池（stopAction 在创建时强制为 Callback，不依赖 prefab 勾选）。
    /// 战斗高频、生命周期短的表现物一律走池：0 运行期 Instantiate/Destroy 是 M4 的 0GC 前提。
    /// </summary>
    public sealed class HitSparkPool : MonoBehaviour
    {
        [SerializeField] private ParticleSystem sparkPrefab;
        [Tooltip("预热数量：开场先建好，战斗中不再扩容（除非同时命中数超过它）")]
        [SerializeField] private int prewarm = 4;

        private readonly Queue<ParticleSystem> pool = new Queue<ParticleSystem>();
        private bool warnedMissingPrefab;

        private void Start()
        {
            if (sparkPrefab == null) return;
            for (int i = 0; i < prewarm; i++)
                Return(Create());
        }

        /// <summary>在世界坐标处放一发火花。</summary>
        public void Play(Vector3 position)
        {
            if (sparkPrefab == null)
            {
                if (!warnedMissingPrefab)
                {
                    warnedMissingPrefab = true;
                    Debug.LogWarning("[HitSparkPool] 未指定 Spark Prefab，命中火花不显示", this);
                }
                return;
            }

            ParticleSystem ps = pool.Count > 0 ? pool.Dequeue() : Create();
            ps.transform.position = position;
            ps.gameObject.SetActive(true);
            ps.Play();
        }

        internal void Return(ParticleSystem ps)
        {
            ps.gameObject.SetActive(false);
            pool.Enqueue(ps);
        }

        private ParticleSystem Create()
        {
            ParticleSystem ps = Instantiate(sparkPrefab, transform);
            ParticleSystem.MainModule main = ps.main;
            main.stopAction = ParticleSystemStopAction.Callback; // 停止 → OnParticleSystemStopped → 归池
            var returner = ps.gameObject.AddComponent<PooledSparkReturner>();
            returner.Initialize(this, ps);
            return ps;
        }
    }

    /// <summary>粒子停止回调 → 归池。由 HitSparkPool.Create 挂上，不要手动添加。</summary>
    public sealed class PooledSparkReturner : MonoBehaviour
    {
        private HitSparkPool pool;
        private ParticleSystem ps;

        internal void Initialize(HitSparkPool owner, ParticleSystem system)
        {
            pool = owner;
            ps = system;
        }

        private void OnParticleSystemStopped()
        {
            if (pool != null) pool.Return(ps);
        }
    }
}
