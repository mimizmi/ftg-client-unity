using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Loxodon.Framework.Localizations;
using UnityEngine;

namespace Domain.Service.Localization
{
    /// <summary>
    /// Loxodon 本地化的 Addressables 数据提供器（IDataProvider 是框架的官方扩展点，
    /// 官方自带 Resources / AssetBundle 两种来源，这里补上 Addressables 来源）。
    /// 语言表为 Loxodon 标准 XML 格式，地址 Locale/{语言码}——与帧数据/Lua 同一条
    /// catalog 管线，翻译文案随热更即时生效，不发版。
    /// 与官方 DefaultDataProvider 的差异：不做 default → zh → zh-CN 三级级联探测——
    /// 表是项目自己的（只有 Locale/zh 与 Locale/en），语言码在流程入口已归一化为
    /// 两字母码，探测不存在的地址只会白刷警告日志。
    /// 切语言：Localization.Current.CultureInfo 赋值触发 Refresh，框架回调本提供器
    /// 按新语言重装表，观察属性原地更新，挂 LocalizedTextMeshPro 的文本自动刷新。
    /// </summary>
    public sealed class AddressablesLocalizationDataProvider : IDataProvider
    {
        private readonly Func<string, string> readText;
        private readonly IDocumentParser parser = new XmlDocumentParser();

        public AddressablesLocalizationDataProvider(Func<string, string> readText)
            => this.readText = readText;

        public Task<Dictionary<string, object>> Load(CultureInfo cultureInfo)
        {
            var dict = new Dictionary<string, object>();
            string code = cultureInfo.TwoLetterISOLanguageName;
            string text = readText($"Locale/{code}");
            if (text == null)
                return Task.FromResult(dict); // 表缺失：空表返回，界面保留 prefab 原文

            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    foreach (KeyValuePair<string, object> kv in parser.Parse(stream, cultureInfo))
                        dict[kv.Key] = kv.Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Localization] 语言表 \"Locale/{code}\" 解析失败：{e.Message}");
            }
            return Task.FromResult(dict);
        }
    }
}
