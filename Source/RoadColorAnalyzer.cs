using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace MoreNetworkCapacity.Diagnostics
{
    internal sealed class RoadColorAnalyzer : MonoBehaviour
    {
        internal static string Report;
        private const string IssuesUrl="https://github.com/untouchable3/MoreNetworkCapacity/issues";
        private UIPanel panel;
        private UILabel title, summary, markHint, detail;
        private UILabel helpText;
        private UIButton help;
        private bool showingHelp;
        private UIButton fold, singleButton, compareButton, generate, clear, fields, abnormal, restored;
        private UIListBox rows;
        private RoadColorSample a, b;
        private InspectorPickTool picker;
        private ToolBase previous;
        private bool picking;
        private InstanceID firstId, secondId;
        private bool comparison, folded, chinese;
        private string message;
        private bool onlyDifferences=true;
        private float nextPoll;

        private void Start()
        {
            var view=UIView.GetAView();
            panel=(UIPanel)view.AddUIComponent(typeof(UIPanel));
            panel.name="MNCRoadColorAnalyzer";
            panel.backgroundSprite="MenuPanel2";
            panel.size=new Vector2(Mathf.Min(660,view.fixedWidth-20),424);
            panel.relativePosition=new Vector3(Mathf.Max(0,Mathf.Min(460,view.fixedWidth-panel.width-8)),90);
            title=Label(8,26);
            help=Button(panel.width-166,4,74,delegate {
                showingHelp=!showingHelp;
                Draw();
            });
            helpText=Label(46,370);
            fold=Button(panel.width-84,4,74,delegate {
                StopPicking();
                folded=!folded;
                Draw();
            });
            float w=(panel.width-48)/5;
            singleButton=Button(8,38,w,delegate { BeginPick(false); });
            compareButton=Button(16+w,38,w,delegate { BeginPick(true); });
            fields=Button(24+2*w,38,w,delegate { onlyDifferences=!onlyDifferences; Draw(); });
            generate=Button(32+3*w,38,w,delegate {
                Poll();
                Report=RoadColorSample.MakeReport(a,comparison?b:null,UiLanguage.Chinese);
                message=UiLanguage.T("下次导出的诊断包中将附有报告","The next diagnostic ZIP will include the report.");
                Draw();
            });
            clear=Button(40+4*w,38,w,delegate { StopPicking(); a=b=null; Watch(); Report=null; message=null; Draw(); });
            summary=Label(72,38);
            abnormal=Button(8,110,140,delegate { Mark(true); });
            restored=Button(156,110,140,delegate { Mark(false); });
            markHint=Label(142,40);
            rows=panel.AddUIComponent<UIListBox>();
            rows.relativePosition=new Vector3(8,184);
            rows.size=new Vector2(panel.width-16,160);
            rows.itemHeight=22;
            rows.textScale=0.85f;
            rows.itemHover="ListItemHover"; rows.itemHighlight="ListItemHighlight";
            rows.eventSelectedIndexChanged+=delegate(UIComponent sender,int index) {
                if(detail!=null && index>=0 && index<rows.items.Length) detail.text=rows.items[index];
            };
            detail=Label(348,70);
            // Put the interactive handle above the title label and outside the fold button.
            var drag=panel.AddUIComponent<UIDragHandle>();
            drag.name="RoadColorAnalyzerTitleDrag";
            drag.target=panel;
            drag.relativePosition=Vector3.zero;
            drag.size=new Vector2(help.relativePosition.x-2,35);
            drag.isInteractive=true;
            drag.constrainToScreen=true;
            drag.BringToFront();
            InspectorPickTool.ChangingPicker+=StopPicking;
            Draw();
        }

        private UILabel Label(int y, int height)
        {
            var label=panel.AddUIComponent<UILabel>();
            label.relativePosition=new Vector3(12,y); label.isInteractive=false;
            label.autoSize=false; label.wordWrap=true; label.textScale=0.9f;
            label.size=new Vector2(panel.width-24,height);
            return label;
        }

        private UIButton Button(float x,float y,float width, Action action)
        {
            var button=panel.AddUIComponent<UIButton>();
            button.relativePosition=new Vector3(x,y);
            button.size=new Vector2(width,28); button.textScale=0.8f;
            button.normalBgSprite="ButtonMenu"; button.hoveredBgSprite="ButtonMenuHovered";
            button.pressedBgSprite="ButtonMenuPressed"; button.disabledBgSprite="ButtonMenuDisabled";
            button.eventClicked+=delegate { action(); };
            return button;
        }

        private void BeginPick(bool compare)
        {
            if(picking && comparison==compare){ StopPicking(); return; }
            ToolController controller=ToolsModifierControl.toolController;
            if(controller==null) return;
            InspectorPickTool.ReleaseOtherPicker();
            previous=controller.CurrentTool;
            if(picker==null) picker=controller.gameObject.AddComponent<InspectorPickTool>();
            picker.Clear();
            comparison=compare;
            a=b=null; Watch();
            firstId=secondId=InstanceID.Empty;
            picking=true;
            picker.Picked=Sample;
            picker.Undo=UndoSelection;
            picker.CursorHint=Hint;
            controller.CurrentTool=picker;
            message=null; Draw();
        }

        private string Hint()
        {
            if(showingHelp) return UiLanguage.T("点「返回」继续分析","Click Back to resume");
            if(!comparison) return UiLanguage.T("左键：检查路段或节点   右键：撤回","Left: inspect road or node   Right: back");
            if(a==null) return UiLanguage.T("左键：选择第一个路段或节点   右键：退出","Left: pick first road or node   Right: exit");
            if(!a.Abnormal.HasValue) return UiLanguage.T("请在窗口标记对象 1 正常或异常   右键：撤回","Mark object 1 normal or abnormal in the panel   Right: undo");
            if(b==null) return UiLanguage.T("左键：选择第二个路段或节点   右键：撤回第一个","Left: pick second road or node   Right: undo first");
            if(!b.Abnormal.HasValue) return UiLanguage.T("请在窗口标记对象 2 正常或异常   右键：撤回","Mark object 2 normal or abnormal in the panel   Right: undo");
            return UiLanguage.T("对比完成   右键：撤回第二个选择","Comparison ready   Right: undo second pick");
        }

        private void Sample(InstanceID id,Vector3 position)
        {
            if(showingHelp || !picking || (comparison && (b!=null || (a!=null && !a.Abnormal.HasValue)))) return;
            try
            {
                var sample=RoadColorSample.Capture(id,position);
                if(sample==null) return;
                Accept(id,sample);
            }
            catch(Exception error)
            {
                message=UiLanguage.T("采样失败  ","Sampling failed  ")+error.GetType().Name;
                Logging.Warning("Road color sampling failed: "+error);
            }
            Draw();
        }

        private void Accept(InstanceID id,RoadColorSample sample)
        {
            if(comparison && a!=null) { b=sample; secondId=id; }
            else { a=sample; firstId=id; }
            SyncSelection();
        }

        private void UndoSelection()
        {
            if(showingHelp) return;
            if(b!=null){ b=null; secondId=InstanceID.Empty; }
            else if(a!=null){ a=null; firstId=InstanceID.Empty; }
            else { StopPicking(); return; }
            SyncSelection();
            Draw();
        }

        private void SyncSelection()
        {
            Watch();
            picker.Pinned=comparison && b!=null ? firstId : InstanceID.Empty;
            picker.Selection=b!=null ? secondId : firstId;
            message=null;
        }

        private void StopPicking()
        {
            if(!picking) return;
            picking=false;
            if(picker!=null)
            {
                picker.Picked=null; picker.Undo=null; picker.CursorHint=null; picker.Clear();
                var controller=ToolsModifierControl.toolController;
                if(controller!=null && controller.CurrentTool==picker)
                    controller.CurrentTool=previous!=null?previous:controller.GetComponent<DefaultTool>();
            }
            previous=null;
            if(panel!=null && panel.isVisible) Draw();
        }

        private void Draw()
        {
            chinese=UiLanguage.Chinese;
            title.text=UiLanguage.T("道路颜色分析器","Road color analyzer");
            fold.text=folded?UiLanguage.T("展开","Expand"):UiLanguage.T("收起","Collapse");
            panel.height=folded?36:424;
            help.isVisible=!folded;
            help.text=showingHelp?UiLanguage.T("返回","Back"):UiLanguage.T("帮助","Help");
            helpText.isVisible=!folded && showingHelp;
            bool analyzing=!folded && !showingHelp;
            singleButton.isVisible=compareButton.isVisible=generate.isVisible=clear.isVisible=analyzing;
            summary.isVisible=rows.isVisible=detail.isVisible=analyzing;
            fields.isVisible=analyzing && comparison;
            abnormal.isVisible=restored.isVisible=analyzing && a!=null;
            markHint.isVisible=analyzing && (comparison || a!=null);
            if(helpText.isVisible)
                helpText.text=UiLanguage.T(
                    "同一段路，拉远就变白\n" +
                    "1. 在路面仍然变白时，点「单项分析」，再用鼠标左键点一下这段路。\n" +
                    "2. 点「标记画面异常」。拉近再拉远，回到刚才的视角。\n" +
                    "3. 如果颜色恢复，点「标记画面恢复」。始终选同一段路。如果一直没恢复，可以直接生成报告。\n\n" +
                    "同一画面里，一段路变白，另一段正常\n" +
                    "1. 保持视角不动，点「对比分析」，左键点变白的路，再点「标记画面异常」。\n" +
                    "2. 左键点附近颜色正常的路，再点「标记画面正常」。尽量选同款道路。选错时，在地图上点右键撤回最近一次选择。\n\n" +
                    "报告错误\n" +
                    "点「生成报告」，再打开 MNC 设置 →「故障排查」→「导出诊断包」，发送导出的 ZIP 文件即可。",
                    "One road turns white when you zoom out\n" +
                    "1. While the road is white, click Inspect, then left-click that road.\n" +
                    "2. Click Mark abnormal. Zoom in, then back out to the same view.\n" +
                    "3. If the color returns, click Mark recovered. Keep the same road selected. If it stays white, you can still make a report.\n\n" +
                    "Two roads look different in the same view\n" +
                    "1. Keep the camera still. Click Compare, left-click the white road, then click Mark abnormal.\n" +
                    "2. Left-click a nearby road that looks normal and click Mark normal. Try to pick the same road type. Right-click the map to undo a pick.\n\n" +
                    "Report a bug\n" +
                    "Click Make report. Open MNC settings → Diagnostics → Export diagnostic ZIP. Send the ZIP to the person investigating.")
                    +"\nGitHub Issues: "+IssuesUrl;
            markHint.text=comparison
                ? UiLanguage.T("与区域有关的颜色异常，请保持同一视角。选择路段 1 并标记正常或异常，再选择路段 2 并标记正常或异常。","For area-specific color changes, keep the same camera view. Pick segment 1 and mark it normal or abnormal, then do the same for segment 2.")
                : UiLanguage.T("与视角有关的颜色异常请在 同一路段 使用该工具。","For color changes that depend on the camera view, use this tool on the same road segment.");
            abnormal.text=UiLanguage.T("标记画面异常","Mark abnormal");
            restored.text=comparison?UiLanguage.T("标记画面正常","Mark normal"):UiLanguage.T("标记画面恢复","Mark recovered");
            if(!analyzing) return;
            fields.text=onlyDifferences?UiLanguage.T("显示全部","Show all"):UiLanguage.T("只看差异","Differences");
            singleButton.text=picking && !comparison ? UiLanguage.T("结束单项分析","End inspection") : UiLanguage.T("单项分析","Inspect");
            compareButton.text=picking && comparison ? UiLanguage.T("结束对比分析","End comparison") : UiLanguage.T("对比分析","Compare");
            generate.text=UiLanguage.T("生成报告","Make report");
            clear.text=UiLanguage.T("清空","Clear");
            generate.isEnabled=a!=null && (!comparison || (b!=null && b.Abnormal.HasValue));
            summary.text=comparison?"1: "+ObjectStatus(a)+"     2: "+ObjectStatus(b):(a==null?"—":a.ObjectName);
            if(message!=null) summary.text+="\n"+message;
            List<string> lines;
            if(comparison && a!=null && b!=null)
            {
                lines=a.Compare(b,chinese,onlyDifferences);
                if(lines.Count==0) lines.Add(UiLanguage.T("采样字段相同","Sampled fields match"));
            }
            else if(a!=null || (comparison && b!=null)) lines=(a??b).Lines(chinese);
            else lines=new List<string>();
            rows.selectedIndex=-1; rows.items=lines.ToArray(); rows.scrollPosition=0;
            detail.text=lines.Count>0?UiLanguage.T("点选列表行查看完整值。? 表示未知，— 表示无。","Select a row for its full value. ? = unknown; — = absent."):
                UiLanguage.T("点选地图上的路段或节点。","Pick a road segment or node on the map.");
        }
        private static string ObjectStatus(RoadColorSample sample)
        {
            if(sample==null) return "—";
            return sample.ObjectName+" ("+sample.ComparisonStatus(UiLanguage.Chinese)+")";
        }

        private void Mark(bool isAbnormal)
        {
            var target=b??a;
            if(target==null) return;
            if(comparison) target.Abnormal=isAbnormal;
            string observation=isAbnormal?"异常 / abnormal":comparison?"正常 / normal":"恢复 / recovered";
            if(target.Trace!=null) target.Trace.Mark(observation);
            message=UiLanguage.T("已标记当前画面：","Scene marked: ")+observation;
            Draw();
        }
        private void Watch()
        {
            if(a!=null && a.Trace!=null || b!=null && b.Trace!=null) RoadRenderHooks.Start();
            RoadRenderHooks.First=a==null?null:a.Trace;
            RoadRenderHooks.Second=b==null?null:b.Trace;
        }
        private void Poll()
        {
            if(a!=null && a.Trace!=null) a.Trace.Poll();
            if(b!=null && b.Trace!=null) b.Trace.Poll();
        }

        private void Update()
        {
            if(panel==null) return;
            if(picking && (ToolsModifierControl.toolController==null || ToolsModifierControl.toolController.CurrentTool!=picker))
                StopPicking();
            if(chinese!=UiLanguage.Chinese) Draw();
            if(Time.unscaledTime>=nextPoll){nextPoll=Time.unscaledTime+0.4f; Poll();}
        }

        private void OnDisable(){ StopPicking(); RoadRenderHooks.Stop(); }
        private void OnDestroy()
        {
            InspectorPickTool.ChangingPicker-=StopPicking;
            StopPicking();
            if(picker!=null) UnityEngine.Object.Destroy(picker);
            if(panel!=null) UnityEngine.Object.Destroy(panel.gameObject);
        }
    }
}
