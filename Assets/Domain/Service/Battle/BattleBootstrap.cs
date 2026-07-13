using System;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Motion;
using Loxodon.Framework.Contexts;
using Loxodon.Framework.Messaging;
using Loxodon.Framework.Services;
using UnityEngine;

namespace Domain.Service.Battle
{
    [DefaultExecutionOrder(-500)] 
    public class BattleBootstrap : MonoBehaviour
    {
        [SerializeField] private BattleLoop loop;
        [SerializeField] private FightingInputController p1Seat;
        [SerializeField] private FightingInputController p2Seat;
        
        [Header("角色（Prefab 资产 + 数据 ID）")]
        [SerializeField] private GameObject p1CharacterPrefab;
        [SerializeField] private GameObject p2CharacterPrefab;
        [SerializeField] private string p1CharacterId = "Frank";
        [SerializeField] private string p2CharacterId = "Frank";
        
        [Header("出生点")]
        [SerializeField] private float p1SpawnX = -1f;
        [SerializeField] private float p2SpawnX = 1f;
        
        public Context BattleContext => battleContext;
        
        private Context battleContext;
        private BattleServiceBundle battleServices;
        private PlayerContext p1Context;
        private PlayerContext p2Context;
        private GameObject p1Instance;
        private GameObject p2Instance;

        private void Awake()
        {
            ApplicationContext app = Context.GetApplicationContext();
            var repository = app.GetService<IFighterDefinitionRepository>();
            battleContext = new Context(null, app);
            battleServices = new BattleServiceBundle(battleContext.GetContainer());
            battleServices.Start();
            Messenger messenger = battleContext.GetService<Messenger>();
            CollisionResolver resolver = battleContext.GetService<CollisionResolver>();
            
            FighterState p1 = BuildPlayer(out p1Context, out p1Instance,
                "P1", p1Seat, p1CharacterPrefab,
                repository.Get(p1CharacterId), new Vector2(p1SpawnX, 0f), messenger);
 
            FighterState p2 = BuildPlayer(out p2Context, out p2Instance,
                "P2", p2Seat, p2CharacterPrefab,
                repository.Get(p2CharacterId), new Vector2(p2SpawnX, 0f), messenger);
            
            loop.Initialize(p1, p2, resolver, messenger);
            
            BindView(p1Instance, p1);
            BindView(p2Instance, p2);
        }
        
        private FighterState BuildPlayer(out PlayerContext playerContext, out GameObject instance,
            string seatName, FightingInputController seat, GameObject characterPrefab,
            FighterDefinition definition, Vector2 spawnPosition, Messenger battleMessenger)
        {
            // PlayerContext 默认继承 ApplicationContext 的全部服务
            playerContext = new PlayerContext(name);

            seat.Detector.Clear();
            // 用仓库数据装配检测器与角色（定义是共享只读配置，可安全用于双方）
            foreach (MotionPattern motion in definition.Motions)
                seat.Detector.Add(motion);

            // 招式表：指令 → 招式的解析层（连招规则在这里）
            var moveTable = new MoveTable();
            moveTable.AddRange(definition.MoveEntries);

            var fighter = new FighterState(seat, moveTable, definition.Movement)
            {
                Name = name,
                Position = spawnPosition,
            };
            foreach (MoveData move in definition.Moves)
                fighter.AddMove(move);
            
            instance = Instantiate(characterPrefab,
                new Vector3(spawnPosition.x, spawnPosition.y, 0f), Quaternion.identity);
            instance.name = $"{seatName}_{definition.CharacterId}";

            // 玩家域服务注册：随 PlayerContext.Dispose 释放
            IServiceContainer container = playerContext.GetContainer();
            container.Register<FightingInputController>(seat);
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

        private void OnDestroy()
        {
            // 释放顺序：先停服务包（成对注销），再整体销毁各上下文
            if (p1Instance != null) Destroy(p1Instance);
            if (p2Instance != null) Destroy(p2Instance);
            battleServices?.Stop();
            battleContext?.Dispose();
            p1Context?.Dispose();
            p2Context?.Dispose();
        }
    }
}