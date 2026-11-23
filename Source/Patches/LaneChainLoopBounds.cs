using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Reviewed lane-node unlink and nearby-node refresh traversals. The latter
    // start at nodeGrid cells, not lanes; both traverse NetNode links, never IDs.
    internal static class LaneChainLoopBounds
    {
        private static readonly string[] RemoveTypes = new[] {
            "CargoPathAI", "PedestrianBridgeAI", "PedestrianPathAI", "PedestrianTunnelAI",
            "PedestrianWayAI", "RunwayAI", "ShipDockAI", "TransportLineAI", "TransportPathAI" };
        private static readonly string[] UpdateTypes = new[] {
            "CargoPathAI", "ConcourseBaseAI", "FlightPathAI", "MetroTrackBaseAI", "PedestrianBridgeAI",
            "PedestrianPathAI", "PedestrianTunnelAI", "PedestrianWayAI", "RoadBaseAI", "ShipPathAI",
            "TaxiwayAI", "TrainTrackBaseAI", "TransportPathAI" };

        internal static bool Handles(MethodBase original)
        {
            MethodInfo method = original as MethodInfo;
            if (method == null || method.IsStatic || method.ReturnType != typeof(void)
                || method.DeclaringType == null || method.DeclaringType.Assembly != typeof(NetManager).Assembly)
                return false;
            ParameterInfo[] p = method.GetParameters();
            bool remove = method.Name == "RemoveLaneConnection";
            bool update = method.Name == "UpdateLanes";
            if (!remove && !update) return false;
            if (Array.IndexOf(remove ? RemoveTypes : UpdateTypes, method.DeclaringType.FullName) < 0) return false;
            return p.Length == (remove ? 2 : 3) && p[0].ParameterType == typeof(ushort)
                && p[1].ParameterType == (remove ? typeof(NetNode) : typeof(NetSegment)).MakeByRefType()
                && (remove || p[2].ParameterType == typeof(bool));
        }

        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null) return null;
            int found = -1;
            for (int i = 0; i < code.Count; ++i)
                if (code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand, 32768)
                    && Guard(original, code, i))
                {
                    if (found >= 0) return null;
                    found = i;
                }
            return found < 0 ? null : new[] { found };
        }

        private static bool Guard(MethodBase original, List<CodeInstruction> code, int bound)
        {
            bool remove = original.Name == "RemoveLaneConnection";
            bool transport = original.DeclaringType == typeof(TransportLineAI);
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, remove ? OpCodes.Ble : OpCodes.Blt)) return false;
            int condition = Skip(code, LabelIndex(code, code[branch].operand));
            if (condition <= branch) return false;
            int cursor = Local(code[condition], false), back = Next(code, condition);
            if (cursor < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return false;
            int start = Skip(code, LabelIndex(code, code[back].operand));
            if (start < 0 || start >= bound) return false;
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            if (load < start || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            int counter = Local(code[store], true);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (counter < 0 || counter == cursor || Local(code[load], false) != counter
                || counter >= locals.Count || cursor >= locals.Count
                || VariableType(code[store], counter, locals) != typeof(int)
                || VariableType(code[condition], cursor, locals) != typeof(ushort)) return false;
            int initialJump = Previous(code, start), initialStore = Previous(code, initialJump);
            int zero = Previous(code, initialStore), seed = Previous(code, zero);
            if (seed < 0 || !Branch(code[initialJump].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[initialJump].operand)) != condition
                || Local(code[initialStore], true) != counter || code[zero].opcode != OpCodes.Ldc_I4_0
                || Local(code[seed], true) != cursor) return false;
            int manager;
            if (!Seed(code, seed, remove, locals, out manager)) return false;
            int h = Next(code, branch);
            if (h < 0 || code[h].opcode != OpCodes.Ldc_I4_1) return false;
            h = Next(code, h);
            if (h < 0 || code[h].opcode != OpCodes.Ldstr || !object.Equals(code[h].operand, "Invalid list detected!\n")) return false;
            h = Next(code, h);
            if (!Call(code, h, typeof(Environment), "get_StackTrace")) return false;
            h = Next(code, h);
            MethodInfo concat = h < 0 ? null : code[h].operand as MethodInfo;
            if (!Call(code, h, typeof(string), "Concat") || concat.ReturnType != typeof(string)
                || concat.GetParameters().Length != 2) return false;
            h = Next(code, h);
            MethodInfo error = h < 0 ? null : code[h].operand as MethodInfo;
            if (h < 0 || code[h].opcode != OpCodes.Call || error == null || error.Name != "Error"
                || error.DeclaringType == null || !error.DeclaringType.IsGenericType
                || error.DeclaringType.GetGenericTypeDefinition().FullName != "ColossalFramework.CODebugBase`1") return false;
            h = Next(code, h);
            if (h < 0 || !Branch(code[h].opcode, OpCodes.Br) || Next(code, h) != condition
                || Skip(code, LabelIndex(code, code[h].operand)) != Next(code, back)) return false;
            int update = Previous(code, load);
            if (update < start || Local(code[update], true) != cursor
                || !NodeUpdate(code, update, cursor, start, manager, remove ? "m_nextLaneNode" : "m_nextGridNode")) return false;
            int alternate = -1;
            for (int i = start; i < update; ++i)
            {
                if (Local(code[i], true) == counter || Address(code[i]) == counter || Address(code[i]) == cursor) return false;
                if (Local(code[i], true) != cursor) continue;
                // TransportLineAI continues after unlinking the selected node.
                // It advances from that exact by-ref node's saved next link.
                int field = Previous(code, i), argument = Previous(code, field), jump = Next(code, i);
                if (!transport || alternate >= 0 || argument < start || code[argument].opcode != OpCodes.Ldarg_2
                    || !Field(code[field], typeof(NetNode), "m_nextLaneNode") || jump < 0
                    || !Branch(code[jump].opcode, OpCodes.Br)
                    || Skip(code, LabelIndex(code, code[jump].operand)) != load) return false;
                alternate = i;
            }
            return !transport || alternate >= 0;
        }

        private static bool Seed(List<CodeInstruction> code, int store, bool remove,
            IList<LocalVariableInfo> locals, out int manager)
        {
            manager = -1;
            int end = Previous(code, store), source = -1;
            if (remove)
            {
                int element = Previous(code, end), conversion = Previous(code, element);
                int lane = Previous(code, conversion), node = Previous(code, lane);
                int buffer = Previous(code, node), pool = Previous(code, buffer);
                source = Previous(code, pool);
                if (source < 0 || !Field(code[end], typeof(NetLane), "m_nodes")
                    || code[element].opcode != OpCodes.Ldelema || !object.Equals(code[element].operand, typeof(NetLane))
                    || code[conversion].opcode != OpCodes.Conv_U || !Field(code[lane], typeof(NetNode), "m_lane")
                    || code[node].opcode != OpCodes.Ldarg_2 || !Field(code[pool], typeof(NetManager), "m_lanes")
                    || !Field(code[buffer], typeof(Array32<NetLane>), "m_buffer")) return false;
            }
            else
            {
                int add = Previous(code, end), x = Previous(code, add), multiply = Previous(code, x);
                int width = Previous(code, multiply), z = Previous(code, width), grid = Previous(code, z);
                source = Previous(code, grid);
                int xLocal = x < 0 ? -1 : Local(code[x], false), zLocal = z < 0 ? -1 : Local(code[z], false);
                if (source < 0 || code[end].opcode != OpCodes.Ldelem_U2 || code[add].opcode != OpCodes.Add
                    || code[multiply].opcode != OpCodes.Mul || code[width].opcode != OpCodes.Ldc_I4
                    || !object.Equals(code[width].operand, 270) || !Field(code[grid], typeof(NetManager), "m_nodeGrid")
                    || xLocal < 0 || zLocal < 0 || xLocal == zLocal || xLocal >= locals.Count || zLocal >= locals.Count
                    || VariableType(code[x], xLocal, locals) != typeof(int)
                    || VariableType(code[z], zLocal, locals) != typeof(int)) return false;
            }
            manager = Local(code[source], false);
            return manager >= 0 && manager < locals.Count && VariableType(code[source], manager, locals) == typeof(NetManager);
        }

        private static bool NodeUpdate(List<CodeInstruction> code, int store, int cursor, int start, int manager, string next)
        {
            int field = Previous(code, store), element = Previous(code, field), index = Previous(code, element);
            int buffer = Previous(code, index), pool = Previous(code, buffer), source = Previous(code, pool);
            return source >= start && Local(code[source], false) == manager
                && Field(code[pool], typeof(NetManager), "m_nodes")
                && Field(code[buffer], typeof(Array16<NetNode>), "m_buffer")
                && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema
                && object.Equals(code[element].operand, typeof(NetNode)) && Field(code[field], typeof(NetNode), next);
        }
        private static bool Field(CodeInstruction code, Type type, string name)
        {
            FieldInfo field = code.operand as FieldInfo;
            return code.opcode == OpCodes.Ldfld && field != null && field.Name == name
                && (type == null || field.DeclaringType == type);
        }
        private static bool Call(List<CodeInstruction> code, int index, Type type, string name)
        {
            MethodInfo call = index < 0 ? null : code[index].operand as MethodInfo;
            return call != null && code[index].opcode == OpCodes.Call && call.DeclaringType == type && call.Name == name;
        }
        private static bool Branch(OpCode actual, OpCode op)
        {
            return actual == op || (op == OpCodes.Br && actual == OpCodes.Br_S)
                || (op == OpCodes.Brtrue && actual == OpCodes.Brtrue_S)
                || (op == OpCodes.Blt && actual == OpCodes.Blt_S) || (op == OpCodes.Ble && actual == OpCodes.Ble_S);
        }
        private static int LabelIndex(List<CodeInstruction> code, object operand)
        {
            if (!(operand is Label)) return -1;
            int result = -1;
            for (int i = 0; i < code.Count; ++i) if (code[i].labels.Contains((Label)operand))
            { if (result >= 0) return -1; result = i; }
            return result;
        }
        private static int Skip(List<CodeInstruction> code, int index)
        {
            if (index < 0) return -1;
            while (index < code.Count && code[index].opcode == OpCodes.Nop) ++index;
            return index < code.Count ? index : -1;
        }
        private static int Next(List<CodeInstruction> code, int index) { return index < 0 ? -1 : Skip(code, index + 1); }
        private static int Previous(List<CodeInstruction> code, int index)
        {
            --index;
            while (index >= 0 && code[index].opcode == OpCodes.Nop) --index;
            return index;
        }
        private static int Address(CodeInstruction code)
        {
            return code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S ? LocalOperand(code.operand) : -1;
        }
        private static int Local(CodeInstruction code, bool store)
        {
            OpCode op = code.opcode;
            if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            return op == (store ? OpCodes.Stloc : OpCodes.Ldloc) || op == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)
                ? LocalOperand(code.operand) : -1;
        }
        private static int LocalOperand(object operand)
        {
            LocalBuilder builder = operand as LocalBuilder;
            if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            if (operand is int) return (int)operand;
            if (operand is byte) return (byte)operand;
            if (operand is short) return (short)operand;
            return -1;
        }
        private static Type VariableType(CodeInstruction access, int index, IList<LocalVariableInfo> locals)
        {
            LocalBuilder builder = access.operand as LocalBuilder;
            if (builder != null) return builder.LocalType;
            LocalVariableInfo variable = access.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalType;
            return index >= 0 && index < locals.Count ? locals[index].LocalType : null;
        }
    }
}
