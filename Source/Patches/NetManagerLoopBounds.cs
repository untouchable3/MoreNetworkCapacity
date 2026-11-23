using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    internal static class NetManagerLoopBounds
    {
        private static readonly Dictionary<string, string> Signatures = new Dictionary<string, string> {
            { "AfterTerrainUpdate", "TerrainArea,TerrainArea,TerrainArea" },
            { "CalculateGroupData", "System.Int32,System.Int32,System.Int32,System.Int32&,System.Int32&,System.Int32&,RenderGroup/VertexArrays&" },
            { "EndRenderingImpl", "RenderManager/CameraInfo" },
            { "GetClosestSegments", "UnityEngine.Vector3,System.UInt16[],System.Int32&" },
            { "IsPriorityRoad", "System.UInt16,System.Boolean&" },
            { "LateUpdateData", "SimulationManager/UpdateMode" },
            { "OnLocaleChanged", "" },
            { "OverlapQuad", "ColossalFramework.Math.Quad2,System.Single,System.Single,ItemClass/CollisionType,ItemClass/Layer,ItemClass/Layer,System.UInt16,System.UInt16,System.UInt16,System.UInt64[]" },
            { "PopulateGroupData", "System.Int32,System.Int32,System.Int32,System.Int32&,System.Int32&,UnityEngine.Vector3,RenderGroup/MeshData,UnityEngine.Vector3&,UnityEngine.Vector3&,System.Single&,System.Single&,System.Boolean&" },
            { "RayCast", "NetInfo,ColossalFramework.Math.Segment3,System.Single,System.Boolean,ItemClass/Service,ItemClass/Service,ItemClass/SubService,ItemClass/SubService,ItemClass/Layer,ItemClass/Layer,NetNode/Flags,NetSegment/Flags,UnityEngine.Vector3&,System.UInt16&,System.UInt16&,System.Single&" },
            { "ReleaseLaneImplementation", "System.UInt32,NetLane&" },
            { "SampleSmoothHeight", "UnityEngine.Vector3" },
            { "TerrainUpdated", "TerrainArea,TerrainArea,TerrainArea" },
            { "UpdateData", "SimulationManager/UpdateMode" },        };
        internal static bool Handles(MethodBase original)
        {
            MethodInfo method = original as MethodInfo;
            string signature;
            if (method == null || method.DeclaringType != typeof(NetManager) || method.IsStatic
                || !Signatures.TryGetValue(method.Name, out signature)) return false;
            string actual = "";
            foreach (ParameterInfo p in method.GetParameters())
                actual += (actual.Length == 0 ? "" : ",") + p.ParameterType.FullName.Replace('+', '/');
            Type result = method.Name == "SampleSmoothHeight" ? typeof(float)
                : (method.Name == "IsPriorityRoad" || method.Name == "CalculateGroupData"
                    || method.Name == "OverlapQuad" || method.Name == "RayCast" ? typeof(bool) : typeof(void));
            return actual == signature && method.ReturnType == result;
        }

        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null || !TypedLocals(original, code)) return null;
            string name = original.Name;
            string[] expected;
            bool arrays = name == "OnLocaleChanged" || name == "UpdateData" || name == "LateUpdateData";
            if (name == "LateUpdateData") expected = new[] { "name", "visibility1", "visibility2" };
            else if (name == "OnLocaleChanged") expected = new[] { "segment" };
            else if (name == "ReleaseLaneImplementation") expected = new[] { "lane" };
            else if (name == "IsPriorityRoad") expected = new[] { "start", "end" };
            else if (name == "GetClosestSegments" || name == "OverlapQuad" || name == "SampleSmoothHeight") expected = new[] { "segment" };
            else expected = new[] { "node", "segment" };
            Dictionary<string, int> found = new Dictionary<string, int>();
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || !(code[i].operand is int)) continue;
                int value = (int)code[i].operand;
                if (value != 32768 && value != 36864) continue;
                string role = arrays ? ArrayGuard(original, code, i)
                    : ChainGuard(original, code, i);
                if (role == null) continue;
                if (Array.IndexOf(expected, role) < 0 || found.ContainsKey(role)) return null;
                found.Add(role, i);
            }
            List<int> indexes = new List<int>();
            foreach (string role in expected)
            {
                int index;
                if (!found.TryGetValue(role, out index)) return null;
                indexes.Add(index);
            }
            indexes.Sort();
            return indexes.ToArray();
        }

        // Harmony operands can carry LocalBuilder/LocalVariableInfo type metadata.
        // Same numeric indexes with changed types are not the reviewed locals.
        // Unrelated newly declared locals are allowed, but not untyped new indexes.
        private static bool TypedLocals(MethodBase original, List<CodeInstruction> code)
        {
            MethodBody body = original.GetMethodBody();
            if (body == null) return false;
            IList<LocalVariableInfo> locals = body.LocalVariables;
            Dictionary<int, Type> extras = new Dictionary<int, Type>();
            foreach (CodeInstruction instruction in code)
            {
                int local = Local(instruction, false);
                if (local < 0) local = Local(instruction, true);
                if (local < 0) local = Address(instruction);
                string operation = instruction.opcode.Name ?? "";
                if (!operation.StartsWith("ldloc") && !operation.StartsWith("stloc")) continue;
                if (local < 0) return false;
                if (local < locals.Count)
                {
                    if (VariableType(instruction, local, locals) != locals[local].LocalType) return false;
                }
                else
                {
                    LocalBuilder builder = instruction.operand as LocalBuilder;
                    LocalVariableInfo variable = instruction.operand as LocalVariableInfo;
                    Type declared = builder != null ? builder.LocalType : variable != null ? variable.LocalType : null;
                    if (declared == null) return false;
                    Type previous;
                    if (extras.TryGetValue(local, out previous) && previous != declared) return false;
                    extras[local] = declared;
                }
            }
            return true;
        }

        private sealed class Chain
        {
            internal int Cursor, Counter, Start, Load, Condition, Back, Zero;
        }
        private static string ChainGuard(MethodBase original, List<CodeInstruction> code, int bound)
        {
            bool priority = original.Name == "IsPriorityRoad", lane = original.Name == "ReleaseLaneImplementation";
            bool segment = object.Equals(code[bound].operand, 36864);
            if ((priority && !segment) || (lane && segment)) return null;
            OpCode predicate = lane || original.Name == "RayCast" ? OpCodes.Ble : OpCodes.Blt;
            Chain chain = Header(original, code, bound, predicate, priority);
            if (chain == null) return null;
            if (priority) return Priority(original, code, chain);
            if (lane) return Lane(original, code, chain) ? "lane" : null;
            if (!GridSeed(original, code, chain, segment)) return null;
            int update = Previous(code, chain.Load);
            if (update < chain.Start || Local(code[update], true) != chain.Cursor
                || !Link(code, update, chain.Cursor, segment ? typeof(NetSegment) : typeof(NetNode),
                    segment ? "m_nextGridSegment" : "m_nextGridNode", chain.Start)) return null;
            for (int i = chain.Start; i < update; ++i)
                if (Local(code[i], true) == chain.Cursor || Local(code[i], true) == chain.Counter) return null;
            return segment ? "segment" : "node";
        }

        private static Chain Header(MethodBase original, List<CodeInstruction> code, int bound, OpCode predicate, bool leave)
        {
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, predicate)) return null;
            int condition = Skip(code, LabelIndex(code, code[branch].operand));
            if (condition <= branch) return null;
            int cursor = Local(code[condition], false), back = Next(code, condition);
            if (cursor < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return null;
            int start = Skip(code, LabelIndex(code, code[back].operand));
            if (start < 0 || start >= bound) return null;
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            int counter = store < 0 ? -1 : Local(code[store], true);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (load < start || counter < 0 || counter == cursor || counter >= locals.Count || cursor >= locals.Count
                || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1
                || Local(code[load], false) != counter || VariableType(code[store], counter, locals) != typeof(int)
                || VariableType(code[condition], cursor, locals) != typeof(ushort)) return null;
            int jump = Previous(code, start), initial = Previous(code, jump), zero = Previous(code, initial);
            if (zero < 0 || !Branch(code[jump].opcode, OpCodes.Br) || Local(code[initial], true) != counter
                || code[zero].opcode != OpCodes.Ldc_I4_0
                || Skip(code, LabelIndex(code, code[jump].operand)) != condition) return null;
            for (int i = start; i <= back; ++i)
                if (Address(code[i]) == cursor || Address(code[i]) == counter
                    || (i != store && Local(code[i], true) == counter)) return null;
            if (!Handler(code, branch, condition, back, leave)) return null;
            return new Chain { Cursor = cursor, Counter = counter, Start = start, Load = load,
                Condition = condition, Back = back, Zero = zero };
        }

        private static bool Handler(List<CodeInstruction> code, int branch, int condition, int back, bool leave)
        {
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
            return h >= 0 && (leave ? code[h].opcode == OpCodes.Leave || code[h].opcode == OpCodes.Leave_S
                : Branch(code[h].opcode, OpCodes.Br)) && Next(code, h) == condition
                && Skip(code, LabelIndex(code, code[h].operand)) == Next(code, back);
        }

        private static bool GridSeed(MethodBase original, List<CodeInstruction> code, Chain chain, bool segment)
        {
            int store = Previous(code, chain.Zero), element = Previous(code, store), index = Previous(code, element);
            if (index < 0 || Local(code[store], true) != chain.Cursor || code[element].opcode != OpCodes.Ldelem_U2) return false;
            int grid;
            if (Local(code[index], false) >= 0)
            {
                grid = Previous(code, index);
                int local = Local(code[index], false), init = LastStore(code, local, index);
                int expr = Previous(code, init);
                if (init < 0 || !GridIndex(original, code, expr)) return false;
                for (int i = init + 1; i < index; ++i) if (Address(code[i]) == local) return false;
            }
            else
            {
                if (!GridIndex(original, code, index)) return false;
                grid = Previous(code, Previous(code, Previous(code, Previous(code, Previous(code, index)))));
            }
            int self = Previous(code, grid);
            return self >= 0 && code[self].opcode == OpCodes.Ldarg_0
                && Field(code[grid], typeof(NetManager), segment ? "m_segmentGrid" : "m_nodeGrid");
        }
        private static bool GridIndex(MethodBase original, List<CodeInstruction> code, int add)
        {
            int x = Previous(code, add), mul = Previous(code, x), width = Previous(code, mul), z = Previous(code, width);
            if (z < 0 || code[add].opcode != OpCodes.Add || code[mul].opcode != OpCodes.Mul
                || code[width].opcode != OpCodes.Ldc_I4 || !object.Equals(code[width].operand, 270)) return false;
            int a = Local(code[x], false), b = Local(code[z], false);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            return a >= 0 && b >= 0 && a != b && a < locals.Count && b < locals.Count
                && VariableType(code[x], a, locals) == typeof(int) && VariableType(code[z], b, locals) == typeof(int);
        }
        private static int LastStore(List<CodeInstruction> code, int local, int end)
        {
            for (int i = end - 1; i >= 0; --i) if (Local(code[i], true) == local) return i;
            return -1;
        }
        // Match a self-owned live pool element, not a similarly named cached array.
        private static bool Element(List<CodeInstruction> code, int element, int cursor, Type type, int start)
        {
            int index = Previous(code, element), buffer = Previous(code, index), pool = Previous(code, buffer), self = Previous(code, pool);
            return self >= start && code[self].opcode == OpCodes.Ldarg_0
                && Field(code[pool], typeof(NetManager), type == typeof(NetNode) ? "m_nodes" : "m_segments")
                && Field(code[buffer], typeof(Array16<>).MakeGenericType(type), "m_buffer")
                && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema
                && object.Equals(code[element].operand, type);
        }
        private static bool Link(List<CodeInstruction> code, int store, int cursor, Type type, string name, int start)
        {
            int field = Previous(code, store), element = Previous(code, field);
            return field >= 0 && Field(code[field], type, name) && Element(code, element, cursor, type, start);
        }

        private static bool Lane(MethodBase original, List<CodeInstruction> code, Chain chain)
        {
            int seed = LastStore(code, chain.Cursor, chain.Zero), field = Previous(code, seed), argument = Previous(code, field);
            if (argument < 0 || code[argument].opcode != OpCodes.Ldarg_2 || !Field(code[field], typeof(NetLane), "m_nodes")) return false;
            for (int i = seed + 1; i < chain.Zero; ++i)
                if (Local(code[i], true) == chain.Cursor || Address(code[i]) == chain.Cursor) return false;
            int update = Previous(code, chain.Load), source = Previous(code, update);
            if (source < chain.Start || Local(code[update], true) != chain.Cursor) return false;
            int temporary = Local(code[source], false), capture = LastStore(code, temporary, source);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (temporary < 0 || temporary == chain.Cursor || temporary == chain.Counter
                || temporary >= locals.Count || VariableType(code[source], temporary, locals) != typeof(ushort)
                || !Link(code, capture, chain.Cursor, typeof(NetNode), "m_nextLaneNode", chain.Start)) return false;
            for (int i = chain.Start; i < update; ++i)
                if (Local(code[i], true) == chain.Cursor || Address(code[i]) == temporary
                    || i != capture && Local(code[i], true) == temporary) return false;
            return true;
        }

        private static string Priority(MethodBase original, List<CodeInstruction> code, Chain chain)
        {
            int seed = Previous(code, chain.Zero), field = Previous(code, seed), element = Previous(code, field);
            int index = Previous(code, element), buffer = Previous(code, index), pool = Previous(code, buffer), self = Previous(code, pool);
            FieldInfo end = field < 0 ? null : code[field].operand as FieldInfo;
            if (self < 0 || Local(code[seed], true) != chain.Cursor || code[self].opcode != OpCodes.Ldarg_0
                || !Field(code[pool], typeof(NetManager), "m_segments")
                || !Field(code[buffer], typeof(Array16<NetSegment>), "m_buffer") || code[index].opcode != OpCodes.Ldarg_1
                || code[element].opcode != OpCodes.Ldelema || !object.Equals(code[element].operand, typeof(NetSegment))
                || end == null || !Field(code[field], typeof(NetSegment), end.Name)
                || end.Name != "m_startNode" && end.Name != "m_endNode") return null;
            int update = Previous(code, chain.Load), source = Previous(code, update), temp = source < 0 ? -1 : Local(code[source], false);
            int capture = LastStore(code, temp, source), call = Previous(code, capture), cursor = Previous(code, call), candidateElement = Previous(code, cursor);
            MethodInfo other = call < 0 ? null : code[call].operand as MethodInfo;
            if (temp < 0 || temp == chain.Cursor || temp == chain.Counter || Local(code[update], true) != chain.Cursor
                || other == null || code[call].opcode != OpCodes.Call || other.DeclaringType != typeof(NetSegment)
                || other.Name != "GetOtherNode" || other.IsStatic || other.ReturnType != typeof(ushort)
                || other.GetParameters().Length != 1 || other.GetParameters()[0].ParameterType != typeof(ushort)
                || Local(code[cursor], false) != chain.Cursor) return null;
            int segmentLoad = Previous(code, candidateElement), candidate = segmentLoad < 0 ? -1 : Local(code[segmentLoad], false);
            if (candidate < 0 || !Element(code, candidateElement, candidate, typeof(NetSegment), chain.Start)) return null;
            int segmentStore = LastStore(code, candidate, segmentLoad), get = Previous(code, segmentStore), slotLoad = Previous(code, get), node = Previous(code, slotLoad);
            MethodInfo getSegment = get < 0 ? null : code[get].operand as MethodInfo;
            if (getSegment == null || code[get].opcode != OpCodes.Call || getSegment.DeclaringType != typeof(NetNode)
                || getSegment.Name != "GetSegment" || getSegment.ReturnType != typeof(ushort) || getSegment.IsStatic
                || getSegment.GetParameters().Length != 1 || getSegment.GetParameters()[0].ParameterType != typeof(int)
                || !Element(code, node, chain.Cursor, typeof(NetNode), chain.Start)) return null;
            int slot = Local(code[slotLoad], false), slotInit = LastStore(code, slot, node), zero = Previous(code, slotInit);
            int tempInit = Previous(code, zero), tempZero = Previous(code, tempInit);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (slot < 0 || tempZero < 0 || slot >= locals.Count || temp >= locals.Count || candidate >= locals.Count
                || VariableType(code[slotLoad], slot, locals) != typeof(int)
                || VariableType(code[source], temp, locals) != typeof(ushort)
                || VariableType(code[segmentLoad], candidate, locals) != typeof(ushort)
                || code[zero].opcode != OpCodes.Ldc_I4_0 || Local(code[tempInit], true) != temp
                || code[tempZero].opcode != OpCodes.Ldc_I4_0 || Skip(code, tempZero) != chain.Start) return null;
            for (int i = chain.Start; i < update; ++i)
                if (Local(code[i], true) == chain.Cursor || Address(code[i]) == temp
                    || i != capture && i != tempInit && Local(code[i], true) == temp) return null;
            // The inner search stops at8 slots or a found next node, then copies
            // precisely that node into the outer cursor before counting a step.
            int innerBranch = Previous(code, source), checkTemp = Previous(code, innerBranch), slotBranch = Previous(code, checkTemp);
            int eight = Previous(code, slotBranch), checkSlot = Previous(code, eight);
            if (!Branch(code[innerBranch].opcode, OpCodes.Brfalse) || Local(code[checkTemp], false) != temp
                || code[slotBranch].opcode != OpCodes.Bge && code[slotBranch].opcode != OpCodes.Bge_S
                || code[eight].opcode != OpCodes.Ldc_I4_8 || Local(code[checkSlot], false) != slot
                || Skip(code, LabelIndex(code, code[slotBranch].operand)) != source) return null;
            int innerStart = Skip(code, LabelIndex(code, code[innerBranch].operand));
            int getSelf = Previous(code, Previous(code, Previous(code, Previous(code, node))));
            int slotStore = Previous(code, checkSlot), slotAdd = Previous(code, slotStore);
            int slotOne = Previous(code, slotAdd), slotIncrement = Previous(code, slotOne), initialJump = Next(code, slotInit);
            if (innerStart != getSelf || slotIncrement < chain.Start || code[slotAdd].opcode != OpCodes.Add
                || code[slotOne].opcode != OpCodes.Ldc_I4_1 || Local(code[slotIncrement], false) != slot
                || Local(code[slotStore], true) != slot || !Branch(code[initialJump].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[initialJump].operand)) != checkSlot) return null;
            for (int i = chain.Start; i < update; ++i)
                if (Address(code[i]) == candidate || Address(code[i]) == slot
                    || i != segmentStore && Local(code[i], true) == candidate
                    || i != slotInit && i != slotStore && Local(code[i], true) == slot) return null;
            return end.Name == "m_startNode" ? "start" : "end";
        }

        private static string ArrayGuard(MethodBase original, List<CodeInstruction> code, int bound)
        {
            bool segment = object.Equals(code[bound].operand, 36864);
            int branch = Next(code, bound), check = Previous(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Blt)) return null;
            int start = Skip(code, LabelIndex(code, code[branch].operand)), counter = check < 0 ? -1 : Local(code[check], false);
            int store = Previous(code, check), add = Previous(code, store), one = Previous(code, add), load = Previous(code, one);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (start < 0 || start >= load || counter < 0 || counter >= locals.Count || Local(code[store], true) != counter
                || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1 || Local(code[load], false) != counter
                || VariableType(code[store], counter, locals) != typeof(int)) return null;
            int initialJump = Previous(code, start), initialStore = Previous(code, initialJump), initial = Previous(code, initialStore);
            if (initial < 0 || !Branch(code[initialJump].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[initialJump].operand)) != check || Local(code[initialStore], true) != counter
                || code[initial].opcode != (original.Name == "OnLocaleChanged" ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1)) return null;
            Type item = segment ? typeof(NetSegment) : typeof(NetNode);
            int pool = Next(code, start), buffer = Next(code, pool), index = Next(code, buffer), element = Next(code, index), flags = Next(code, element);
            if (flags < 0 || !Element(code, element, counter, item, start) || !Field(code[flags], item, "m_flags")) return null;
            for (int i = start; i < check; ++i)
                if (i != store && Local(code[i], true) == counter || Address(code[i]) == counter) return null;
            if (original.Name != "LateUpdateData") return segment ? "segment" : "node";
            if (!segment) return null;
            string role = null;
            for (int i = start; i < load; ++i)
            {
                MethodInfo method = code[i].operand as MethodInfo;
                if (method == null || code[i].opcode != OpCodes.Call || method.ReturnType != typeof(void)
                    || method.IsStatic || method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(ushort)) continue;
                string candidateRole = method.DeclaringType == typeof(NetSegment) && method.Name == "UpdateNameSeed" ? "name"
                    : (method.DeclaringType == typeof(NetManager) && method.Name == "UpdateNameVisibilityPass1" ? "visibility1"
                        : (method.DeclaringType == typeof(NetManager) && method.Name == "UpdateNameVisibilityPass2" ? "visibility2" : null));
                if (candidateRole == null) continue;
                int conversion = Previous(code, i), argument = Previous(code, conversion), receiver = Previous(code, argument);
                if (code[conversion].opcode != OpCodes.Conv_U2 || Local(code[argument], false) != counter
                    || (candidateRole == "name" ? !Element(code, receiver, counter, typeof(NetSegment), start)
                        : code[receiver].opcode != OpCodes.Ldarg_0) || role != null) return null;
                role = candidateRole;
            }
            return role;
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
                || (op == OpCodes.Brtrue && actual == OpCodes.Brtrue_S) || (op == OpCodes.Brfalse && actual == OpCodes.Brfalse_S)
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
