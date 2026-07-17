using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Domain.UI.Battle
{
    /// <summary>
    /// 战斗场景的 HUD 组合根（裸战斗/autoStart 模式用；完整流程由 GameFlowController 接管）。
    /// 等模拟就绪（Bootstrap 的角色包是异步加载的）后，经 UIManager 打开 HUD 并接线。
    /// UI 与战斗代码互不相识，全靠这里牵线——删掉本物体，战斗照常裸奔。
    /// </summary>
    public sealed class BattleHudBinder : MonoBehaviour
    {
        [SerializeField] private Domain.Service.Battle.BattleLoop loop;
        [SerializeField] private UIManager ui;
        [SerializeField] private string hudKey = "UI/BattleHud";

        private async void Start()
        {
            if (loop == null || ui == null)
            {
                Debug.LogError("[BattleHudBinder] 缺引用：请在 Inspector 拖入 BattleLoop 与 UIManager", this);
                return;
            }

            // 角色包异步装配：等 Simulation 出现（autoStart 的 Awake 只是发起了加载）
            await UniTask.WaitUntil(() => loop == null || loop.Simulation != null);
            if (loop == null || loop.Simulation == null) return;

            BattleHudView view = await ui.Open<BattleHudView>(hudKey);
            if (view != null) view.Bind(new BattleHudViewModel(loop.Simulation));
        }
    }
}
