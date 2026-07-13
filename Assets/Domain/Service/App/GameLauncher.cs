using Domain.Infrastructure.Battle;
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
        private void Awake()
        {
            Application.targetFrameRate = TickRate;
            ApplicationContext context = Context.GetApplicationContext();
            IServiceContainer container = context.GetContainer();
            BindingServiceBundle bindingBundle = new BindingServiceBundle(container);
            bindingBundle.Start();
            fighterDefinitionRepository = new ExampleFighterDefinitionRepository();
            messenger = new Messenger();
            container.Register<IFighterDefinitionRepository>(fighterDefinitionRepository);
            container.Register<Messenger>(messenger);
            DontDestroyOnLoad(gameObject);
        }
    }
}