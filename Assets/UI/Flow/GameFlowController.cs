using System;
using System.Globalization;
using Cysharp.Threading.Tasks;
using Domain.Infrastructure.Battle;
using Domain.Infrastructure.Input;
using Domain.Infrastructure.Replay;
using Domain.Service.App;
using Domain.Service.Battle;
using Domain.Service.Localization;
using Domain.Service.Lua;
using Domain.Service.Replay;
using Domain.UI.Battle;
using Loxodon.Framework.Contexts;
using Loxodon.Framework.Localizations;
using UnityEngine;
using XLua;

namespace Domain.UI.Flow
{
    /// <summary>
    /// 游戏流程状态机：主菜单 → 选人 → 战斗(HUD) → 结算 → 再战/回菜单，外加训练场与回放。
    /// 每个转移只做三件事：关旧界面、切战斗开关、开新界面。
    /// M6 起 UniTask 化：流程步骤就是一段顺序 await（开 Loading → 装配 → 开 HUD），
    /// 不再有回调金字塔；async void 只允许出现在事件入口（按钮回调/Unity 生命周期），
    /// 内部一律 UniTask。使用它时把 BattleBootstrap 的 autoStart 取消勾选。
    /// </summary>
    public sealed class GameFlowController : MonoBehaviour
    {
        [SerializeField] private UIManager ui;
        [SerializeField] private BattleBootstrap bootstrap;
        [SerializeField] private BattleLoop loop;
        [SerializeField] private NetworkBattleBootstrap netBootstrap; // 可选：在线对战组合根（不拖 = 无在线入口）
        [SerializeField] private HotUpdater hotUpdater; // 可选：不拖 = 跳过启动热更检查

        [Header("可选角色（数据驱动，随角色包扩充）")]
        [SerializeField] private string[] characterIds = { "Frank" };

        [Header("UI 资源 key")]
        [SerializeField] private string menuKey = "UI/MainMenu";
        [SerializeField] private string selectKey = "UI/CharacterSelect";
        [SerializeField] private string resultKey = "UI/Result";
        [SerializeField] private string hudKey = "UI/BattleHud";
        [SerializeField] private string loadingKey = "UI/Loading";
        [SerializeField] private string noticeKey = "UI/Notice";

        private UIScreen menuScreen;
        private UIScreen selectScreen;
        private UIScreen resultScreen;
        private UIScreen hudScreen;

        private UIScreen loadingScreen;
        private bool loadingDismissed; // 防时序：战斗装配可能比 Loading 界面自身的加载还快
        private LoadingView loadingView; // loadingScreen 的类型化引用（热更状态文本用）
        private string loadingStatus;    // 最近一次状态：Loading 界面异步开好前先攒着，开好补发

        private UIScreen noticeScreen;
        private bool noticeChecked; // 公告每会话只探测一次（热更完成后的首次主菜单）

        private bool pendingTraining; // 选人界面确认后进训练场（而非对战）
        private bool trainingActive;  // 训练进行中：Update 里响应 F1-F4 换假人 / Esc 退出

        private string p1Id, p2Id; // 本场对阵（再战用）

        private ReplayRecorder recorder;  // 每场真人对局自动录制
        private int replayEndFrame = -1;  // 观看回放时的收场帧（-1 = 不在看回放）

        private async void Start()
        {
            if (ui == null || bootstrap == null || loop == null)
            {
                Debug.LogError("[GameFlow] 缺引用：请拖入 UIManager / BattleBootstrap / BattleLoop", this);
                return;
            }

            if (hotUpdater != null)
            {
                // 先热更后进菜单：catalog/增量包在 Loading 界面里拉完，之后的所有加载都拿到新版本
                ShowLoading().Forget();
                await hotUpdater.Run(SetLoadingStatus);
                HideLoading();
            }
            await InitLocalization(); // 热更之后再装语言表：翻译文案吃到最新版本
            ShowMenu();
        }

        // ---- 状态转移 ----

