using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using Domain.Infrastructure.Replay;
using Loxodon.Framework.Contexts;
using Loxodon.Framework.Messaging;
using Loxodon.Framework.Services;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Domain.Service.Battle
{
    /// <summary>
    /// 战斗组合根：装配双方角色（数据 + Prefab + 输入座位）并驱动 BattleLoop。
    /// 角色 prefab 经 Addressables 按地址异步加载，地址 = characterAddressFormat 套角色 Id——
    /// "角色即内容包"：出新角色 = 多一个 Addressables 包 + 一条 Id，本类代码零改动。
    /// 支持两种用法：
    /// ① autoStart = true：Awake 直接开打（无 UI 流程的裸战斗场景 / 快速调试）；
    /// ② autoStart = false：由 GameFlowController 经 StartBattle/StopBattle 控制
    ///    （主菜单 → 选人 → 战斗 → 结算的完整流程）。可重入：Stop 后可再 Start；
    ///    加载途中被 Stop/顶掉的请求自动作废（代号机制，见 startGeneration）。
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class BattleBootstrap : MonoBehaviour
    {
        [SerializeField] private BattleLoop loop;
        [SerializeField] private FightingInputController p1Seat;
        [SerializeField] private FightingInputController p2Seat;

        [Header("角色资源")]
        [Tooltip("角色 Id → Addressables address 的约定（{0} = 角色 Id，如 Characters/Frank）")]
        [SerializeField] private string characterAddressFormat = "Characters/{0}";

        [Header("默认对阵（autoStart 用）")]
        [SerializeField] private string p1CharacterId = "Frank";
        [SerializeField] private string p2CharacterId = "Frank";

        [Header("出生点")]
        [SerializeField] private float p1SpawnX = -1f;
        [SerializeField] private float p2SpawnX = 1f;

        [Header("流程")]
        [Tooltip("true = Awake 直接开打（裸战斗）；接入 GameFlowController 后取消勾选")]
        [SerializeField] private bool autoStart = true;

        public Context BattleContext => battleContext;
        public bool BattleRunning => battleContext != null;

        private Context battleContext;
        private BattleServiceBundle battleServices;
        private PlayerContext p1Context;
        private PlayerContext p2Context;
        private GameObject p1Instance;
        private GameObject p2Instance;

        // 本场战斗持有的角色 prefab 资源句柄；实例销毁后统一归还（Addressables 自带引用计数，镜像内战两份句柄同样成立）
        private readonly List<AsyncOperationHandle<GameObject>> characterHandles =
            new List<AsyncOperationHandle<GameObject>>();
        // 开战/停战都换代：飞行中的异步加载完成时发现代号已变，即知本场已作废，自还句柄不再回调
        private int startGeneration;

        private void Awake()
        {
            if (autoStart) StartBattle(p1CharacterId, p2CharacterId).Forget();
        }

        /// <summary>
        /// 开一场战斗。await 到装配完成：true = loop.Simulation 已就绪；
        /// false = 加载失败（错误已打日志）或本次请求被顶掉/停掉。已在打则先停（重赛语义）。
        /// </summary>
        public async UniTask<bool> StartBattle(string p1Id, string p2Id)
        {
            if (BattleRunning) StopBattle();

            int generation = ++startGeneration;
            (GameObject p1Prefab, GameObject p2Prefab) =
                await UniTask.WhenAll(LoadCharacter(p1Id, generation), LoadCharacter(p2Id, generation));

            if (generation != startGeneration) return false; // 等待期间被顶掉/停掉：流程已被接管
            if (p1Prefab == null || p2Prefab == null) return false;

            Assemble(p1Id, p2Id, p1Seat, p2Seat, p1Prefab, p2Prefab, configOverride: null);
            return true;
        }

        /// <summary>
        /// 训练模式：P2 换成 AI 假人座位（策略可运行期切换），规则改为永不结束 + 连段后回血。
        /// </summary>
        public async UniTask<bool> StartTraining(string p1Id, string p2Id, IDummyPolicy dummyPolicy)
        {
            if (BattleRunning) StopBattle();

            int generation = ++startGeneration;
            (GameObject p1Prefab, GameObject p2Prefab) =
                await UniTask.WhenAll(LoadCharacter(p1Id, generation), LoadCharacter(p2Id, generation));

            if (generation != startGeneration) return false;
            if (p1Prefab == null || p2Prefab == null) return false;

            var dummy = new AiSeat(dummyPolicy);
            Assemble(p1Id, p2Id, p1Seat, dummy, p1Prefab, p2Prefab, TrainingRules.CreateConfig());
            // 座位先于角色构造：装配完才能把"我/对手"接进 AI 视野
            dummy.Attach(self: loop.Simulation.P2, opponent: loop.Simulation.P1);
            TrainingDummy = dummy;
            trainingRules = new TrainingRules(loop.Simulation);
            return true;
        }

        /// <summary>训练中的假人座位（切换行为用）。非训练模式为 null。</summary>
        public AiSeat TrainingDummy { get; private set; }

        private TrainingRules trainingRules;

        /// <summary>
        /// 以回放数据开一场"比赛重演"：双方座位换成 ReplaySeat，回合规则用录制时的。
        /// 何时结束由调用方（GameFlowController）盯着帧数收场。
        /// </summary>
        public async UniTask<bool> StartReplay(ReplayData replay)
        {
            if (BattleRunning) StopBattle();

            int generation = ++startGeneration;
            (GameObject p1Prefab, GameObject p2Prefab) = await UniTask.WhenAll(
                LoadCharacter(replay.P1CharacterId, generation),
                LoadCharacter(replay.P2CharacterId, generation));

            if (generation != startGeneration) return false;
            if (p1Prefab == null || p2Prefab == null) return false;

            Assemble(replay.P1CharacterId, replay.P2CharacterId,
                new ReplaySeat(replay, isP1: true), new ReplaySeat(replay, isP1: false),
                p1Prefab, p2Prefab, replay.Config);
            return true;
        }

        /// <summary>结束当前战斗并释放一切战斗域资源（含作废飞行中的加载请求）。可安全重复调用。</summary>
        public void StopBattle()
        {
            startGeneration++; // 令飞行中的角色加载过期自弃

            trainingRules?.Dispose();
            trainingRules = null;
            TrainingDummy = null;

            if (BattleRunning)
            {
                loop.Shutdown();

                // 释放顺序：先销毁实例，再停服务包（成对注销），最后销毁各上下文
                if (p1Instance != null) Destroy(p1Instance);
                if (p2Instance != null) Destroy(p2Instance);
                p1Instance = null;
                p2Instance = null;

                battleServices?.Stop();
                battleContext?.Dispose();
                p1Context?.Dispose();
                p2Context?.Dispose();
                battleServices = null;
                battleContext = null;
                p1Context = null;
                p2Context = null;
            }

            // 实例已进销毁队列，才轮到归还其 prefab 的资源句柄
            for (int i = 0; i < characterHandles.Count; i++)
                Addressables.Release(characterHandles[i]);
            characterHandles.Clear();
        }

        // ---- 角色包加载 ----

        private async UniTask<GameObject> LoadCharacter(string characterId, int generation)
        {
            string address = string.Format(characterAddressFormat, characterId);
            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(address);

            GameObject prefab = null;
            try { prefab = await handle.ToUniTask(); }
            catch (Exception) { /* 失败统一走下方 null 分支报错 */ }

            if (generation != startGeneration)
            {
                Addressables.Release(handle); // 等待期间本场被停掉/顶掉：作废自还
                return null;
            }

            characterHandles.Add(handle);
            if (prefab == null)
            {
                Debug.LogError($"[BattleBootstrap] 角色包加载失败：\"{address}\"" +
                               "（确认 prefab 已标记 Addressable 且 address 精确匹配，区分大小写）", this);
            }
            return prefab;
        }

        // ---- 战斗装配（角色包就绪后同步完成） ----

        private void Assemble(string p1Id, string p2Id, IInputSeat seat1, IInputSeat seat2,
            GameObject p1Prefab, GameObject p2Prefab, BattleConfig configOverride)
        {
            ApplicationContext app = Context.GetApplicationContext();
            var repository = app.GetService<IFighterDefinitionRepository>();
            battleContext = new Context(null, app);
            battleServices = new BattleServiceBundle(battleContext.GetContainer());
            battleServices.Start();
            Messenger messenger = battleContext.GetService<Messenger>();
            CollisionResolver resolver = battleContext.GetService<CollisionResolver>();

            FighterState p1 = BuildPlayer(out p1Context, out p1Instance,
                "P1", seat1, p1Prefab, repository.Get(p1Id), FixVec2.FromFloat(p1SpawnX, 0f), messenger);

            FighterState p2 = BuildPlayer(out p2Context, out p2Instance,
                "P2", seat2, p2Prefab, repository.Get(p2Id), FixVec2.FromFloat(p2SpawnX, 0f), messenger);

            loop.Initialize(p1, p2, resolver, messenger, configOverride);

            BindView(p1Instance, p1);
            BindView(p2Instance, p2);
        }

        private FighterState BuildPlayer(out PlayerContext playerContext, out GameObject instance,
            string seatName, IInputSeat seat, GameObject characterPrefab,
            FighterDefinition definition, FixVec2 spawnPosition, Messenger battleMessenger)
        {
            // PlayerContext 默认继承 ApplicationContext 的全部服务
            playerContext = new PlayerContext(name);

            // 座位是长驻对象（真人座位在菜单期间仍自驱采样），开战前必须清干净：
            // 输入历史/指令缓冲若带着菜单残留，实况与回放（ReplaySeat 天生全空）首帧起点就不一致
            seat.Buffer.Clear();
            seat.Commands.Clear();
            seat.Detector.Clear();
            // 用仓库数据装配检测器与角色（定义是共享只读配置，可安全用于双方）
            foreach (MotionPattern motion in definition.Motions)
                seat.Detector.Add(motion);

            // 招式表：指令 → 招式的解析层（连招规则在这里）
            var moveTable = new MoveTable();
            moveTable.AddRange(definition.MoveEntries);

            var fighter = new FighterState(seat, moveTable, definition.Movement)
            {
                Name = seatName, // 曾误写为 name（= 本组件 GameObject 名），双方同名导致战斗日志分不清 P1/P2
                Position = spawnPosition,
            };
            foreach (MoveData move in definition.Moves)
                fighter.AddMove(move);
            // 受击类别 → 受击招式映射（受击招式本身已在上面的 Moves 里，带烘焙位移）
            fighter.SetReactions(definition.ReactionMoves);

            instance = Instantiate(characterPrefab,
                new Vector3(spawnPosition.X.ToFloat(), spawnPosition.Y.ToFloat(), 0f), Quaternion.identity);
            instance.name = $"{seatName}_{definition.CharacterId}";

            // 玩家域服务注册：随 PlayerContext.Dispose 释放。
            // 真人座位额外按具体类型注册（回放/假人座位没有这层需求）
            IServiceContainer container = playerContext.GetContainer();
            if (seat is FightingInputController liveSeat)
                container.Register<FightingInputController>(liveSeat);
            container.Register<FighterState>(fighter);

            return fighter;
        }

        private void BindView(GameObject instance, FighterState fighter)
        {
            var view = instance.GetComponentInChildren<FighterView>();
            if (view == null)
            {
                Debug.LogError($"[BattleBootstrap] 角色 Prefab \"{instance.name}\" 上缺少 FighterView 组件。", instance);
                return;
            }

            view.Bind(loop, fighter);
        }

        private void OnDestroy() => StopBattle();
    }
}
