using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity {
 internal static class Capacity { internal const int VanillaNodeCapacity=32768, ExpandedNodeCapacity=65024; }
 internal static class Logging { internal static void Info(string value) {} }
}
namespace MoreNetworkCapacity.Compatibility {
 internal static class ThirdPartyCapacityCompatibility { internal static Assembly FindLoadedAssembly(string name) { return null; } }
}
static class TMCEDistanceTests {
 static class CacheFixture { public static object A=null,B=null,C=null,D=null,E=null,F=null;public static bool Init=false; }
 const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
 static Assembly Resolve(object sender,ResolveEventArgs args) {
  string name=new AssemblyName(args.Name).Name+".dll";
  foreach(string directory in new string[]{Path.Combine(Environment.GetEnvironmentVariable("MNC_STEAM_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"), "steamapps/common/Cities_Skylines/Cities_Data/Managed"),Path.Combine(Environment.GetEnvironmentVariable("MNC_STEAM_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"), "steamapps/workshop/content/255710/2040656402")}) {
   string file=Path.Combine(directory,name); if(File.Exists(file)) return Assembly.LoadFrom(file);
  } return null;
 }
 static List<CodeInstruction> Original(MethodBase method) {
  DynamicMethod dm=new DynamicMethod("read",typeof(void),Type.EmptyTypes,typeof(TMCEDistanceTests),true);
  return PatchProcessor.GetOriginalInstructions(method,dm.GetILGenerator());
 }
 static List<CodeInstruction> Copy(List<CodeInstruction> source) { List<CodeInstruction> copy=new List<CodeInstruction>(); foreach(CodeInstruction c in source) copy.Add(new CodeInstruction(c)); return copy; }
 static int Anchor(List<CodeInstruction> code) { for(int i=0;i<code.Count;i++) if(code[i].LoadsConstant(32768)) return i; throw new Exception("No limit"); }
 static void Reject(MethodBase method,List<CodeInstruction> code,string name) {
  bool rejected=false;try { TMCECompatibility.Rewrite(method,code); } catch(InvalidOperationException) { rejected=true; }
  Check(rejected,"Unsafe pattern accepted: "+name); Console.WriteLine("REJECT "+name);
 }
 static List<CodeInstruction> Positive(MethodBase method) {
  List<CodeInstruction> original=Original(method); int index=Anchor(original);
  Label extra=new DynamicMethod("labels",typeof(void),Type.EmptyTypes).GetILGenerator().DefineLabel();
  original[index].labels.Add(extra); original[index].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock,null));
  List<CodeInstruction> patched=TMCECompatibility.Rewrite(method,original); int changed=0;
  Check(original.Count==patched.Count,"Instruction count changed");
  for(int i=0;i<original.Count;i++) {
   Check(!Object.ReferenceEquals(original[i],patched[i]),"Input instruction reused");
   if(original[i].opcode!=patched[i].opcode||!Object.Equals(original[i].operand,patched[i].operand)) { changed++;Check(i==index&&patched[i].LoadsConstant(65024),"Wrong replacement"); }
   Check(original[i].labels.Count==patched[i].labels.Count&&original[i].blocks.Count==patched[i].blocks.Count,"Metadata lost");
   foreach(Label label in original[i].labels) Check(patched[i].labels.Contains(label),"Label lost");
   for(int j=0;j<original[i].blocks.Count;j++) Check(original[i].blocks[j].blockType==patched[i].blocks[j].blockType,"Block lost");
  }
  Check(changed==1&&original[index].LoadsConstant(32768),"Input mutated or wrong change count");
  Console.WriteLine("PASS real "+method.Name+": exactly one replacement; input, labels and exception blocks preserved");
  return patched;
 }
 static void ConstructorReplay(List<CodeInstruction> code) {
  int index=-1;for(int i=0;i<code.Count;i++) if(code[i].LoadsConstant(65024)) index=i;
  DynamicMethod dm=new DynamicMethod("bitmap",typeof(BitArray),Type.EmptyTypes,typeof(TMCEDistanceTests),true); ILGenerator il=dm.GetILGenerator();
  il.Emit(code[index].opcode,(int)code[index].operand);il.Emit(code[index+1].opcode,(ConstructorInfo)code[index+1].operand);il.Emit(OpCodes.Ret);
  BitArray bitmap=((Func<BitArray>)dm.CreateDelegate(typeof(Func<BitArray>)))();
  Check(bitmap.Length==65024&&!bitmap[65023],"Replayed allocation failed"); Console.WriteLine("PASS actual constructor allocation fragment");
 }
 static void GuardReplay(MethodBase method,List<CodeInstruction> code) {
  int index=-1;for(int i=0;i<code.Count;i++) if(code[i].LoadsConstant(65024)) index=i;
  DynamicMethod dm=new DynamicMethod("guard",typeof(bool),new Type[]{typeof(int)},typeof(TMCEDistanceTests),true); ILGenerator il=dm.GetILGenerator();
  foreach(LocalVariableInfo local in method.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
  CodeInstruction store=code[index-1];
  il.Emit(OpCodes.Ldarg_0);EmitLocal(il,store);
  for(int i=index-5;i<=index;i++) {
   CodeInstruction c=code[i];if(c.opcode.Name.StartsWith("ldloc")||c.opcode.Name.StartsWith("stloc"))EmitLocal(il,c);
   else if(c.operand==null)il.Emit(c.opcode);else il.Emit(c.opcode,Convert.ToInt32(c.operand));
  }
  Label success=il.DefineLabel();il.Emit(code[index+1].opcode,success);il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ret);il.MarkLabel(success);il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Ret);
  Func<int,bool> guard=(Func<int,bool>)dm.CreateDelegate(typeof(Func<int,bool>));
  Check(guard(40000)&&guard(65023)&&guard(65024)&&!guard(65025)&&!guard(65535),"Search budget boundary incorrect");
  Console.WriteLine("PASS actual post-increment guard: 40000/65023/65024 continue; 65025/65535 terminate (original comparison semantics retained)");
 }
 static void EmitLocal(ILGenerator il,CodeInstruction c) {
  if(c.operand==null)il.Emit(c.opcode);else { LocalVariableInfo local=c.operand as LocalVariableInfo;il.Emit(c.opcode,(short)(local==null?Convert.ToInt32(c.operand):local.LocalIndex)); }
 }
 static int Main(string[] args) { try {
  AppDomain.CurrentDomain.AssemblyResolve+=Resolve;Assembly assembly=Assembly.LoadFrom(args[0]);
  Console.WriteLine("DLL "+assembly.FullName+" MVID="+assembly.ManifestModule.ModuleVersionId);
  MethodBase[] targets=TMCECompatibility.Prepare(assembly);Check(targets.Length==2,"Wrong target count");
  ConstructorReplay(Positive(targets[0])); GuardReplay(targets[1],Positive(targets[1]));
  foreach(MethodBase target in targets) { List<CodeInstruction> code=Original(target);int index=Anchor(code);
   List<CodeInstruction> bad=Copy(code);bad[index].operand=16384;Reject(target,bad,"missing limit "+target.Name);
  }
  List<CodeInstruction> ctor=Original(targets[0]);int ci=Anchor(ctor);
  List<CodeInstruction> duplicate=Copy(ctor);duplicate.AddRange(Copy(ctor));Reject(targets[0],duplicate,"duplicate constructor assignments");
  List<CodeInstruction> wrongField=Copy(ctor);wrongField[ci+2].operand=targets[0].DeclaringType.GetField("m_sortedNodes",All);Reject(targets[0],wrongField,"wrong bitmap field");
  List<CodeInstruction> search=Original(targets[1]);int si=Anchor(search);
  int queueOffset=si+5;while(queueOffset<search.Count&&search[queueOffset].opcode!=OpCodes.Ldarg_0)queueOffset++;
  List<CodeInstruction> branch=Copy(search);branch[si+1].opcode=OpCodes.Bge;Reject(targets[1],branch,"reversed guard branch");
  List<CodeInstruction> destination=Copy(search);destination[si+1].operand=new DynamicMethod("other",typeof(void),Type.EmptyTypes).GetILGenerator().DefineLabel();Reject(targets[1],destination,"unknown branch target");
  List<CodeInstruction> increment=Copy(search);increment[si-3].opcode=OpCodes.Ldc_I4_2;Reject(targets[1],increment,"changed counter stride");
  List<CodeInstruction> queue=Copy(search);queue[queueOffset+1].operand=targets[1].DeclaringType.GetField("m_visited",All);Reject(targets[1],queue,"wrong queue field");
  List<CodeInstruction> queueCall=Copy(search);queueCall[queueOffset+2].operand=typeof(string).GetProperty("Length").GetGetMethod();Reject(targets[1],queueCall,"wrong queue count accessor");
  List<CodeInstruction> duplicateSearch=Copy(search);duplicateSearch.AddRange(Copy(search));Reject(targets[1],duplicateSearch,"duplicate search traversal");
  List<CodeInstruction> badMessage=Copy(search);badMessage[si+2].operand="Some other limit";Reject(targets[1],badMessage,"unrecognized diagnostic anchor");
  List<CodeInstruction> localType=Copy(search);ILGenerator fake=new DynamicMethod("badlocal",typeof(void),Type.EmptyTypes).GetILGenerator();
  int counter=search[si-5].operand is LocalVariableInfo?((LocalVariableInfo)search[si-5].operand).LocalIndex:search[si-5].opcode==OpCodes.Ldloc_0?0:search[si-5].opcode==OpCodes.Ldloc_1?1:search[si-5].opcode==OpCodes.Ldloc_2?2:search[si-5].opcode==OpCodes.Ldloc_3?3:Convert.ToInt32(search[si-5].operand);
  LocalBuilder wrong=null;for(int i=0;i<=counter;i++)wrong=fake.DeclareLocal(typeof(ushort));localType[si-5]=new CodeInstruction(OpCodes.Ldloc,wrong);Reject(targets[1],localType,"counter local type mismatch");
  BitArray bitmap=new BitArray(32768,true),same=bitmap;TMCECompatibility.EnsureBitmapPrefix(ref bitmap);
  Check(Object.ReferenceEquals(same,bitmap)&&bitmap.Length==65024&&bitmap[32767]&&!bitmap[32768],"Growth lost bits or replaced owner");
  FieldInfo backing=null;foreach(FieldInfo field in typeof(BitArray).GetFields(All))if(!field.IsStatic&&field.FieldType==typeof(int[]))backing=field;
  Check(backing!=null,"BitArray backing storage unavailable");object storage=backing.GetValue(bitmap);
  for(int i=0;i<1000;i++)TMCECompatibility.EnsureBitmapPrefix(ref bitmap);Check(Object.ReferenceEquals(same,bitmap)&&Object.ReferenceEquals(storage,backing.GetValue(bitmap)),"Repeated call allocated bitmap storage");
  BitArray larger=new BitArray(65536);TMCECompatibility.EnsureBitmapPrefix(ref larger);Check(larger.Length==65536,"Larger bitmap shrunk");
  bool nullRejected=false;BitArray empty=null;try{TMCECompatibility.EnsureBitmapPrefix(ref empty);}catch(InvalidOperationException){nullRejected=true;}Check(nullRejected,"Null bitmap accepted");
  object instance=FormatterServices.GetUninitializedObject(targets[0].DeclaringType);FieldInfo visited=targets[0].DeclaringType.GetField("m_visited",All);MethodInfo update=targets[0].DeclaringType.GetMethod("UpdateNode",All);
  visited.SetValue(instance,new BitArray(32768,true));bool originalFailure=false;try{update.Invoke(instance,new object[]{(ushort)40000,(ushort)1,1f});}catch(TargetInvocationException e){originalFailure=e.InnerException is ArgumentOutOfRangeException;}Check(originalFailure,"Original high ID error not reproduced");
  bitmap=(BitArray)visited.GetValue(instance);TMCECompatibility.EnsureBitmapPrefix(ref bitmap);bitmap.SetAll(true);visited.SetValue(instance,bitmap);
  foreach(ushort id in new ushort[]{32767,32768,40000,65023})update.Invoke(instance,new object[]{id,(ushort)1,1f});
  Console.WriteLine("PASS actual UpdateNode: original 40000 fails; prepared 32767/32768/40000/65023 succeed");
  FieldInfo[] fixture=new FieldInfo[7];string[] names={"A","B","C","D","E","F","Init"};
  for(int i=0;i<fixture.Length;i++){fixture[i]=typeof(CacheFixture).GetField(names[i],All);fixture[i].SetValue(null,i<6?(object)new object():(object)false);}
  TMCECompatibility.ResetLoadCaches(fixture);TMCECompatibility.ResetLoadCaches(fixture);
  for(int i=0;i<6;i++)Check(fixture[i].GetValue(null)==null,"Old city cache retained");Check((bool)fixture[6].GetValue(null),"Incoming-city node initializer not scheduled");
  Console.WriteLine("PASS fixture lifecycle reset: six old graph references cleared, initialization deferred, repeat safe; actual cache static initializers not executed");
  Console.WriteLine("PASS TMCE distance regression; no singleton initialization, native patch installation, complete worker, save or game mutation.");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;} }
}