        private async void ShowMenu()
        {
            MainMenuView view = await ui.Open<MainMenuView>(menuKey);
            if (view == null) return;
            menuScreen = view;
            view.Bind(new MainMenuViewModel(
                onStart: ShowCharacterSelect, onQuit: Quit, onReplay: WatchLatestReplay,
                onTraining: ShowTrainingSelect, onLanguage: ToggleLanguage, onOnline: StartOnlineBattle));
            TryShowLuaNotice();
        }

        /// <summary>
        /// 运营公告：显示开关/标题/正文/关闭回调全部来自 Lua 热更脚本（Lua/notice），
        /// C# 只搭壳转发。ShowMenu 在热更完成后才会到达，拿到的必是最新脚本。
        /// 没有公告模块/开关关闭 = 静默跳过（运营侧不发公告是常态）。
        /// </summary>
        private async void TryShowLuaNotice()
        {
            if (noticeChecked) return;
            noticeChecked = true;

            var lua = Context.GetApplicationContext().GetService<LuaService>();
            if (lua == null) return;

            LuaTable notice;
            try { notice = lua.Require("notice")[0] as LuaTable; }
            catch (Exception e)
            {
                Debug.Log($"[GameFlow] 无 Lua 公告模块（可选）：{e.Message}");
                return;
            }
            if (notice == null || !notice.Get<bool>("show")) return;

            // 双语公告：英文环境优先取 *_en 字段，缺失回落中文——Lua 侧可以只写一种语言
            string title = PickLocalized(notice, "title");
            string body = PickLocalized(notice, "body");
            // Get<Action> 走 xLua 委托桥：反射模式在 Mono/编辑器可用；IL2CPP 需 CSharpCallLua 生成
            Action onClosed = notice.Get<Action>("on_closed");

            NoticeView view = await ui.Open<NoticeView>(noticeKey);
            if (view == null) return;
            noticeScreen = view;
            view.Bind(new NoticeViewModel(title, body, onClose: () =>
            {
                onClosed?.Invoke();
                CloseScreen(ref noticeScreen);
            }));
        }

        private async void ShowCharacterSelect()
        {
            CloseScreen(ref menuScreen);
            CharacterSelectView view = await ui.Open<CharacterSelectView>(selectKey);
            if (view == null) return;
            selectScreen = view;
            view.Bind(new CharacterSelectViewModel(characterIds,
                onConfirm: (p1, p2) => { if (pendingTraining) StartTraining(p1, p2); else StartBattle(p1, p2); },
                onBack: BackToMenuFromSelect));
        }

        private void ShowTrainingSelect()
        {
            pendingTraining = true; // 复用选人界面，确认后走训练装配
            ShowCharacterSelect();
        }

        private void BackToMenuFromSelect()
        {
            pendingTraining = false;
            CloseScreen(ref selectScreen);
            ShowMenu();
        }

        private async void StartBattle(string p1, string p2)
        {
            CloseScreen(ref selectScreen);
            p1Id = p1;
            p2Id = p2;

            ShowLoading().Forget();
            bool ok = await bootstrap.StartBattle(p1, p2);
            HideLoading();
            if (!ok) { ShowMenu(); return; } // 角色包加载失败：错误已打日志，退回菜单

            loop.Simulation.MatchEnded += OnMatchEnded;

            // 每场自动录制：确定性模拟下回放 = 每帧 6 字节的输入流
            recorder?.Stop();
            recorder = new ReplayRecorder(loop.Simulation, p1, p2);

            OpenHud(loop.Simulation);
        }

        // ---- 训练场 ----

        private async void StartTraining(string p1, string p2)
        {
            CloseScreen(ref selectScreen);
            ShowLoading().Forget();
            // 训练场不录回放（无限时长没有回放意义），也不订 MatchEnded（训练配置到不了终局）
            bool ok = await bootstrap.StartTraining(p1, p2, DummyPolicies.Idle);
            HideLoading();
            if (!ok) { pendingTraining = false; ShowMenu(); return; }

            trainingActive = true;
            OpenHud(loop.Simulation);
            Debug.Log("[Training] 训练场开始：F1 站桩 / F2 蹲姿 / F3 后走 / F4 简单CPU，Esc 退出");
        }

