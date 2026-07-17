using System;
using Domain.Infrastructure.Battle;
using Domain.Service.Lua;
using Loxodon.Framework.Binding;
using Loxodon.Framework.Contexts;
using Loxodon.Framework.Messaging;
using Loxodon.Framework.Services;
using UnityEngine;

namespace Domain.Service.App
{
    [DefaultExecutionOrder(-1000)]
    public class GameLauncher : MonoBehaviour
    {
        public const int TickRate = 60;
        private IFighterDefinitionRepository fighterDefinitionRepository;
        private Messenger messenger;
        private LuaService luaService;

        private void Awake()
        {
            Application.targetFrameRate = TickRate;
            ApplicationContext context = Context.GetApplicationContext();
            IServiceContainer container = context.GetContainer();
            BindingServiceBundle bindingBundle = new BindingServiceBundle(container);
            bindingBundle.Start();
            // 帧数据 JSON 经 Addressables 读取（可热更）；仓库懒加载，首次 Get 时目录已被 HotUpdater 刷新
            fighterDefinitionRepository = new ExampleFighterDefinitionRepository(AddressablesTextReader.Read);
            messenger = new Messenger();
            // Lua 虚拟机：脚本同样经 Addressables 读取（逻辑热更与资源/数据同一条 catalog 管线）
            luaService = new LuaService(AddressablesTextReader.Read);
            container.Register<IFighterDefinitionRepository>(fighterDefinitionRepository);
            container.Register<Messenger>(messenger);
            container.Register<LuaService>(luaService);
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // 冒烟：链路通了 Console 会出现 "[Lua] boot.lua 已加载 …"。
            // 注意这发生在 HotUpdater 之前，取的是本地/已缓存版本——正式 Lua 业务模块
            // 应由 GameFlow 在热更完成后再 require，这里只验证管线不承载业务。
            try
            {
                luaService.Require("boot");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GameLauncher] Lua 冒烟未通过（xLua 未导入或 \"Lua/boot\" 未标记 Addressable）：" +
                                 e.Message, this);
            }
        }

        private void Update() => luaService?.Tick(Time.unscaledTime);

        private void OnDestroy() => luaService?.Dispose();
    }
}