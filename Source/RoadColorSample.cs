using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using ColossalFramework;
using ColossalFramework.Plugins;
using UnityEngine;

namespace MoreNetworkCapacity.Diagnostics
{
    internal sealed class RoadColorSample
    {
        internal readonly DateTime Time = DateTime.UtcNow;
        internal readonly string ObjectName;
        internal RoadRenderTrace Trace;
        internal bool? Abnormal;
        internal string ComparisonStatus(bool chinese)
        {
            if(!Abnormal.HasValue) return chinese?"待标记":"Unmarked";
            return Abnormal.Value?(chinese?"异常":"Abnormal"):(chinese?"正常":"Normal");
        }
        private readonly SortedDictionary<string,string> values = new SortedDictionary<string,string>(StringComparer.Ordinal);

        internal RoadColorSample(string objectName) { ObjectName = objectName; }
        internal void Add(string key, string value) { values[key] = value; }

        private static string Label(string key, bool chinese)
        {
            string[] names = key.Split('|');
            return chinese ? names[0] : names[names.Length-1];
        }

        private static string Display(string value,bool chinese)
        {
            if(!chinese) return value;
            if(value=="Disabled") return "未启用";
            if(value=="Enabled") return "已启用";
            if(value=="No skin") return "未绑定";
            if(value=="Skin bound, no color override") return "已绑定，无颜色修改";
            if(value.StartsWith("Color override ",StringComparison.Ordinal)) return "有颜色修改 " + value.Substring("Color override ".Length);
            return value;
        }

        internal List<string> Lines(bool chinese)
        {
            if(Trace!=null) Trace.Describe(this);
            var rows = new List<string>();
            foreach (var pair in values) rows.Add(Label(pair.Key,chinese) + "  " + Display(pair.Value,chinese));
            return rows;
        }

        internal List<string> Differences(RoadColorSample other, bool chinese)
        {
            return Compare(other,chinese,true);
        }

        internal List<string> Compare(RoadColorSample other,bool chinese,bool differencesOnly)
        {
            if(Trace!=null) Trace.Describe(this);
            if(other.Trace!=null) other.Trace.Describe(other);
            var rows = new List<string>();
            var keys = new SortedDictionary<string,string>(values, StringComparer.Ordinal);
            foreach (string key in other.values.Keys) keys[key] = "";
            foreach (string key in keys.Keys)
            {
                string a, b;
                values.TryGetValue(key,out a); other.values.TryGetValue(key,out b);
                if(a==b && !differencesOnly) rows.Add(Label(key,chinese)+"  =  "+Display(a ?? "—",chinese));
                if (a != b) rows.Add(Label(key,chinese) + "  " + Display(a ?? "—",chinese) + " → " + Display(b ?? "—",chinese));
            }
            return rows;
        }

        internal static string MakeReport(RoadColorSample a, RoadColorSample b, bool chinese)
        {
            var result = new StringBuilder("MNC " + BuildInfo.Version + " | Road color analysis\r\n");
            foreach (var sample in new[]{a,b})
            {
                if (sample == null) continue;
                result.AppendLine((ReferenceEquals(sample,a) ? "1. " : "2. ") + sample.ObjectName + " | " + sample.Time.ToString("u"));
                if(b!=null && sample.Abnormal.HasValue)
                    result.AppendLine((chinese?"画面标记  ":"Visual observation  ")+sample.ComparisonStatus(chinese));
                foreach (string row in sample.Lines(chinese)) result.AppendLine(row);
                if(sample.Trace!=null) sample.Trace.Append(result);
                result.AppendLine();
            }
            if (b != null)
            {
                var differences = a.Differences(b,chinese);
                result.AppendLine((chinese ? "差异（1 → 2）" : "Differences (1 → 2)") + "  " + differences.Count);
                foreach(string row in differences) result.AppendLine(row);
            }
            return result.ToString();
        }

        private static string Number(float value) { return value.ToString("0.##",CultureInfo.InvariantCulture); }
        private static string ColorText(Color color)
        {
            Color32 c = color;
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}",c.r,c.g,c.b,c.a);
        }
        private static string Asset(UnityEngine.Object obj) { return obj == null ? "—" : obj.name; }

        internal static string Identity(UnityEngine.Object obj)
        {
            if(obj==null) return "—";
            var tex=obj as Texture;
            return (string.IsNullOrEmpty(obj.name)?"(unnamed)":obj.name)+" #"+obj.GetInstanceID()+
                (tex==null?"":" "+tex.width+"x"+tex.height);
        }

