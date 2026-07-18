using System;
using Loxodon.Framework.Localizations;
using TMPro;
using UnityEngine;

namespace Domain.UI
{
    /// <summary>
    /// Loxodon 本地化的 TMP 适配组件：官方包只带 UGUI Text 版（TMP 版在未导入的
    /// loxodon-framework-textmeshpro 扩展包里），按框架预留的 AbstractLocalized
    /// 扩展点补齐，与官方实现同构。
    /// 用法：挂在带 TMP_Text 的节点上填 key（如 menu.start），切语言自动刷新。
    /// </summary>
    [AddComponentMenu("Loxodon/Localization/LocalizedTextMeshPro")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    public sealed class LocalizedTextMeshPro : AbstractLocalized<TMP_Text>
    {
        protected override void OnValueChanged(object sender, EventArgs e)
        {
            object v = this.value.Value;
            if (v != null) this.target.text = v.ToString(); // 表未装载/缺词条：保留 prefab 原文
        }
    }
}
