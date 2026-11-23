using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity.Diagnostics
{
    // Installed only while the player has the color analyzer open in a city.
    internal static class RoadRenderHooks
    {
        private const string Owner="com.codex.csl.more-network-capacity.colortrace";
        private static Harmony harmony;
        internal static string Status="inactive";
        internal static volatile RoadRenderTrace First, Second;
        [ThreadStatic] private static Scope current;
        internal struct Scope { internal RoadRenderTrace A,B; internal int X,Z,Layer; }

        internal static void Start()
        {
            if(harmony!=null) return;
            Logging.Info("Road color render observers: install BEGIN.");
            try
            {
                harmony=new Harmony(Owner);
                foreach(Type type in new[]{typeof(NetNode),typeof(NetSegment)})
                foreach(MethodInfo m in type.GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly))
                {
                    var p=m.GetParameters();
                    bool group=m.Name=="PopulateGroupData";
                    if(group && p[0].ParameterType==typeof(ushort) || m.Name=="RenderInstance" && p.Length==3 && p[1].ParameterType==typeof(ushort))
                        harmony.Patch(m,Hook(group?"Group":"Instance"),null,null,Hook("Exit"));
                    else if(group && p[0].ParameterType==typeof(NetInfo))
                        harmony.Patch(m,Hook("BeforeVertices"),Hook("Vertices"),null,null);
                    else if(m.Name=="RenderSegments" || (m.Name=="RenderInstance" && p.Length>3))
                        harmony.Patch(m,null,null,Hook("ObserveDraws"),null);
                }
                Status="active";
                Logging.Info("Road color render observers: install END.");
            }
            catch(Exception error)
            {
                Stop(); Status="unavailable: "+error.GetType().Name;
                Logging.Warning("Road color render diagnostics unavailable: "+error);
            }
        }
        internal static void Stop()
        {
            First=Second=null;
            if(harmony!=null) harmony.UnpatchAll(Owner);
            harmony=null; Status="inactive";
        }
        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(RoadRenderHooks).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
        }
        private static Scope Enter(MethodBase m,ushort id,int x,int z,int layer)
        {
            Scope old=current;
            bool node=m.DeclaringType==typeof(NetNode);
            var a=First; var b=Second;
            current=new Scope { A=a!=null && a.Matches(node,id)?a:null, B=b!=null && b.Matches(node,id)?b:null,X=x,Z=z,Layer=layer };
            return old;
        }
        // Named arguments differ between nodeID and segmentID; their positions are stable.
        private static void Group(MethodBase __originalMethod,ushort __0,int groupX,int groupZ,int layer,out Scope __state)
        { __state=Enter(__originalMethod,__0,groupX,groupZ,layer); }
        private static void Instance(MethodBase __originalMethod,ushort __1,out Scope __state)
        { __state=Enter(__originalMethod,__1,0,0,0); }
        private static void Exit(Scope __state) { current=__state; }
        private static void BeforeVertices(int vertexIndex,out int __state) { __state=vertexIndex; }
        private static void Vertices(RenderGroup.MeshData data,int vertexIndex,int __state,Vector4 objectIndex,Vector2 objectColorIndex)
        {
            if(current.A==null && current.B==null) return;
            var expected=new Vector4(objectIndex.z,objectIndex.w,objectColorIndex.x,objectColorIndex.y);
            try
            {
                if(current.A!=null) current.A.Vertices(data.m_uvs4,__state,vertexIndex,expected,current.X,current.Z,current.Layer);
                if(current.B!=null) current.B.Vertices(data.m_uvs4,__state,vertexIndex,expected,current.X,current.Z,current.Layer);
            }
            catch(Exception) { /* Observations never interrupt group construction. */ }
        }
        private static IEnumerable<CodeInstruction> ObserveDraws(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo draw=typeof(Graphics).GetMethod("DrawMesh",new[]{typeof(Mesh),typeof(Vector3),typeof(Quaternion),typeof(Material),
                typeof(int),typeof(Camera),typeof(int),typeof(MaterialPropertyBlock)});
            var count=typeof(NetInfo.LodValue).GetField("m_lodCount");
            foreach(var ins in instructions)
            {
                if(ins.opcode==OpCodes.Call && Equals(ins.operand,draw))
                    ins.operand=typeof(RoadRenderHooks).GetMethod("Submit");
                else if(ins.opcode==OpCodes.Stfld && Equals(ins.operand,count))
                {
                    ins.opcode=OpCodes.Call; ins.operand=typeof(RoadRenderHooks).GetMethod("Batch");
                }
                yield return ins;
            }
        }
        public static void Submit(Mesh mesh,Vector3 position,Quaternion rotation,Material material,int layer,Camera camera,int submesh,MaterialPropertyBlock props)
        {
            Graphics.DrawMesh(mesh,position,rotation,material,layer,camera,submesh,props);
            Draw(false,material);
        }
        public static void Batch(NetInfo.LodValue lod,int count)
        {
            lod.m_lodCount=count;
            if(count>0) Draw(true,lod.m_material);
        }
        private static void Draw(bool batch,Material material)
        {
            if(current.A!=null) current.A.Draw(batch,material);
            if(current.B!=null) current.B.Draw(batch,material);
        }
    }

    internal sealed class RoadRenderTrace
    {
        private readonly bool node;
        private readonly ushort id;
        private readonly Vector3 point;
        private readonly uint buildIndex;
        private volatile bool expired;
        private bool bindingMismatch;
        private string lastLine;
        private readonly List<string> marks=new List<string>();
        private readonly object sync=new object();
        private int skippedCallbacks;
        private long groups,vertices,bad,missing,direct,batches;
        private string lastGroup,firstBad,lastState;
        private Vector4 lastUv;
        private bool hasUv;
        private Material near,lod;
        private readonly List<string> history=new List<string>();
        private int omitted;
        internal RoadRenderTrace(bool node,ushort id,Vector3 point,uint buildIndex) { this.node=node;this.id=id;this.point=point;this.buildIndex=buildIndex; }
        internal bool Matches(bool n,ushort value){return !expired && node==n && id==value;}
        private static string UV(Vector4 v){return string.Format(CultureInfo.InvariantCulture,"({0:G7},{1:G7},{2:G7},{3:G7})",v.x,v.y,v.z,v.w);}
        internal void Vertices(Vector4[] data,int from,int to,Vector4 expected,int x,int z,int layer)
        {
            if(to<=from) return;
            // The render path can skip observations if the diagnostics UI owns the lock.
            if(!Monitor.TryEnter(sync))
            {
                Interlocked.Increment(ref skippedCallbacks);
                return;
            }
            try
            {
                groups++;
                lastGroup="group="+x+","+z+" layer="+layer+" vertices=["+from+","+to+")";
                if(data==null || from<0 || to>data.Length){missing++;return;}
                vertices+=to-from;
                for(int i=from;i<to;i++)
                {
                    Vector4 actual=data[i]; lastUv=actual; hasUv=true;
                    if(actual.Equals(expected)) continue;
                    bad++;
                    if(firstBad==null) firstBad=lastGroup+" vertex="+i+" writer="+UV(expected)+" buffer="+UV(actual);
                }
            }
            finally { Monitor.Exit(sync); }
        }
        internal void Draw(bool batch,Material material)
        {
            if(!Monitor.TryEnter(sync))
            {
                Interlocked.Increment(ref skippedCallbacks);
                return;
            }
            try { if(batch){batches++;lod=material;}else{direct++;near=material;} }
            finally { Monitor.Exit(sync); }
        }
        private static string Texel(Texture2D tex,int index)
        {
            if(tex==null || index<0 || index>=(long)tex.width*tex.height) return "?";
            Color32 c=tex.GetPixel(index%tex.width,index/tex.width);
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}",c.r,c.g,c.b,c.a);
        }
        private static string Channel(Texture2D tex,float x,float y)
        {
            if(tex==null || !(x>=0 && x<1 && y>=0 && y<1)) return "outside/unknown";
            int index=(int)(x*tex.width)+tex.width*(int)(y*tex.height);
            return index+"="+Texel(tex,index);
        }
        private static string Binding(Material mat)
        {
            if(mat==null)return "—";
            return RoadColorSample.Identity(mat)+" / "+RoadColorSample.Identity(mat.shader)+" / APR="+
                (mat.HasProperty("_APRMap")?RoadColorSample.Identity(mat.GetTexture("_APRMap")):"—");
        }
        internal void Poll()
        {
            string state;
            try
            {
                if(expired) return;
                var net=Singleton<NetManager>.instance;
                bool valid=node ? id<net.m_nodes.m_buffer.Length && net.m_nodes.m_buffer[id].m_buildIndex==buildIndex &&
                    (net.m_nodes.m_buffer[id].m_flags & NetNode.Flags.Created)!=0 :
                    id<net.m_segments.m_buffer.Length && net.m_segments.m_buffer[id].m_buildIndex==buildIndex &&
                    (net.m_segments.m_buffer[id].m_flags & NetSegment.Flags.Created)!=0;
                if(!valid){expired=true;return;}
                var manager=Singleton<RenderManager>.instance;
                Texture2D tex=manager.m_objectColorMap;
                Texture global=Shader.GetGlobalTexture("_ObjectColorMap");
                int slot=(node?Capacity.GetNodeColorIndexBase():Capacity.SegmentColorIndexBase)+id;
                bool matches=tex!=null && tex==global;
                bool mismatch=tex!=null && global!=tex;
                Material lastNear, lastLod;
                bool hasSample;
                Vector4 uv;
                lock(sync)
                {
                    bindingMismatch|=mismatch;
                    lastNear=near;
                    lastLod=lod;
                    hasSample=hasUv;
                    uv=lastUv;
                }
                state="atlas="+RoadColorSample.Identity(tex)+" global="+RoadColorSample.Identity(global)+
                    " match="+matches+" slot="+slot+" "+Texel(tex,slot)+
                    " | direct="+Binding(lastNear)+" | batch="+Binding(lastLod);
                if(tex!=global && global is Texture2D) state+=" global slot="+Texel((Texture2D)global,slot);
                if(hasSample) state+=" | UV4.xy "+Channel(tex,uv.x,uv.y)+" zw "+Channel(tex,uv.z,uv.w);
            }
            catch(Exception ex){state="sampling unavailable: "+ex.GetType().Name;}
            var camera=Camera.main;
            float distance=camera==null ? -1 : Vector3.Distance(camera.transform.position,point);
            lock(sync)
            {
                // Record camera movement even when colors remain stable.
                int band=Mathf.FloorToInt(distance/50);
                string key=band+" "+state+" direct/batch/group="+(direct!=lastDirect)+"/"+(batches!=lastBatch)+"/"+(groups!=lastGroupCount);
                lastDirect=direct;lastBatch=batches;lastGroupCount=groups;
                lastLine=DateTime.UtcNow.ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture)+" distance="+distance.ToString("F0",CultureInfo.InvariantCulture)+
                    " direct/batch/group="+direct+"/"+batches+"/"+groups+" "+state;
                if(key==lastState) return;
                lastState=key;
                if(history.Count==24){history.RemoveAt(1);omitted++;}
                history.Add(lastLine);
            }
        }
        internal void Mark(string label)
        {
            Poll();
            lock(sync)
            {
                if(marks.Count==12) marks.RemoveAt(1);
                marks.Add(label+" | "+(expired?"selection expired":lastLine));
            }
        }
        private long lastDirect,lastBatch,lastGroupCount;
        internal void Describe(RoadColorSample sample)
        {
            lock(sync)
            {
                sample.Add("追踪：对象有效性|Trace: selection validity",expired?"已释放或重建 / expired":"未发现替换 / no replacement observed");
                sample.Add("追踪：颜色贴图绑定异常|Trace: atlas binding mismatch",bindingMismatch?"已观测 / observed":"未观测 / not observed");
                sample.Add("追踪：渲染观察器|Trace: render observer",RoadRenderHooks.Status);
                sample.Add("追踪：因争用跳过的采样|Trace: observations skipped due to contention",Interlocked.CompareExchange(ref skippedCallbacks,0,0).ToString(CultureInfo.InvariantCulture));
                sample.Add("追踪：直接绘制 / LOD 入批次数|Trace: draw submissions / LOD enqueues",direct+" / "+batches);
                sample.Add("追踪：分组网格写入 / 顶点数|Trace: group writes / vertices",groups+" / "+vertices);
                sample.Add("追踪：顶点 UV4 校验|Trace: vertex UV4 check",bad>0?"MISMATCH "+bad+" | "+firstBad:
                    (vertices==0?"未观测 / not observed":"一致 / match: "+vertices)+(missing==0?"":" | missing ranges="+missing));
                if(lastGroup!=null)sample.Add("追踪：最近分组|Trace: last group",lastGroup);
                if(hasUv)sample.Add("追踪：最近顶点 UV4|Trace: last vertex UV4",UV(lastUv));
            }
        }
        internal void Append(StringBuilder text)
        {
            lock(sync)
            {
                text.AppendLine("UTC | distance / observed direct draws, LOD enqueues, group writes / material and color bindings");
                foreach(string mark in marks) text.AppendLine("PLAYER MARK | "+mark);
                foreach(string line in history)text.AppendLine(line);
                if(omitted!=0)text.AppendLine("Intermediate transitions omitted: "+omitted);
                text.AppendLine("UV4 comparison covers observed CPU group writes before mesh upload. Final GPU pixels and pre-existing cached meshes are not sampled.");
            }
        }
    }
}
