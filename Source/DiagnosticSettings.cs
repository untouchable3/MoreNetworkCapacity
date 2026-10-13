using System.Collections;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace MoreNetworkCapacity
{
    internal static class DiagnosticSettings
    {
        internal static void Add(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Diagnostics / 诊断");
            UIHelper gameHelper = group as UIHelper;
            UIComponent content = gameHelper == null ? null : gameHelper.self as UIComponent;
            if (content == null)
            {
                Logging.Warning("Diagnostic settings UI content is unavailable.");
                return;
            }
            UILabel status = AddText(content, "MNC-diagnostic-status", "");
            UpdateStatus(status);
            UIButton export = null;
            export = group.AddButton("Export diagnostic ZIP / 导出诊断包", delegate
            {
                if (DiagnosticExport.IsBusy) return;
                bool started = DiagnosticExport.Begin();
                UpdateStatus(status);
                if (started && export != null) export.StartCoroutine(WaitForExport(export, status));
            }) as UIButton;
            group.AddButton("Open diagnostics folder / 打开诊断目录", delegate
            {
                DiagnosticExport.OpenFolder();
                UpdateStatus(status);
            });
            AddText(content, "MNC-diagnostic-sharing",
                "Sharing / 分享说明\n"
                + "Local ZIP only; nothing is uploaded automatically.\n"
                + "Review local paths and save names before sharing.\n"
                + "仅导出本地 ZIP，不会自动上传。\n"
                + "分享前请检查日志中的本地路径和存档名称。");
            if (DiagnosticExport.IsBusy && export != null) export.StartCoroutine(WaitForExport(export, status));
        }

        private static UILabel AddText(UIComponent content, string name, string text)
        {
            UILabel label = content.AddUIComponent<UILabel>();
            label.name = name;
            label.autoSize = false;
            label.width = content.width > 40f ? content.width - 20f : 520f;
            label.autoHeight = true;
            label.wordWrap = true;
            label.textScale = 0.9f;
            label.padding = new RectOffset(0, 0, 4, 4);
            label.text = text;
            return label;
        }

        private static void UpdateStatus(UILabel status)
        {
            if (status != null) status.text = "Status / 状态\n" + DiagnosticExport.LastMessage;
        }

        private static IEnumerator WaitForExport(UIButton button, UILabel status)
        {
            button.isEnabled = false;
            while (DiagnosticExport.IsBusy) yield return null;
            if (button != null) button.isEnabled = true;
            UpdateStatus(status);
        }
    }
}
