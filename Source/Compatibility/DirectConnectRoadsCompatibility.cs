using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class DirectConnectRoadsCompatibility
    {
        internal static readonly string[] Targets = { "UpdateAllNodes", "UpdateAllNetworkRenderers",
            "FastUpdateAllRoadJunctions", "FullUpdateAllRoadJunctions", "UpdateAllNodeRenderers", "UpdateAllNodeRenderersFor" };
        private static bool _installed;
        internal static void Reset() { _installed = false; }
        internal static void Apply(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("DirectConnectRoads");
            if (assembly == null) return;
            Type type = assembly.GetType("DirectConnectRoads.Util.NetInfoUtil", true);
            List<MethodInfo> methods = new List<MethodInfo>();
            foreach (string name in Targets)
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, name == "UpdateAllNodeRenderersFor" ? new[] { typeof(NetInfo) } : Type.EmptyTypes, null);
                if (method == null || method.ReturnType != typeof(void)) throw new InvalidOperationException("DCR refresh signature changed: " + name);
                Rewrite(method, PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null));
                methods.Add(method);
            }
            MethodInfo patch = typeof(DirectConnectRoadsCompatibility).GetMethod("Transpiler", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo begin = typeof(DirectConnectRoadsCompatibility).GetMethod("BeginRefresh", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo end = typeof(DirectConnectRoadsCompatibility).GetMethod("EndRefresh", BindingFlags.NonPublic | BindingFlags.Static);
            List<MethodInfo> attempted = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo method in methods) { attempted.Add(method); harmony.Patch(method, new HarmonyMethod(begin), null, new HarmonyMethod(patch), new HarmonyMethod(end)); }
                _installed = true;
                Logging.Info("DirectConnectRoads bulk node refresh covers actual network capacity (6 methods/6 bounds).");
            }
            catch { foreach (MethodInfo method in attempted) { harmony.Unpatch(method, patch); harmony.Unpatch(method, begin); harmony.Unpatch(method, end); } throw; }
        }
        private static void BeginRefresh(MethodBase __originalMethod, out System.Diagnostics.Stopwatch __state)
        {
            __state = System.Diagnostics.Stopwatch.StartNew();
            Logging.Info("DCR bulk refresh BEGIN: " + __originalMethod.Name);
        }
        private static Exception EndRefresh(MethodBase __originalMethod, System.Diagnostics.Stopwatch __state, Exception __exception)
        {
            Logging.Info("DCR bulk refresh END: " + __originalMethod.Name + "; elapsedMs="
                + (__state == null ? -1 : __state.ElapsedMilliseconds) + "; failed=" + (__exception != null));
            return __exception;
        }
        private static IEnumerable<CodeInstruction> Transpiler(MethodBase __originalMethod, IEnumerable<CodeInstruction> instructions)
        { return Rewrite(__originalMethod, instructions); }

        internal static List<CodeInstruction> Rewrite(MethodBase method, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>();
            foreach (CodeInstruction c in instructions) code.Add(new CodeInstruction(c));
            int bound = -1;
            for (int i = 0; i < code.Count; ++i)
                if (code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand, 32768))
                { if (bound >= 0) throw new InvalidOperationException("Ambiguous DCR node bound."); bound = i; }
            int b = bound;
            if (b < 7 || b + 4 >= code.Count) throw new InvalidOperationException("DCR node bound missing.");
            int counter = Local(code[b - 1], false);
            int flag = Local(code[b + 2], true);
            if (counter < 0 || counter >= method.GetMethodBody().LocalVariables.Count
                || method.GetMethodBody().LocalVariables[counter].LocalType != typeof(ushort)
                || Local(code[b - 6], false) != counter || code[b - 5].opcode != OpCodes.Ldc_I4_1
                || code[b - 4].opcode != OpCodes.Add || code[b - 3].opcode != OpCodes.Conv_U2
                || Local(code[b - 2], true) != counter || code[b + 1].opcode != OpCodes.Clt
                || flag < 0 || Local(code[b + 3], false) != flag
                || (code[b + 4].opcode != OpCodes.Brtrue && code[b + 4].opcode != OpCodes.Brtrue_S)
                || !(code[b + 4].operand is Label)) throw new InvalidOperationException("DCR node counter changed.");
            int body = code.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains((Label)code[b + 4].operand); });
            int stores = 0, seed = -1; bool root = false;
            for (int i = 0; i < code.Count; ++i)
            {
                if (Local(code[i], true) == counter) { ++stores; if (i > 0 && code[i - 1].opcode == OpCodes.Ldc_I4_0) seed = i; }
                MethodInfo call = code[i].operand as MethodInfo;
                if (i > body && i < b - 6 && call != null && call.DeclaringType.FullName == "KianCommons.NetUtil"
                    && call.Name == "ToNode" && Local(code[i - 1], false) == counter) root = true;
            }
            if (body < 0 || body >= b - 6 || seed < 0 || seed >= body || stores != 2 || !root
                || (code[seed + 1].opcode != OpCodes.Br && code[seed + 1].opcode != OpCodes.Br_S)
                || !(code[seed + 1].operand is Label) || !code[b - 1].labels.Contains((Label)code[seed + 1].operand))
                throw new InvalidOperationException("DCR node traversal root changed.");
            code[b].opcode = OpCodes.Call;
            code[b].operand = typeof(IntersectionMarkingCompatibility).GetMethod("NodeCount", BindingFlags.Static | BindingFlags.NonPublic);
            return code;
        }
        private static int Local(CodeInstruction c, bool store)
        {
            if (c.opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (c.opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (c.opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (c.opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (c.opcode != (store ? OpCodes.Stloc : OpCodes.Ldloc) && c.opcode != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            LocalVariableInfo local = c.operand as LocalVariableInfo;
            return local != null ? local.LocalIndex : Convert.ToInt32(c.operand);
        }
    }
}