        internal static RoadColorSample Capture(InstanceID id, Vector3 hit)
        {
            var net = Singleton<NetManager>.instance;
            bool node = id.Type == InstanceType.NetNode;
            ushort index = node ? id.NetNode : id.NetSegment;
            if (index == 0 || (!node && id.Type != InstanceType.NetSegment)) return null;
            var nodes = net.m_nodes.m_buffer;
            var segments = net.m_segments.m_buffer;
            if (node ? index >= nodes.Length || (nodes[index].m_flags & NetNode.Flags.Created)==0 :
                index >= segments.Length || (segments[index].m_flags & NetSegment.Flags.Created)==0) return null;
            NetInfo info = node ? nodes[index].Info : segments[index].Info;
            var s = new RoadColorSample((node ? "NetNode " : "NetSegment ") + index);
            s.Add("对象|Object",s.ObjectName);
            s.Add("资产内部名|Prefab",Asset(info));
            s.Add("命中位置 X/Y/Z|Hit X/Y/Z", Number(hit.x)+" / "+Number(hit.y)+" / "+Number(hit.z));
            if (Camera.main != null) s.Add("相机到命中点距离|Camera distance",Number(Vector3.Distance(Camera.main.transform.position,hit)));
            if (info != null)
            {
                s.Read("道路显示名|Road title",delegate { return info.GetLocalizedTitle(); });
                s.Read("道路变体|Road variant", delegate { return Variant(info); });
                s.Add("道路 AI|Road AI",info.m_netAI == null ? "—" : info.m_netAI.GetType().Name);
                s.Add("预制件当前基础色|Current prefab color",ColorText(info.m_color));
                if (node && info.m_nodes != null)
                    for(int i=0;i<info.m_nodes.Length;i++)
                    {
                        var n=info.m_nodes[i]; if(n!=null) s.Mesh(i,n.m_nodeMaterial,n.m_material,n.m_lodMaterial,n.m_combinedLod,n.m_lodRenderDistance);
                    }
                if (!node && info.m_segments != null)
                    for(int i=0;i<info.m_segments.Length;i++)
                    {
                        var seg=info.m_segments[i]; if(seg!=null) s.Mesh(i,seg.m_segmentMaterial,seg.m_material,seg.m_lodMaterial,seg.m_combinedLod,seg.m_lodRenderDistance);
                    }
            }
            if(!node) s.Add("端点|Endpoints",segments[index].m_startNode+" → "+segments[index].m_endNode);
            int pixel=(node ? Capacity.GetNodeColorIndexBase() : Capacity.SegmentColorIndexBase)+index;
            s.Add("预期颜色格编号|Expected color slot",pixel.ToString());
            s.Read("预期颜色格 RGBA|Expected atlas RGBA",delegate {
                var tex=Singleton<RenderManager>.instance.m_objectColorMap;
                if(tex==null || pixel >= (long)tex.width*tex.height) return "?";
                return ColorText(tex.GetPixel(pixel%tex.width,pixel/tex.width)); });
            var plugins = EnabledAssemblies();
            Assembly skins = Find(plugins,"NetworkSkins");
            Assembly painter = Find(plugins,"Painter");
            foreach(string name in new[]{"NetworkSkins","Painter","SurfacePainter","ThemeMixer","TrueLodToggler"})
                s.Add(name+" 状态|"+name+" status",plugins==null ? "?" : Find(plugins,name)==null ? "Disabled" : "Enabled");
            s.Skin(skins,node,index,"",plugins!=null);
            if(!node)
            {
                s.Skin(skins,true,segments[index].m_startNode," A",plugins!=null);
                s.Skin(skins,true,segments[index].m_endNode," B",plugins!=null);
            }
            s.Read("Repaint 道路换纹理开关|Repaint RetextureRoads",delegate {
                if(painter==null) return plugins==null ? "?" : "—";
                return painter.GetType("Repaint.ModSettings",true).GetProperty("RetextureRoads",Members).GetValue(null,null).ToString(); });
            s.Read("Repaint 道路名单命中|Repaint supported name",delegate {
                if(painter==null) return plugins==null ? "?" : "—";
                var names=(HashSet<string>)painter.GetType("Repaint.NetInitMeshDataPatches",true).GetField("SupportedNetNames",Members).GetValue(null);
                return names.Contains(info.name).ToString(); });
            s.Read("环境：道路上表面纹理|Global road upward diffuse", delegate { return Identity(Shader.GetGlobalTexture("_RoadUpwardDiffuse")); });
            s.Read("环境：道路下表面纹理|Global road downward diffuse", delegate { return Identity(Shader.GetGlobalTexture("_RoadDownwardDiffuse")); });
            s.Read("环境：道路远景 APR 图集|Network LOD APR atlas",delegate { return Identity(net.m_lodAprAtlas); });
            s.Read("地表：水泥 A/B、砂砾、裸地|Surface: pavement A/B, gravel, ruined",delegate {
                var cell=Singleton<TerrainManager>.instance.SampleDetailSurface(hit);
                return cell.m_pavementA+" / "+cell.m_pavementB+" / "+cell.m_gravel+" / "+cell.m_ruined; });
            s.Trace=new RoadRenderTrace(node,index,hit,node ? nodes[index].m_buildIndex : segments[index].m_buildIndex);
            s.Trace.Poll();
            return s;
        }