        private void Update()
        {
            if (onlineActive)
            {
                var okb = UnityEngine.InputSystem.Keyboard.current;
                if (okb != null && okb.escapeKey.wasPressedThisFrame) ExitOnline();
                return;
            }

            if (!trainingActive) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;

            if (kb.escapeKey.wasPressedThisFrame) { ExitTraining(); return; }
            if (kb.f1Key.wasPressedThisFrame) SetDummy(DummyPolicies.Idle, "站桩");
            if (kb.f2Key.wasPressedThisFrame) SetDummy(DummyPolicies.Crouch, "蹲姿");
            if (kb.f3Key.wasPressedThisFrame) SetDummy(DummyPolicies.WalkBack, "后走");
            if (kb.f4Key.wasPressedThisFrame) SetDummy(DummyPolicies.SimpleCpu, "简单CPU");
        }

        private void SetDummy(IDummyPolicy policy, string label)
        {
            if (bootstrap.TrainingDummy == null) return;
            bootstrap.TrainingDummy.Policy = policy;
            Debug.Log($"[Training] 假人行为 → {label}");
        }

        private void ExitTraining()
        {
            trainingActive = false;
            pendingTraining = false;
            CloseScreen(ref hudScreen);
            bootstrap.StopBattle();
            ShowMenu();
        }

        // ---- 在线对战（回滚 + Go 权威服务器）----

        private bool onlineActive;

        private async void StartOnlineBattle()
        {
            if (netBootstrap == null)
            {
                Debug.LogError("[GameFlow] 未拖入 NetworkBattleBootstrap：主菜单的在线入口无法工作。", this);
                ShowMenu();
                return;
            }

            CloseScreen(ref menuScreen);
            ShowLoading().Forget();
            bool ok = await netBootstrap.StartOnlineMatch();
            HideLoading();
            if (!ok) { ShowMenu(); return; } // 握手超时/加载失败：错误已打日志，退回菜单

            onlineActive = true;
            netBootstrap.Simulation.MatchEnded += OnOnlineMatchEnded;
            OpenHud(netBootstrap.Simulation); // HUD 绑【确认】模拟（身份稳定，仅落后回滚窗口若干帧）
            Debug.Log("[GameFlow] 在线对战开始：Esc 退出。");
        }

        private async void OnOnlineMatchEnded(int winner)
        {
            StopOnline();

            ResultView view = await ui.Open<ResultView>(resultKey);
            if (view == null) return;
            resultScreen = view;
            string winnerText = winner == 0
                ? Localization.Current.GetText("result.draw", "DRAW")
                : Localization.Current.GetFormattedText("result.wins", winner);
            view.Bind(new ResultViewModel(winnerText, onRematch: RematchOnline, onMenu: ExitOnlineToMenu));
        }

        private void RematchOnline()
        {
            CloseScreen(ref resultScreen);
            CloseScreen(ref hudScreen); // HUD 绑着旧确认模拟，随旧局一起关
            StartOnlineBattle();        // StartOnlineMatch 内部先 StopMatch 旧局
        }

        private void ExitOnlineToMenu()
        {
            CloseScreen(ref resultScreen);
            CloseScreen(ref hudScreen);
            StopOnline();
            ShowMenu();
        }

        // Esc 退出在线对战：关 HUD、停局、回菜单。
        private void ExitOnline()
        {
            CloseScreen(ref hudScreen);
            StopOnline();
            ShowMenu();
        }

        private void StopOnline()
        {
            onlineActive = false;
            if (netBootstrap == null) return;
            if (netBootstrap.Simulation != null)
                netBootstrap.Simulation.MatchEnded -= OnOnlineMatchEnded;
            netBootstrap.StopMatch();
        }

        // ---- 战斗内界面 ----

