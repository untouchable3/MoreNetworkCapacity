using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace MoreNetworkCapacity.Diagnostics
{
    public sealed class InspectorLoading : LoadingExtensionBase
    {
        private const string Setting = "MoreNetworkCapacity.NetworkIdInspector.Enabled";
        private static bool cityLoaded;
        private static GameObject host, colorHost;
        private const string ColorSetting = "MoreNetworkCapacity.RoadColorAnalyzer.Enabled";
        internal static bool ColorEnabled { get { return PlayerPrefs.GetInt(ColorSetting, 0) != 0; } }

        internal static void SetColorEnabled(bool value)
        {
            PlayerPrefs.SetInt(ColorSetting, value ? 1 : 0);
            PlayerPrefs.Save();
            UpdateColorHost(value);
        }

        private static void UpdateColorHost(bool enabled)
        {
            if (cityLoaded && enabled)
            {
                if (colorHost != null) return;
                colorHost = new GameObject("MNC Road Color Analyzer");
                colorHost.AddComponent<RoadColorAnalyzer>();
            }
            else if (colorHost != null)
            {
                colorHost.SetActive(false);
                UnityEngine.Object.Destroy(colorHost);
                colorHost = null;
            }
        }

        internal static bool Enabled
        {
            get { return PlayerPrefs.GetInt(Setting, 0) != 0; }
        }

        internal static void SetEnabled(bool enabled)
        {
            PlayerPrefs.SetInt(Setting, enabled ? 1 : 0);
            PlayerPrefs.Save();
            UpdateHost(enabled);
        }

        public override void OnLevelLoaded(LoadMode mode)
        {
            cityLoaded = mode == LoadMode.NewGame || mode == LoadMode.LoadGame ||
                mode == LoadMode.NewGameFromScenario;
            UpdateHost(Enabled);
            UpdateColorHost(ColorEnabled);
        }

        public override void OnLevelUnloading() { LeaveCity(); }
        public override void OnReleased() { LeaveCity(); }

        internal static void LeaveCity()
        {
            cityLoaded = false;
            UpdateHost(false);
            UpdateColorHost(false);
            RoadColorAnalyzer.Report = null;
        }

        private static void UpdateHost(bool enabled)
        {
            if (cityLoaded && enabled)
            {
                if (host != null) return;
                host = new GameObject("MNC Network ID Inspector");
                host.AddComponent<Inspector>();
            }
            else if (host != null)
            {
                host.SetActive(false);
                UnityEngine.Object.Destroy(host);
                host = null;
            }
        }
    }

    internal enum InspectorMode { None, Stop, Road }

    internal sealed class InspectorPickTool : ToolBase
    {
        internal InstanceID Selection;
        private InstanceID hover;

        internal System.Action<InstanceID, Vector3> Picked;
        internal System.Action Undo;
        internal System.Func<string> CursorHint;
        internal InstanceID Pinned;
        internal static event System.Action ChangingPicker;
        internal static void ReleaseOtherPicker() { if (ChangingPicker != null) ChangingPicker(); }
        private InstanceID Hit(out Vector3 position)
        {
            InstanceID id = InstanceID.Empty;
            position = Vector3.zero;
            Camera camera = Camera.main;
            if (camera == null) return id;
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            RaycastInput input = new RaycastInput(ray, camera.farClipPlane);
            input.m_netService = new RaycastService(ItemClass.Service.Road, ItemClass.SubService.None, ItemClass.Layer.Default);
            input.m_netService2 = input.m_netService;
            input.m_ignoreNodeFlags = NetNode.Flags.None;
            input.m_ignoreSegmentFlags = NetSegment.Flags.None;
            input.m_ignoreTerrain = true;
            RaycastOutput hit;
            if (RayCast(input, out hit))
            {
                position = hit.m_hitPos;
                if (hit.m_netNode != 0) id.NetNode = hit.m_netNode;
                else if (hit.m_netSegment != 0) id.NetSegment = hit.m_netSegment;
            }
            return id;
        }

        internal void Clear()
        {
            Pinned = Selection = hover = InstanceID.Empty;
        }

        protected override void OnToolGUI(Event e)
        {
            if (UIView.HasModalInput() || UIView.IsInsideUI())
            {
                hover = InstanceID.Empty;
                return;
            }
            if (Undo != null && e.button == 1 && (e.type == EventType.MouseDown || e.type == EventType.MouseUp))
            {
                bool released = e.type == EventType.MouseUp;
                e.Use();
                if (released) Undo();
                return;
            }
            if (e.type == EventType.Repaint && CursorHint != null)
            {
                var content = new GUIContent(CursorHint());
                Vector2 size = GUI.skin.box.CalcSize(content) + new Vector2(12, 6);
                Vector2 mouse = e.mousePosition;
                float x = Mathf.Clamp(mouse.x + 18, 0, Mathf.Max(0, Screen.width - size.x));
                float y = mouse.y + 22 + size.y > Screen.height ? Mathf.Max(0, mouse.y-size.y-6) : mouse.y+22;
                GUI.Box(new Rect(x,y,size.x,size.y),content);
            }
            bool click = e.type == EventType.MouseDown && e.button == 0;
            if (click || e.type == EventType.Repaint)
            {
                Vector3 position;
                hover = Hit(out position);
                if (click && hover.Index != 0)
                {
                    if (Picked != null) Picked(hover, position);
                    else Selection = hover;
                }
                if (click) e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0) e.Use();
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            if (Pinned != Selection) Highlight(cameraInfo, Pinned, new Color32(70, 160, 255, 245));
            if (hover != Selection && hover != Pinned) Highlight(cameraInfo, hover, new Color32(70, 210, 255, 155));
            Highlight(cameraInfo, Selection, new Color32(255, 192, 48, 245));
        }

        internal static void Highlight(RenderManager.CameraInfo camera, InstanceID id, Color color)
        {
            if (id.Index == 0) return;
            NetManager net = Singleton<NetManager>.instance;
            if (id.Type == InstanceType.NetSegment)
            {
                ushort segment = id.NetSegment;
                NetSegment[] buffer = net.m_segments.m_buffer;
                if (segment < buffer.Length && (buffer[segment].m_flags & NetSegment.Flags.Created) != 0)
                    NetTool.RenderOverlay(camera, ref buffer[segment], color, color);
            }
            else if (id.Type == InstanceType.NetNode)
            {
                ushort node = id.NetNode;
                NetNode[] buffer = net.m_nodes.m_buffer;
                if (node >= buffer.Length || (buffer[node].m_flags & NetNode.Flags.Created) == 0) return;
                Vector3 pos = buffer[node].m_position;
                Singleton<RenderManager>.instance.OverlayEffect.DrawCircle(camera, color, pos,
                    Mathf.Clamp(buffer[node].m_bounds.size.magnitude, 10f, 36f), pos.y - 1f, pos.y + 1f, true, true);
            }
        }
    }
    [HarmonyLib.HarmonyPatch(typeof(ToolManager), "EndOverlayImpl")]
    internal static class StopHoverOverlay
    {
        internal static InstanceID Hovered;
        private static void Postfix(RenderManager.CameraInfo cameraInfo)
        {
            if (Hovered.Index != 0)
                InspectorPickTool.Highlight(cameraInfo, Hovered, new Color32(255, 192, 48, 245));
        }
    }

    internal static class Readout
    {
        internal static string Number(ushort id, int vanillaLength)
        {
            if (id == 0) return UiLanguage.T("无", "None");
            return id.ToString() + (id >= vanillaLength ? UiLanguage.T("（高编号）", " (high ID)") : UiLanguage.T("（常规）", " (standard)"));
        }

        internal static string Node(ushort id)
        {
            string result = "NetNode " + Number(id, Capacity.VanillaNodeCapacity);
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            if (id == 0 || id >= nodes.Length) return result + UiLanguage.T(" · 超出当前节点池", " · Outside node pool");
            NetNode node = nodes[id];
            if ((node.m_flags & NetNode.Flags.Created) == 0) return result + UiLanguage.T(" · 未使用", " · Unused");
            if (node.m_transportLine != 0) return result + UiLanguage.T(" · 线路 ID ", " · Line ID ") + node.m_transportLine;
            return result + ((node.m_flags & NetNode.Flags.Junction) != 0 ? UiLanguage.T(" · 路口", " · Junction") : UiLanguage.T(" · 网络节点", " · Network node"));
        }

        internal static string Segment(ushort id)
        {
            string result = "NetSegment " + Number(id, Capacity.VanillaSegmentCapacity);
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            if (id == 0 || id >= segments.Length) return result + UiLanguage.T(" · 超出当前路段池", " · Outside segment pool");
            NetSegment segment = segments[id];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0) return result + UiLanguage.T(" · 未使用", " · Unused");
            return result + UiLanguage.T("\n端点 ", "\nEndpoints ") + Number(segment.m_startNode, Capacity.VanillaNodeCapacity) + " → " + Number(segment.m_endNode, Capacity.VanillaNodeCapacity);
        }

        internal static string Selected(InstanceID id)
        {
            switch (id.Type)
            {
                case InstanceType.NetNode: return Node(id.NetNode);
                case InstanceType.NetSegment: return Segment(id.NetSegment);
                case InstanceType.Building: return UiLanguage.T("建筑 ID ", "Building ID ") + id.Building;
                case InstanceType.Vehicle: return UiLanguage.T("车辆 ID ", "Vehicle ID ") + Number(id.Vehicle, 16384);
                case InstanceType.TransportLine: return UiLanguage.T("线路 ID ", "Line ID ") + id.TransportLine;
                default: return id.Index == 0 ? string.Empty : id.Type + " ID " + id.Index;
            }
        }
    }

    internal sealed class Inspector : MonoBehaviour
    {
        private InspectorPickTool tool;
        private ToolBase previousTool;
        private int stopIndex;

        private UIPanel panel;
        private UILabel title, stopHint, selectedLabel, pickedLabel;
        private UIButton stopButton, roadButton, foldButton;
        private InspectorMode mode;
        private bool collapsed, chinese;
        private float nextRefresh;


        private void Start()
        {
            panel = (UIPanel)UIView.GetAView().AddUIComponent(typeof(UIPanel));
            panel.name = "MNCNetworkIdInspectorPanel";
            panel.backgroundSprite = "MenuPanel2";
            panel.size = new Vector2(432, 190);
            panel.relativePosition = new Vector3(16, 90);
            UIDragHandle drag = panel.AddUIComponent<UIDragHandle>();
            drag.target = panel;
            drag.size = new Vector2(314, 34);
            drag.relativePosition = Vector3.zero;
            title = Label(8);
            stopHint = Label(82);
            stopHint.autoSize = false;
            stopHint.wordWrap = true;
            stopHint.size = new Vector2(406, 40);
            pickedLabel = Label(124);
            selectedLabel = Label(163);
            stopButton = Button(12, 39, 174);
            roadButton = Button(195, 39, 224);
            foldButton = Button(324, 3, 95);
            stopButton.eventClicked += delegate { Toggle(InspectorMode.Stop); };
            roadButton.eventClicked += delegate { Toggle(InspectorMode.Road); };
            foldButton.eventClicked += delegate
            {
                collapsed = !collapsed;
                ReleaseTool();
                panel.height = collapsed ? 38 : 190;
                stopButton.isVisible = roadButton.isVisible = !collapsed;
                selectedLabel.isVisible = pickedLabel.isVisible = !collapsed;
                RefreshControls();
            };
            InspectorPickTool.ChangingPicker += StopRoadPicker;
            RefreshControls();
        }

        private void StopRoadPicker()
        {
            if (mode != InspectorMode.Road) return;
            ReleaseTool();
            RefreshControls();
        }

        private UIButton Button(float x, float y, float width)
        {
            UIButton button = panel.AddUIComponent<UIButton>();
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.textScale = 0.85f;
            button.size = new Vector2(width, 30);
            button.relativePosition = new Vector3(x, y);
            return button;
        }

        private UILabel Label(float y)
        {
            UILabel label = panel.AddUIComponent<UILabel>();
            label.isInteractive = false;
            label.textScale = 0.88f;
            label.relativePosition = new Vector3(13, y);
            return label;
        }

        private void Toggle(InspectorMode requested)
        {
            if (mode == requested)
                ReleaseTool();
            else if (requested == InspectorMode.Stop)
            {
                ReleaseTool();
                if (tool != null) tool.Clear();
                mode = InspectorMode.Stop;
            }
            else
            {
                ToolController controller = ToolsModifierControl.toolController;
                if (controller == null) return;
                InspectorPickTool.ReleaseOtherPicker();
                ReleaseTool();
                if (controller.CurrentTool != tool || tool == null) previousTool = controller.CurrentTool;
                if (tool == null) tool = controller.gameObject.AddComponent<InspectorPickTool>();
                tool.Clear();
                tool.CursorHint = delegate
                {
                    return UiLanguage.T("左键：查看节点或路段   右键：退出",
                        "Left-click: inspect node or segment   Right-click: exit");
                };
                tool.Undo = StopRoadPicker;
                mode = requested;
                controller.CurrentTool = tool;
            }
            RefreshControls();
        }

        private void ReleaseTool()
        {
            StopHoverOverlay.Hovered = InstanceID.Empty;
            if (mode == InspectorMode.Road && tool != null)
            {
                tool.CursorHint = null;
                tool.Undo = null;
                ToolController controller = ToolsModifierControl.toolController;
                if (controller != null && controller.CurrentTool == tool)
                    controller.CurrentTool = previousTool != null ? previousTool : controller.GetComponent<DefaultTool>();
            }
            mode = InspectorMode.None;
            previousTool = null;
        }

        private void RefreshControls()
        {
            chinese = UiLanguage.Chinese;
            title.text = UiLanguage.T("路网 ID 查看器", "Network ID inspector");
            stopButton.text = UiLanguage.T("站点", "Stops");
            roadButton.text = UiLanguage.T("节点/路段", "Nodes / segments");
            stopHint.isVisible = !collapsed && mode == InspectorMode.Stop;
            stopHint.text = UiLanguage.T("选择对应的线路工具，鼠标移到相应位置即可查看",
                "Choose the matching transit line tool, then hover over the stop.");
            pickedLabel.relativePosition = new Vector3(13, mode == InspectorMode.Stop ? 124 : 82);
            selectedLabel.relativePosition = new Vector3(13, mode == InspectorMode.Stop ? 144 : 128);
            foldButton.text = collapsed ? UiLanguage.T("展开", "Expand") : UiLanguage.T("收起", "Collapse");
            nextRefresh = 0;
        }

        private static InstanceID HoverStop(out int stopIndex)
        {
            stopIndex = -1;
            InstanceID id = InstanceID.Empty;
            if (UIView.HasModalInput() || UIView.IsInsideUI()) return id;
            Camera camera = Camera.main;
            ToolController controller = ToolsModifierControl.toolController;
            if (camera == null || controller == null || !Singleton<TransportManager>.exists || !Singleton<NetManager>.exists) return id;
            var transport = controller.CurrentTool as TransportTool;
            int mask = transport != null && transport.m_prefab != null ? 1 << (int)transport.m_prefab.m_transportType : -1;
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            TransportManager manager = Singleton<TransportManager>.instance;
            Vector3 point;
            ushort line;
            int segmentIndex;
            if (manager.RayCast(ray, camera.farClipPlane, mask, out point, out line, out stopIndex, out segmentIndex)
                && stopIndex >= 0 && line != 0 && line < manager.m_lines.m_buffer.Length)
            {
                ushort stop = manager.m_lines.m_buffer[line].GetStop(stopIndex);
                NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
                if (stop != 0 && stop < nodes.Length && nodes[stop].m_transportLine == line &&
                    (nodes[stop].m_flags & NetNode.Flags.Created) != 0) id.NetNode = stop;
            }
            return id;
        }

        private void Update()
        {
            if (panel == null) return;
            if (mode == InspectorMode.Road && (tool == null || ToolsModifierControl.toolController == null || ToolsModifierControl.toolController.CurrentTool != tool))
            {
                ReleaseTool();
                RefreshControls();
            }
            if (chinese != UiLanguage.Chinese) RefreshControls();
            if (collapsed || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.1f;
            selectedLabel.text = Readout.Selected(WorldInfoPanel.GetCurrentInstanceID());
            InstanceID picked = tool == null ? InstanceID.Empty : tool.Selection;
            if (mode == InspectorMode.Stop)
                picked = StopHoverOverlay.Hovered = HoverStop(out stopIndex);
            if (picked.Index != 0)
            {
                string stop = mode != InspectorMode.Stop || stopIndex < 0 ? "" : UiLanguage.T("第 ", "Stop ") + (stopIndex + 1) + UiLanguage.T(" 站  ", "  ");
                string label = mode == InspectorMode.Stop ? UiLanguage.T("悬停站点  ", "Hovered stop  ") : UiLanguage.T("地图点选  ", "Map selection  ");
                pickedLabel.text = label + stop + Readout.Selected(picked);
            }
            else pickedLabel.text = string.Empty;
        }

        private void OnDisable() { ReleaseTool(); }
        private void OnDestroy()
        {
            InspectorPickTool.ChangingPicker -= StopRoadPicker;
            ReleaseTool();
            if (tool != null) UnityEngine.Object.Destroy(tool);
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
        }
    }
}
