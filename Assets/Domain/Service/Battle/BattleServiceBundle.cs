using Domain.Infrastructure.Battle;
using Loxodon.Framework.Messaging;
using Loxodon.Framework.Services;

namespace Domain.Service.Battle
{
    public class BattleServiceBundle : AbstractServiceBundle
    {
        public BattleServiceBundle(IServiceContainer container) : base(container)
        {
        }

        protected override void OnStart(IServiceContainer container)
        {
            container.Register<Messenger>(new Messenger());
            container.Register<CollisionResolver>(new CollisionResolver());
        }

        protected override void OnStop(IServiceContainer container)
        {
            container.Unregister<CollisionResolver>();
            container.Unregister<Messenger>();
        }
    }
}

