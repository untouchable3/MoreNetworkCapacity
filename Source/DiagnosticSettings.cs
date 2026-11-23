using System;
using System.Collections;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace MoreNetworkCapacity
{
    internal sealed class DiagnosticSettings
    {
        private readonly UIComponent root;
        private UIPanel page;
        private UIButton diagnostics, compatibility;
        private UITabstrip tabs;
        private UITabContainer pages;
        private readonly UIPanel[] bodies = new UIPanel[2];
        private string selectedMod;
        private bool includeSave = true;

        private DiagnosticSettings(UIComponent root) { this.root = root; }

        internal static void Add(UIHelperBase helper)
        {
            UIHelper group = helper.AddGroup("More Network Capacity   " + BuildInfo.Version) as UIHelper;
            UIComponent root = group == null ? null : group.self as UIComponent;
            if (root == null) { Logging.Warning("MNC settings UI content unavailable."); return; }
            new DiagnosticSettings(root).Build();
        }

        private void Build()
        {
            UIHelper controls = new UIHelper(root);
            controls.AddDropdown("Language / 语言", new string[] { "System / 跟随游戏", "简体中文", "English" },
                UiLanguage.Choice, delegate(int value)
                {
                    if (value < 0 || value == UiLanguage.Choice) return;
                    UiLanguage.Choice = value;
                    ShowPage();
                });
            float width = root.width > 100 ? root.width - 8 : 560;
            tabs = root.AddUIComponent<UITabstrip>();
            tabs.size = new Vector2(width, 38);
            tabs.closeOnReclick = false;
            pages = root.AddUIComponent<UITabContainer>();
            pages.size = new Vector2(width, 320);
            tabs.tabPages = pages;
            diagnostics = AddTab("Diagnostics");
            compatibility = AddTab("Compatibility");
            tabs.selectedIndex = 0;
            tabs.eventSelectedIndexChanged += delegate { ShowPage(); };
            ShowPage();
        }

        private UIButton AddTab(string name)
        {
            UIButton tab = tabs.AddTab(name);
            tab.autoSize = false;
            tab.size = new Vector2(Mathf.Min(200, tabs.width / 2), tabs.height);
            tab.normalBgSprite = "SubBarButtonBase";
            tab.hoveredBgSprite = "SubBarButtonBaseHovered";
            tab.focusedBgSprite = "SubBarButtonBaseFocused";
            tab.pressedBgSprite = "SubBarButtonBasePressed";
            return tab;
        }
        private static UIPanel Panel(UIComponent parent, LayoutDirection direction)
        {
            UIPanel result = parent.AddUIComponent<UIPanel>();
            result.width = parent.width > 100 ? parent.width - 8 : 560;
            result.autoLayoutDirection = direction;
            result.autoLayoutPadding = new RectOffset(0, 6, 2, 4);
            result.autoLayout = true;
            result.autoFitChildrenVertically = true;
            return result;
        }

        private void ShowPage()
        {
            if (compatibility == null || tabs.selectedIndex < 0) return;
            diagnostics.text = UiLanguage.T("故障排查", "Diagnostics");
            compatibility.text = UiLanguage.T("兼容状态", "Compatibility");
            int index = tabs.selectedIndex;
            UIPanel tabPage = (UIPanel)pages.components[index];
            if (bodies[index] != null)
            {
                bodies[index].Hide();
                tabPage.RemoveUIComponent(bodies[index]);
                UnityEngine.Object.Destroy(bodies[index].gameObject);
            }
            page = bodies[index] = Panel(tabPage, LayoutDirection.Vertical);
            page.relativePosition = Vector3.zero;
            page.eventSizeChanged += delegate(UIComponent sender, Vector2 size)
            {
                if (sender == bodies[index] && tabs.selectedIndex == index)
                    pages.height = Mathf.Max(80, size.y + 12);
            };

            if (index == 1) ShowCompatibility();
            else ShowDiagnostics(new UIHelper(page));
            pages.height = Mathf.Max(80, page.height + 12);
        }
        private UILabel Text(string value, bool heading)
        {
            UILabel label = page.AddUIComponent<UILabel>();
            label.autoSize = false;
            label.width = Mathf.Max(100, page.width - 10);
            label.autoHeight = true;
            label.wordWrap = true;
            label.textScale = heading ? 1.05f : 0.88f;
            label.textColor = heading ? (Color32)Color.white : new Color32(215, 225, 232, 255);
            label.padding = new RectOffset(0, 4, heading ? 8 : 2, 2);
            label.text = value;
            return label;
        }

        private void ShowDiagnostics(UIHelper controls)
        {
            Text(UiLanguage.T("对象检查", "Object inspection"), true);
            controls.AddCheckbox(UiLanguage.T("启用路网 ID 查看器", "Enable network ID inspector"),
                Diagnostics.InspectorLoading.Enabled, Diagnostics.InspectorLoading.SetEnabled);
            controls.AddCheckbox(UiLanguage.T("启用道路颜色分析器", "Enable road color analyzer"),
                Diagnostics.InspectorLoading.ColorEnabled, Diagnostics.InspectorLoading.SetColorEnabled);
            Text(UiLanguage.T("诊断报告", "Diagnostic report"), true);
            controls.AddCheckbox(UiLanguage.T("包含最近一次的存档", "Include the most recent save"), includeSave,
                delegate(bool value) { includeSave = value; });
            UILabel status = Text(DiagnosticExport.StatusMessage(UiLanguage.Chinese), false);
            UIPanel actions = Panel(page, LayoutDirection.Horizontal);
            UIHelper buttons = new UIHelper(actions);
            float buttonWidth = Mathf.Floor((actions.width - 12f) / 2f);
            UIButton export = null;
            export = (UIButton)buttons.AddButton(UiLanguage.T("导出诊断包", "Export diagnostic ZIP"), delegate
            {
                if (DiagnosticExport.Begin(includeSave)) export.StartCoroutine(WaitForExport(export, status));
                status.text = DiagnosticExport.StatusMessage(UiLanguage.Chinese);
            });
            UIButton open = (UIButton)buttons.AddButton(UiLanguage.T("打开诊断目录", "Open diagnostics folder"), delegate
            {
                DiagnosticExport.OpenFolder();
                status.text = DiagnosticExport.StatusMessage(UiLanguage.Chinese);
            });
            export.autoSize = open.autoSize = false;
            export.width = open.width = buttonWidth;
            if (DiagnosticExport.IsBusy) export.StartCoroutine(WaitForExport(export, status));
        }

        private static IEnumerator WaitForExport(UIButton button, UILabel status)
        {
            button.isEnabled = false;
            while (DiagnosticExport.IsBusy) yield return null;
            if (button != null) button.isEnabled = true;
            if (status != null) status.text = DiagnosticExport.StatusMessage(UiLanguage.Chinese);
        }

        private void ShowCompatibility()
        {
            Text(UiLanguage.T("运行时兼容状态", "Runtime compatibility"), true);
            CompatibilityStatus snapshot;
            try { snapshot = new CompatibilityStatus(); }
            catch (Exception error)
            {
                Text(UiLanguage.T("读取失败  ", "Could not read status  ") + error.Message, false);
                Logging.Warning("Compatibility status query failed: " + error);
                return;
            }
            TableRow( UiLanguage.T("模组", "Mod"), UiLanguage.T("版本", "Version"),
                UiLanguage.T("状态", "State"), UiLanguage.T("直接补丁", "Patches"), true);
            UIPanel openDetail = null;
            UIButton openRow = null;
            foreach (string entry in snapshot.Names)
            {
                string name = entry;
                UIButton row = TableRow( name, snapshot.Version(name), snapshot.PluginState(name),
                    snapshot.PatchCount(name), false);
                UIPanel detail = Panel(page, LayoutDirection.Vertical);
                detail.Hide();
                row.eventClicked += delegate
                {
                    bool opening = !detail.isVisible;
                    if (openDetail != null)
                    {
                        openDetail.Hide();
                        openRow.normalBgSprite = "ListItemHover";
                        openRow.color = RowColor;
                    }
                    selectedMod = opening ? name : null;
                    openDetail = opening ? detail : null;
                    openRow = opening ? row : null;
                    if (opening) ExpandRow(row, detail, name, snapshot);
                };
                if (name == selectedMod)
                {
                    openDetail = detail;
                    openRow = row;
                    ExpandRow(row, detail, name, snapshot);
                }
            }
        }

        private static readonly Color32 RowColor = new Color32(115, 125, 138, 255);

        private void ExpandRow(UIButton row, UIPanel detail, string name, CompatibilityStatus snapshot)
        {
            row.normalBgSprite = "ListItemHighlight";
            row.color = Color.white;
            detail.Show();
            if (detail.childCount != 0) return;
            string info = snapshot.Describe(name);
            if (info.Length > 0)
            {
                UILabel note = detail.AddUIComponent<UILabel>();
                note.autoSize = false;
                note.width = detail.width - 12;
                note.autoHeight = true;
                note.wordWrap = true;
                note.padding = new RectOffset(12, 0, 4, 4);
                note.textScale = 0.88f;
                note.text = info;
            }
            string[] methods = snapshot.PatchedMethods(name);
            if (methods.Length == 0) return;
            UIListBox list = detail.AddUIComponent<UIListBox>();
            list.width = detail.width - 8;
            list.itemHeight = 24;
            list.height = Math.Min(6, methods.Length) * list.itemHeight;
            list.textScale = 0.85f;
            list.itemPadding = new RectOffset(12, 4, 3, 0);
            list.itemHover = "ListItemHover";
            list.itemHighlight = "ListItemHighlight";
            list.items = methods;
            if (methods.Length > 6)
                list.eventMouseWheel += delegate(UIComponent sender, UIMouseEventParameter e) { e.Use(); };
            list.tooltip = UiLanguage.T("MNC 直接挂接的方法，可滚动查看", "Methods directly patched by MNC. Scroll to view.");
        }

        private UIButton TableRow(string name, string version, string state, string count, bool header)
        {
            UIComponent row;
            UIButton button = null;
            if (header) row = page.AddUIComponent<UIPanel>();
            else
            {
                button = page.AddUIComponent<UIButton>();
                button.normalBgSprite = "ListItemHover";
                button.hoveredBgSprite = "ListItemHighlight";
                button.color = RowColor;
                row = button;
            }
            row.size = new Vector2(page.width - 10, header ? 28 : 33);
            float width = row.width;
            Cell(row, name, 0, width * 0.46f);
            Cell(row, version, width * 0.46f, width * 0.23f);
            Cell(row, state, width * 0.69f, width * 0.18f);
            Cell(row, count, width * 0.87f, width * 0.13f);
            return button;
        }

        private static void Cell(UIComponent row, string value, float x, float width)
        {
            UILabel cell = row.AddUIComponent<UILabel>();
            cell.isInteractive = false;
            cell.autoSize = false;
            cell.size = new Vector2(width - 6, row.height);
            cell.relativePosition = new Vector3(x + 5, 0);
            cell.verticalAlignment = UIVerticalAlignment.Middle;
            cell.textScale = 0.86f;
            cell.text = value;
            cell.tooltip = value;
        }
    }
}
