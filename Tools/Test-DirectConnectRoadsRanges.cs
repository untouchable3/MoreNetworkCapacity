using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;
namespace MoreNetworkCapacity { internal static class Logging { internal static void Info(string s) {} } }
namespace MoreNetworkCapacity.Compatibility {
internal static class ThirdPartyCapacityCompatibility { internal static Assembly FindLoadedAssembly(string s) { return null; } }
internal static class IntersectionMarkingCompatibility { internal static int NodeCount() { return 65024; } }
}
class Test {
static string[] paths;
static Assembly Resolve(object o, ResolveEventArgs e) { foreach(string p in paths) { string f=Path.Combine(p,new AssemblyName(e.Name).Name+".dll"); if(File.Exists(f))return Assembly.LoadFrom(f); } return null; }
static int Main(string[] args) { try { paths=args;AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
Assembly a=Assembly.LoadFrom(Path.Combine(args[1],"DirectConnectRoads.dll"));
Type t=a.GetType("DirectConnectRoads.Util.NetInfoUtil",true);
foreach(string name in DirectConnectRoadsCompatibility.Targets) {
MethodInfo m=t.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
List<CodeInstruction> input=PatchProcessor.GetOriginalInstructions(m,(ILGenerator)null);
List<CodeInstruction> output=DirectConnectRoadsCompatibility.Rewrite(m,input);
int n=0;for(int i=0;i<input.Count;i++)if(input[i].opcode!=output[i].opcode){n++; if(output[i].opcode!=OpCodes.Call)throw new Exception("Unexpected rewrite");}
if(n!=1)throw new Exception("Expected exactly one bound");
int b=input.FindIndex(delegate(CodeInstruction c){return c.opcode==OpCodes.Ldc_I4 && object.Equals(c.operand,32768);});
input[b+1]=new CodeInstruction(OpCodes.Cgt, null);
bool refused=false;try{DirectConnectRoadsCompatibility.Rewrite(m,input);}catch(InvalidOperationException){refused=true;}if(!refused)throw new Exception("Changed comparison accepted");
Console.WriteLine("PASS real DCR IL and altered-comparison rejection: "+name);
} return 0;}catch(Exception e){Console.WriteLine(e);return 1;} }
}