        private static string Variant(NetInfo info)
        {
            // Use actual prefab links, including custom-named variants.
            for(uint i=0;i<PrefabCollection<NetInfo>.LoadedCount();i++)
            {
                NetInfo candidate=PrefabCollection<NetInfo>.GetLoaded(i);
                if(candidate==null) continue;
                var road=candidate.m_netAI as RoadAI;
                if(road==null) continue;
                if(road.m_elevatedInfo==info) return "Elevated";
                if(road.m_bridgeInfo==info) return "Bridge";
                if(road.m_slopeInfo==info) return "Slope";
                if(road.m_tunnelInfo==info) return "Tunnel";
            }
            return info.m_netAI!=null && info.m_netAI.GetType()==typeof(RoadAI) ? "Ground (RoadAI)" : "?";
        }

        private void Mesh(int i, Material near, Material source, Material lod, NetInfo.LodValue combined, float distance)
        {
            string slot=" ["+i+"]";
            MaterialFields("近景"+slot+"|Near"+slot,near);
            MaterialFields("源材质"+slot+"|Source material"+slot,source);
            MaterialFields("远景"+slot+"|LOD"+slot,lod);
            MaterialFields("合并远景"+slot+"|Combined LOD"+slot,combined == null ? null : combined.m_material);
            Add("配置的 LOD 距离"+slot+"|Configured LOD distance"+slot,Number(distance));
        }

        private void MaterialFields(string key, Material mat)
        {
            Read(key,delegate {
                if(mat==null) return "—";
                return Identity(mat)+" / "+Identity(mat.shader)+" / APR: "+(mat.HasProperty("_APRMap") ? Identity(mat.GetTexture("_APRMap")) : "—")
                    +" / Main: "+(mat.HasProperty("_MainTex")?Identity(mat.GetTexture("_MainTex")):"—")
                    +" / tint: "+(mat.HasProperty("_Color")?ColorText(mat.GetColor("_Color")):"—"); });
        }
        private const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private static Dictionary<string,Assembly> EnabledAssemblies()
        {
            var result = new Dictionary<string,Assembly>(StringComparer.Ordinal);
            try
            {
                foreach(var plugin in PluginManager.instance.GetPluginsInfo())
                    if(plugin.isEnabled) foreach(var asm in plugin.GetAssemblies()) result[asm.GetName().Name]=asm;
                return result;
            }
            catch(Exception) { return null; }
        }

        private static Assembly Find(Dictionary<string,Assembly> plugins,string name)
        {
            Assembly assembly;
            return plugins!=null && plugins.TryGetValue(name,out assembly) ? assembly : null;
        }

        private void Read(string key, Func<string> read)
        {
            try { Add(key,read()); }
            catch(Exception) { Add(key,"?"); }
        }
        private void Skin(Assembly asm,bool node,ushort id,string suffix,bool pluginsKnown)
        {
            string key="Network Skins 皮肤"+suffix+"|Network Skins skin"+suffix;
            Read(key,delegate {
                if(asm==null) return pluginsKnown ? "Disabled" : "?";
                Type type=asm.GetType("NetworkSkins.Skins.NetworkSkinManager",true);
                var skins=(Array)type.GetField(node?"NodeSkins":"SegmentSkins",Members).GetValue(null);
                object skin=skins.GetValue(id);
                if(skin==null) return "No skin";
                var modifiers=(IEnumerable)skin.GetType().GetProperty("Modifiers",Members).GetValue(skin,null);
                foreach(object mod in modifiers)
                    if(mod!=null && mod.GetType().FullName=="NetworkSkins.Skins.Modifiers.ColorModifier")
                        return "Color override "+ColorText((Color)skin.GetType().GetField("m_color",Members).GetValue(skin));
                return "Skin bound, no color override"; });
        }
    }
}
