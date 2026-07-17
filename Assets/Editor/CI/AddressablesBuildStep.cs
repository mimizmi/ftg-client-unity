using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FTG.EditorTools
{
    /// <summary>
    /// Player 构建前自动出 Addressables 内容——没有这步，打出来的包缺所有
    /// Addressables 资产（UI/角色/帧数据全空），是最常见的出包摔坑点。
    /// 【演示包模式】（菜单 FG/Build 里勾选，或 CI 批处理自动开）：
    /// 全组强制本地路径 + 关闭 remote catalog——外发的演示包必须离线自足，
    /// 不能指着开发机的 127.0.0.1 Hosting；热更演示只在本地编辑器环境做。
    /// </summary>
    public sealed class AddressablesBuildStep : IPreprocessBuildWithReport
    {
        private const string ForceLocalMenu = "FG/Build/演示包模式（Addressables 全本地化）";
        private const string ForceLocalPref = "FTG.Build.ForceLocalAddressables";

        public int callbackOrder => -100; // 赶在场景处理之前把内容备好

        [MenuItem(ForceLocalMenu)]
        private static void ToggleForceLocal()
        {
            bool now = !EditorPrefs.GetBool(ForceLocalPref, false);
            EditorPrefs.SetBool(ForceLocalPref, now);
            Debug.Log($"[AddressablesBuildStep] 演示包模式 = {(now ? "开（全本地化出包）" : "关（按 Groups 配置出包）")}");
        }

        [MenuItem(ForceLocalMenu, true)]
        private static bool ToggleForceLocalValidate()
        {
            Menu.SetChecked(ForceLocalMenu, EditorPrefs.GetBool(ForceLocalPref, false));
            return true;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[AddressablesBuildStep] 工程无 Addressables 设置，跳过内容构建");
                return;
            }

            // CI 批处理天然是演示包语义（构建机上没有 Hosting 服务）
            if (Application.isBatchMode || EditorPrefs.GetBool(ForceLocalPref, false))
                ForceLocal(settings);

            Debug.Log("[AddressablesBuildStep] 构建 Addressables 内容…");
            AddressableAssetSettings.BuildPlayerContent();
        }

        private static void ForceLocal(AddressableAssetSettings settings)
        {
            settings.BuildRemoteCatalog = false;
            foreach (AddressableAssetGroup group in settings.groups)
            {
                var schema = group != null ? group.GetSchema<BundledAssetGroupSchema>() : null;
                if (schema == null) continue;
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            }
            Debug.Log("[AddressablesBuildStep] 演示包模式：全组已切本地路径，remote catalog 关闭（包体离线自足）。" +
                      "注意：此改动会落在 Addressables 设置资产上，出完包用 git 还原或重新配 Remote。");
        }
    }
}
