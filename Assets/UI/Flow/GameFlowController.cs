using Domain.Infrastructure.Replay;
using Domain.Service.App;
using Domain.Service.Battle;
using Domain.Service.Replay;
using Domain.UI.Battle;
using UnityEngine;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 游戏流程状态机：主菜单 → 选人 → 战斗(HUD) → 结算 → 再战/回菜单。
    /// 状态即"当前开着哪些界面 + 战斗开没开"，转移函数就是下面的方法——
    /// 每个转移只做三件事：关旧界面、切战斗开关、开新界面。
    /// 使用它时把 BattleBootstrap 的 autoStart 取消勾选（否则一进场景就自己开打了）。
    /// </summary>
    public sealed class GameFlowController : MonoBehaviour
    {
        [SerializeField] private UIManager ui;
        [SerializeField] private BattleBootstrap bootstrap;
        [SerializeField] private BattleLoop loop;
        [SerializeField] private HotUpdater hotUpdater; // 可选：不拖 = 跳过启动热更检查

        [Header("可选角色（数据驱动，M3 起随角色包扩充）")]
        [SerializeField] private string[] characterIds = { "Frank" };

        [Header("UI 资源 key")]
        [SerializeField] private string menuKey = "UI/MainMenu";
        [SerializeField] private string selectKey = "UI/CharacterSelect";
        [SerializeField] private string resultKey = "UI/Result";
        [SerializeField] private string hudKey = "UI/BattleHud";
        [SerializeField] private string loadingKey = "UI/Loading";

        private UIScreen menuScreen;
        private UIScreen selectScreen;
        private UIScreen resultScreen;
        private UIScreen hudScreen;
        private UIScreen loadingScreen;
        private bool loadingDismissed; // 防时序：战斗装配可能比 Loading 界面自身的加载还快
        private LoadingView loadingView; // loadingScreen 的类型化引用（热更状态文本用）
        private string loadingStatus;    // 最近一次状态：Loading 界面异步开好前先攒着，开好补发
        private string p1Id, p2Id; // 本场对阵（再战用）

        private ReplayRecorder recorder;  // 每场真人对局自动录制
        private int replayEndFrame = -1;  // 观看回放时的收场帧（-1 = 不在看回放）

        private void Start()
        {
            if (ui == null || bootstrap == null || loop == null)
            {
                Debug.LogError("[GameFlow] 缺引用：请拖入 UIManager / BattleBootstrap / BattleLoop", this);
                return;
            }

            if (hotUpdater != null)
            {
                // 先热更后进菜单：catalog/增量包在 Loading 界面里拉完，之后的所有加载都拿到新版本
                ShowLoading();
                hotUpdater.Run(
                    onStatus: SetLoadingStatus,
                    onFinished: () => { HideLoading(); ShowMenu(); });
            }
            else
            {
                ShowMenu();
            }
        }

        // ---- 状态转移 ----

        private void ShowMenu()
        {
            ui.Open<MainMenuView>(menuKey, onOpened: view =>
            {
                menuScreen = view;
                view.Bind(new MainMenuViewModel(
                    onStart: ShowCharacterSelect, onQuit: Quit, onReplay: WatchLatestReplay));
            });
        }

        private void ShowCharacterSelect()
        {
            CloseScreen(ref menuScreen);
            ui.Open<CharacterSelectView>(selectKey, onOpened: view =>
            {
                selectScreen = view;
                view.Bind(new CharacterSelectViewModel(characterIds,
                    onConfirm: StartBattle, onBack: BackToMenuFromSelect));
            });
        }

        private void BackToMenuFromSelect()
        {
            CloseScreen(ref selectScreen);
            ShowMenu();
        }

        private void StartBattle(string p1, string p2)
        {
            CloseScreen(ref selectScreen);
            p1Id = p1;
            p2Id = p2;

            ShowLoading();
            bootstrap.StartBattle(p1, p2, onFinished: ok =>
            {
                HideLoading();
                if (!ok) { ShowMenu(); return; } // 角色包加载失败：错误已打日志，退回菜单

                loop.Simulation.MatchEnded += OnMatchEnded;

                // 每场自动录制：确定性模拟下回放 = 每帧 6 字节的输入流
                recorder?.Stop();
                recorder = new ReplayRecorder(loop.Simulation, p1, p2);

                OpenHud();
            });
        }

        private void ShowLoading()
        {
            loadingDismissed = false;
            loadingStatus = null;
            ui.Open<LoadingView>(loadingKey, onOpened: view =>
            {
                if (loadingDismissed) { ui.Close(view); return; } // 装配抢先完成：开完即关
                loadingScreen = view;
                loadingView = view;
                if (loadingStatus != null) view.SetStatus(loadingStatus); // 补发攒下的状态
            });
        }

        private void HideLoading()
        {
            loadingDismissed = true;
            loadingView = null;
            CloseScreen(ref loadingScreen);
        }

        private void SetLoadingStatus(string text)
        {
            loadingStatus = text;
            loadingView?.SetStatus(text);
        }

        private void OpenHud()
        {
            ui.Open<BattleHudView>(hudKey, onOpened: view =>
            {
                hudScreen = view;
                view.Bind(new BattleHudViewModel(loop.Simulation));
            });
        }

        private void OnMatchEnded(int winner)
        {
            loop.Simulation.MatchEnded -= OnMatchEnded;

            if (recorder != null)
            {
                recorder.Stop();
                ReplayFileStore.Save(recorder.Data);
                recorder = null;
            }

            ui.Open<ResultView>(resultKey, onOpened: view =>
            {
                resultScreen = view;
                view.Bind(new ResultViewModel(winner, onRematch: Rematch, onMenu: BackToMenuFromResult));
            });
        }

        // ---- 回放观看 ----

        private void WatchLatestReplay()
        {
            ReplayData data = ReplayFileStore.LoadLatest();
            if (data == null)
            {
                Debug.Log("[GameFlow] 还没有回放：先打一场吧");
                return; // 停留在主菜单
            }

            CloseScreen(ref menuScreen);
            ShowLoading();
            bootstrap.StartReplay(data, onFinished: ok =>
            {
                HideLoading();
                if (!ok) { ShowMenu(); return; }

                replayEndFrame = data.FrameCount + 60; // 播完留 1 秒余韵再收场
                loop.Simulation.TickFinished += OnReplayTick;

                OpenHud();
            });
        }

        private void OnReplayTick(int frame)
        {
            if (frame < replayEndFrame) return;
            EndReplay();
        }

        private void EndReplay()
        {
            if (loop.Simulation != null) loop.Simulation.TickFinished -= OnReplayTick;
            replayEndFrame = -1;

            CloseScreen(ref hudScreen);
            bootstrap.StopBattle();
            ShowMenu();
        }

        private void Rematch()
        {
            CloseScreen(ref resultScreen);
            CloseScreen(ref hudScreen); // HUD 的 VM 绑着旧模拟，必须随旧战斗一起关
            StartBattle(p1Id, p2Id);    // StartBattle 内部会先 Stop 旧战斗
        }

        private void BackToMenuFromResult()
        {
            CloseScreen(ref resultScreen);
            CloseScreen(ref hudScreen);
            if (loop.Simulation != null) loop.Simulation.MatchEnded -= OnMatchEnded;
            bootstrap.StopBattle();
            ShowMenu();
        }

        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void CloseScreen(ref UIScreen screen)
        {
            if (screen != null) ui.Close(screen);
            screen = null;
        }
    }
}