        private async void OpenHud(BattleSimulation sim)
        {
            BattleHudView view = await ui.Open<BattleHudView>(hudKey);
            if (view == null) return;
            hudScreen = view;
            view.Bind(new BattleHudViewModel(sim));
        }

        private async void OnMatchEnded(int winner)
        {
            loop.Simulation.MatchEnded -= OnMatchEnded;

            if (recorder != null)
            {
                recorder.Stop();
                ReplayFileStore.Save(recorder.Data);
                recorder = null;
            }

            ResultView view = await ui.Open<ResultView>(resultKey);
            if (view == null) return;
            resultScreen = view;
            string winnerText = winner == 0
                ? Localization.Current.GetText("result.draw", "DRAW")
                : Localization.Current.GetFormattedText("result.wins", winner);
            view.Bind(new ResultViewModel(winnerText, onRematch: Rematch, onMenu: BackToMenuFromResult));
        }

        // ---- 回放观看 ----

        private async void WatchLatestReplay()
        {
            ReplayData data = ReplayFileStore.LoadLatest();
            if (data == null)
            {
                Debug.Log("[GameFlow] 还没有回放：先打一场吧");
                return; // 停留在主菜单
            }

            CloseScreen(ref menuScreen);
            ShowLoading().Forget();
            bool ok = await bootstrap.StartReplay(data);
            HideLoading();
            if (!ok) { ShowMenu(); return; }

            replayEndFrame = data.FrameCount + 60; // 播完留 1 秒余韵再收场
            loop.Simulation.TickFinished += OnReplayTick;

            OpenHud(loop.Simulation);
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

        // ---- 本地化（Loxodon Localizations，语言表经 Addressables 热更）----

        private const string LanguagePrefsKey = "ftg.language";

        private static bool IsEnglish
            => Localization.Current.CultureInfo.TwoLetterISOLanguageName != "zh";

        /// <summary>
        /// 装载语言表提供器。语言码归一化为两字母（zh/en）：玩家上次选择优先，否则跟系统语言。
        /// 在热更完成后调用，语言表拉到的是最新版本。
        /// </summary>
        private async UniTask InitLocalization()
        {
            Localization loc = Localization.Current;
            string code = PlayerPrefs.GetString(LanguagePrefsKey, "");
            if (code.Length == 0)
                code = loc.CultureInfo.TwoLetterISOLanguageName == "zh" ? "zh" : "en";
            loc.CultureInfo = new CultureInfo(code);
            await loc.AddDataProvider(new AddressablesLocalizationDataProvider(AddressablesTextReader.Read));
        }

        private void ToggleLanguage()
        {
            string next = IsEnglish ? "zh" : "en";
            PlayerPrefs.SetString(LanguagePrefsKey, next);
            // 赋值 CultureInfo 触发框架 Refresh：提供器按新语言重装表，观察属性原地更新，
            // 所有挂 LocalizedTextMeshPro 的文本自动刷新，无需重开界面
            Localization.Current.CultureInfo = new CultureInfo(next);
        }

        /// <summary>Lua 表双语字段：英文环境优先 {field}_en，缺失回落中文主字段。</summary>
        private static string PickLocalized(LuaTable t, string field)
        {
            if (IsEnglish)
            {
                string en = t.Get<string>(field + "_en");
                if (!string.IsNullOrEmpty(en)) return en;
            }
            return t.Get<string>(field);
        }

        // ---- Loading ----

        private async UniTask ShowLoading()
        {
            loadingDismissed = false;
            loadingStatus = null;
            LoadingView view = await ui.Open<LoadingView>(loadingKey);
            if (view == null) return;
            if (loadingDismissed) { ui.Close(view); return; } // 装配抢先完成：开完即关
            loadingScreen = view;
            loadingView = view;
            if (loadingStatus != null) view.SetStatus(loadingStatus); // 补发攒下的状态
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

        private void CloseScreen(ref UIScreen screen)
        {
            if (screen != null) ui.Close(screen);
            screen = null;
        }
    }
}
