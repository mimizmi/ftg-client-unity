using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.FixedPoint;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Motion;
using Domain.Net;
using Domain.Net.Transport;
using Loxodon.Framework.Contexts;
using Loxodon.Framework.Services;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Proto = FTG.Net.Proto;

namespace Domain.Service.Battle
{
    /// <summary>
    /// 在线对战组合根（P3）：连 Go 中继服务器、握手拿座位与权威开局头、本地跑回滚驱动、视图跟随预测模拟。
    /// 与单机 BattleBootstrap 并列——差别在于逻辑由 RollbackDriver 驱动（而非 BattleLoop），且本组件
    /// 自身即表现层时钟（IPresentationClock）：每逻辑帧 +1，渲染帧间用累加器进度插值。
    ///
    /// 输入：本地设备经 FightingInputController 采样，每逻辑帧读其 Buffer.Latest 喂回滚驱动的 poll；
    /// 座位由服务器分配，driver 依 localIsP1 把本地/远端输入映射到 P1/P2。
    /// 视图：两个 FighterView 各绑一个 provider —— () => driver.Predicted.P1 / .P2，因为回滚【每帧重建
    /// 预测模拟】，角色对象是临时的，必须每帧现取而非持固定引用。
    ///
    /// 场景 wiring：挂本组件，指派 localSeat（本地玩家的 FightingInputController）；角色 prefab 走
    /// Addressables（地址 = characterAddressFormat 套角色 Id）；需 ApplicationContext 已就绪
    /// （GameLauncher 注册了 IFighterDefinitionRepository）。调 StartOnlineMatch() 开打。
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class NetworkBattleBootstrap : MonoBehaviour, IPresentationClock
    {
        [SerializeField] private FightingInputController localSeat;

        [Header("角色资源")]
        [SerializeField] private string characterAddressFormat = "Characters/{0}";

        [Header("服务器")]
        [SerializeField] private string serverAddress = "127.0.0.1:7777";
        [SerializeField] private string matchId = "m1";
        [SerializeField] private string characterId = "Frank";
        [SerializeField] private int windowSize = 32;

        [Header("流程")]
        [Tooltip("true = Awake 直接连线开打（裸联机场景）；接入菜单流程后取消勾选，由外部调 StartOnlineMatch")]
        [SerializeField] private bool autoStart = false;

        [Tooltip("在 Game 视图显示「开始/停止在线对战」按钮与连接状态——无需接菜单即可 play 测试")]
        [SerializeField] private bool showDebugUI = true;

        private const int TickRate = 60;
        private const float TickDelta = 1f / TickRate;
        private const float MaxAccumulated = 0.25f;

        // ---- IPresentationClock：视图跟随的逻辑帧 + 插值进度 ----
        public int CurrentFrame { get; private set; }
        public float InterpolationAlpha => accumulator / TickDelta;

        public bool Running => driver != null;

        /// <summary>确认模拟（身份稳定、永不回退）：HUD/血条/计时绑这里，仅比画面落后回滚窗口若干帧。</summary>
        public BattleSimulation Simulation => confirmed;

        public int Seat { get; private set; }
        public ConnectionState State => transport?.State ?? ConnectionState.Disconnected;
        public ConnStats Stats => transport != null ? transport.Stats : default;

        private NetClientTransport transport;
        private RollbackDriver driver;
        private BattleSimulation confirmed;
        private float accumulator;
        private GameObject p1Instance;
        private GameObject p2Instance;
        private readonly List<AsyncOperationHandle<GameObject>> handles =
            new List<AsyncOperationHandle<GameObject>>();

        private void Awake()
        {
            if (autoStart) StartOnlineMatch().Forget();
        }

        /// <summary>
        /// 连服务器、握手、装配并开打。await 到就绪：true = driver 已跑；false = 握手超时/加载失败/缺依赖。
        /// </summary>
        public async UniTask<bool> StartOnlineMatch()
        {
            if (Running) StopMatch();

            if (localSeat == null)
            {
                Debug.LogError("[NetworkBattleBootstrap] 未指派 localSeat（本地玩家的 FightingInputController）。", this);
                return false;
            }

            // ① 连服务器 + 握手。WaitReady 会阻塞（重发 Join + 轮询），放线程池别卡主线程；完成后回主线程。
            (string host, int port) = ParseHostPort(serverAddress);
            var join = new Proto.JoinRequest { MatchId = matchId, CharacterId = characterId, ProtocolVersion = 1 };
            transport = new NetClientTransport(host, port, join, windowSize);
            bool ready = await UniTask.RunOnThreadPool(() => transport.WaitReady(TimeSpan.FromSeconds(30)));
            if (!ready)
            {
                Debug.LogError($"[NetworkBattleBootstrap] 与 {serverAddress} 握手超时：确认服务器在跑且有对家。", this);
                StopMatch();
                return false;
            }

            Seat = transport.Seat;
            Proto.MatchSetup setup = transport.Setup;
            BattleConfig config = ReplayProtoCodec.FromProto(setup.Config);
            string p1Char = string.IsNullOrEmpty(setup.P1CharacterId) ? characterId : setup.P1CharacterId;
            string p2Char = string.IsNullOrEmpty(setup.P2CharacterId) ? characterId : setup.P2CharacterId;

            // ② 载入双方角色 prefab（Addressables，镜像 BattleBootstrap）。
            (GameObject p1Prefab, GameObject p2Prefab) =
                await UniTask.WhenAll(LoadCharacter(p1Char), LoadCharacter(p2Char));
            if (p1Prefab == null || p2Prefab == null)
            {
                StopMatch();
                return false;
            }

            // ③ 建 confirmed 模拟（两座位皆 NetworkSeat）+ 回滚驱动。
            var repository = Context.GetApplicationContext().GetService<IFighterDefinitionRepository>();
            if (repository == null)
            {
                Debug.LogError("[NetworkBattleBootstrap] 找不到 IFighterDefinitionRepository（ApplicationContext 未就绪？）。", this);
                StopMatch();
                return false;
            }

            FighterState p1 = BuildFighter("P1", -1f, new NetworkSeat(), repository.Get(p1Char));
            FighterState p2 = BuildFighter("P2", 1f, new NetworkSeat(), repository.Get(p2Char));
            confirmed = new BattleSimulation(p1, p2, new CollisionResolver(), config);
            driver = new RollbackDriver(confirmed, transport, Poll, localIsP1: Seat == 1);

            // ④ 实例化 prefab，视图绑到【当前预测模拟】的对应角色（每帧现取）。
            p1Instance = Instantiate(p1Prefab, SpawnVec(-1f), Quaternion.identity);
            p1Instance.name = $"P1_{p1Char}";
            p2Instance = Instantiate(p2Prefab, SpawnVec(1f), Quaternion.identity);
            p2Instance.name = $"P2_{p2Char}";
            BindView(p1Instance, () => driver.Predicted.P1);
            BindView(p2Instance, () => driver.Predicted.P2);

            // ⑤ 本地设备座位交给我们按逻辑帧驱动采样（关掉它的自驱 Update）。
            localSeat.SelfDriven = false;
            localSeat.Buffer.Clear();
            localSeat.Commands.Clear();
            localSeat.Detector.Clear();

            CurrentFrame = 0;
            accumulator = 0f;
            Debug.Log($"[NetworkBattleBootstrap] 对局就绪：本端座位 P{Seat}（{serverAddress}）。", this);
            return true;
        }

        // 本地设备采样：读 FightingInputController 本帧刚采的输入（Pressed/Released 由驱动据 prevHeld 重算）。
        private LocalInput Poll(int wallTick)
        {
            InputFrame f = localSeat.Buffer.Latest;
            return new LocalInput(f.Direction, f.Held);
        }

        private void Update()
        {
            if (driver == null) return;

            accumulator += Time.deltaTime;
            if (accumulator > MaxAccumulated) accumulator = MaxAccumulated;

            // driver 判空写进循环条件：外部可能在推进途中 StopMatch。
            while (driver != null && accumulator >= TickDelta)
            {
                accumulator -= TickDelta;
                localSeat.ManualTick(); // 采样本地设备到 Buffer
                driver.Advance();       // poll 读 Buffer.Latest：本地即时生效，远端未到即预测/回滚
                CurrentFrame++;
            }
        }

        /// <summary>停止对局并释放传输、实例与 Addressables 句柄。可安全重复调用。</summary>
        public void StopMatch()
        {
            if (p1Instance != null) Destroy(p1Instance);
            if (p2Instance != null) Destroy(p2Instance);
            p1Instance = null;
            p2Instance = null;

            driver = null;
            confirmed = null;
            transport?.Dispose();
            transport = null;

            if (localSeat != null) localSeat.SelfDriven = true;

            for (int i = 0; i < handles.Count; i++)
                Addressables.Release(handles[i]);
            handles.Clear();
        }

        private async UniTask<GameObject> LoadCharacter(string charId)
        {
            string address = string.Format(characterAddressFormat, charId);
            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(address);

            GameObject prefab = null;
            try { prefab = await handle.ToUniTask(); }
            catch (Exception) { /* 失败走下方 null 分支报错 */ }

            handles.Add(handle);
            if (prefab == null)
                Debug.LogError($"[NetworkBattleBootstrap] 角色包加载失败：\"{address}\"" +
                               "（确认 prefab 已标记 Addressable 且 address 精确匹配）。", this);
            return prefab;
        }

        private FighterState BuildFighter(string seatName, float spawnX, IInputSeat seat, FighterDefinition def)
        {
            foreach (MotionPattern motion in def.Motions)
                seat.Detector.Add(motion);

            var moveTable = new MoveTable();
            moveTable.AddRange(def.MoveEntries);

            var fighter = new FighterState(seat, moveTable, def.Movement)
            {
                Name = seatName,
                Position = FixVec2.FromFloat(spawnX, 0f),
            };
            foreach (MoveData move in def.Moves)
                fighter.AddMove(move);
            fighter.SetReactions(def.ReactionMoves);
            return fighter;
        }

        private void BindView(GameObject instance, Func<FighterState> fighterSource)
        {
            var view = instance.GetComponentInChildren<FighterView>();
            if (view == null)
            {
                Debug.LogError($"[NetworkBattleBootstrap] 角色 \"{instance.name}\" 缺少 FighterView。", instance);
                return;
            }
            view.Bind(this, fighterSource); // this = IPresentationClock（跟随回滚逻辑帧）
        }

        // 调试入口：无需接菜单，play 后在 Game 视图直接点按钮开/停在线对战，并显示连接质量。
        private void OnGUI()
        {
            if (!showDebugUI) return;

            const float w = 320f, h = 26f;
            var r = new Rect(12f, 12f, w, h);

            if (!Running)
            {
                if (GUI.Button(r, $"▶ 开始在线对战（{serverAddress}）"))
                    StartOnlineMatch().Forget();
                return;
            }

            ConnStats s = Stats;
            GUI.Label(r, $"座位 P{Seat}ㅤ{State}ㅤ确认帧 {driver.ConfirmedFrame}");
            r.y += h;
            GUI.Label(r, $"RTT≈{s.RttFrames} 帧ㅤ新鲜度 {s.StaleSteps}ㅤ修正 {driver.Corrections}ㅤ最大回滚 {driver.MaxRollback} 帧");
            r.y += h;
            if (GUI.Button(r, "■ 停止")) StopMatch();
        }

        private static Vector3 SpawnVec(float x) => new Vector3(x, 0f, 0f);

        private static (string host, int port) ParseHostPort(string addr)
        {
            int i = addr.LastIndexOf(':');
            return (addr.Substring(0, i), int.Parse(addr.Substring(i + 1)));
        }

        private void OnDestroy() => StopMatch();
    }
}
