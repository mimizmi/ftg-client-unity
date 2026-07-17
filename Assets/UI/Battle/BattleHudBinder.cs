using UnityEngine;

namespace Domain.UI.Battle
{
    /// <summary>
    /// 战斗场景的 HUD 组合根：等模拟就绪（BattleBootstrap.Awake @-500 已跑完）后，
    /// 经 UIManager 打开 HUD 并完成 VM ↔ 模拟 的接线。
    /// UI 与战斗代码互不相识，全靠这里牵线——删掉本物体，战斗照常裸奔。
    /// </summary>
    public sealed class BattleHudBinder : MonoBehaviour
    {
        [SerializeField] private Domain.Service.Battle.BattleLoop loop;
        [SerializeField] private UIManager ui;
        [SerializeField] private string hudKey = "UI/BattleHud";

        private void Start()
        {
            if (loop == null || ui == null)
            {
                Debug.LogError("[BattleHudBinder] 缺引用：请在 Inspector 拖入 BattleLoop 与 UIManager", this);
                return;
            }
            if (loop.Simulation == null)
            {
                Debug.LogError("[BattleHudBinder] BattleLoop 尚未 Initialize（检查 BattleBootstrap 执行顺序）", this);
                return;
            }

            ui.Open<BattleHudView>(hudKey,
                onOpened: view => view.Bind(new BattleHudViewModel(loop.Simulation)));
        }
    }
}
