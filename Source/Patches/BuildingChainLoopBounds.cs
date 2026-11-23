using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Each reviewed bound is tied to a typed traversal, never its value alone.
    internal static class BuildingChainLoopBounds
    {
        private sealed class Shape
        {
            internal readonly Type Type;
            internal readonly string Name, Signature, Return, Rules;
            internal readonly bool Static;
            internal Shape(Type type, string name, string signature, string result, bool isStatic, string rules)
            { Type = type; Name = name; Signature = signature; Return = result; Static = isStatic; Rules = rules; }
        }
        private static readonly Shape[] Shapes = new Shape[]
        {
            new Shape(typeof(Building), "ContainsNode", "System.UInt16", "System.Boolean", false, "B"),
            new Shape(typeof(Building), "ContainsSegment", "System.UInt16", "System.Boolean", false, "B"),
            new Shape(typeof(Building), "FindAccessLane", "NetInfo+LaneType,VehicleInfo+VehicleType,VehicleInfo+VehicleCategory,UnityEngine.Vector3,UnityEngine.Vector3&,System.Single&", "System.UInt32", false, "S"),
            new Shape(typeof(Building), "FindLane", "NetInfo+LaneType,VehicleInfo+VehicleType,VehicleInfo+VehicleCategory,UnityEngine.Vector3,UnityEngine.Vector3&,System.Single&", "System.UInt32", false, "B"),
            new Shape(typeof(Building), "FindNode", "ItemClass+Service,ItemClass+SubService,ItemClass+Layer,UnityEngine.Vector3", "System.UInt16", false, "B"),
            new Shape(typeof(Building), "FindParentNode", "System.UInt16", "System.UInt16", false, "G"),
            new Shape(typeof(Building), "FindSegment", "ItemClass+Service,ItemClass+SubService,ItemClass+Layer", "System.UInt16", false, "B"),
            new Shape(typeof(BuildingDecoration), "ClearDecorations", "", "System.Void", true, "DN"),
            new Shape(typeof(BuildingDecoration), "GetBuildingSegments", "Building&", "System.UInt16[]", true, "B"),
            new Shape(typeof(BuildingDecoration), "SavePaths", "BuildingInfo,System.UInt16,Building&", "System.Void", true, "DN"),
            new Shape(typeof(BuildingManager), "FindNodeOwner", "System.UInt16", "System.UInt16", false, "B"),
            new Shape(typeof(BuildingManager), "IgnoreOverlap", "System.UInt16,System.UInt16,System.UInt16,System.UInt16", "System.Boolean", false, "P"),
            new Shape(typeof(BuildingManager), "ReleasePaths", "System.UInt16,Building&", "System.Void", false, "Bb"),
            new Shape(typeof(BuildingManager), "UpdateFlags", "System.UInt16,Building+Flags", "System.Void", false, "B"),
            new Shape(typeof(BuildingTool), "IgnoreRelocateSegments", "System.UInt16,System.UInt64[],System.UInt64[]", "System.Void", true, "BB"),
            new Shape(typeof(DefaultTool), "RenderOverlay", "RenderManager+CameraInfo", "System.Void", false, "BB"),
            new Shape(typeof(TransportLine), "ReleaseNode", "System.UInt16", "System.Void", false, "b"),
        };
        private static Shape Reviewed(MethodBase original)
        {
            MethodInfo method = original as MethodInfo;
            if (method == null) return null;
            foreach (Shape shape in Shapes)
            {
                if (method.DeclaringType != shape.Type || method.Name != shape.Name || method.IsStatic != shape.Static
                    || TypeName(method.ReturnType) != shape.Return) continue;
                List<string> args = new List<string>();
                foreach (ParameterInfo p in method.GetParameters()) args.Add(TypeName(p.ParameterType));
                if (string.Join(",", args.ToArray()) == shape.Signature) return shape;
            }
            return null;
        }
        private static string TypeName(Type type)
        {
            if (type.IsByRef) return TypeName(type.GetElementType()) + "&";
            if (type.IsArray) return TypeName(type.GetElementType()) + "[]";
            if (!type.IsGenericType) return type.FullName;
            List<string> args = new List<string>();
            foreach (Type arg in type.GetGenericArguments()) args.Add(TypeName(arg));
            return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", args.ToArray()) + ">";
        }
        internal static bool Handles(MethodBase original) { return Reviewed(original) != null; }
        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            Shape shape = Reviewed(original);
            if (shape == null || code == null) return null;
            if (!TypedLocals(original, code)) return null;
            List<int> found = new List<int>();
            string kinds = "";
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4) continue;
                bool node = object.Equals(code[i].operand, 32768), segment = object.Equals(code[i].operand, 36864);
                if (!node && !segment) continue;
                char kind = '\0';
                foreach (char rule in "BPGSbND")
                {
                    if (shape.Rules.IndexOf(rule) < 0 || ((rule == 'S' || rule == 'D') != segment)) continue;
                    bool match;
                    if (rule == 'N' || rule == 'D') match = ArrayWalk(original, code, i, segment);
                    else match = Guard(original, code, i, rule == 'P' || rule == 'G' || rule == 'S' ? OpCodes.Blt : OpCodes.Ble,
                        segment, rule == 'b', rule == 'G' ? "m_nextGridNode" : rule == 'S' ? "m_nextGridSegment" : "m_nextBuildingNode");
                    if (!match) continue;
                    if (kind != '\0') return null;
                    kind = rule;
                }
                if (kind != '\0') { kinds += kind; found.Add(i); }
            }
            return kinds == shape.Rules ? found.ToArray() : null;
        }
        private static bool Guard(MethodBase original, List<CodeInstruction> code, int bound,
            OpCode predicate, bool segment, bool cached, string next)
        {
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, predicate)) return false;
            int condition = LabelIndex(code, code[branch].operand);
            if (condition <= branch) return false;
            condition = Skip(code, condition);
            if (condition < 0) return false;
            int cursor = Local(code[condition], false);
            int back = Next(code, condition);
            if (cursor < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return false;
            int start = LabelIndex(code, code[back].operand);
            if (start < 0 || start >= bound) return false;
            start = Skip(code, start);
            if (start < 0 || start >= bound) return false;
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            if (load < start || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            int counter = Local(code[store], true);
            if (counter < 0 || counter == cursor || Local(code[load], false) != counter) return false;
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (counter >= locals.Count || cursor >= locals.Count || VariableType(code[store], counter, locals) != typeof(int)
                || VariableType(code[condition], cursor, locals) != typeof(ushort)) return false;

            // Initial zero counter and jump to exactly the same cursor test.
            int initialJump = Previous(code, start), initialStore = Previous(code, initialJump);
            int zero = Previous(code, initialStore);
            if (zero < 0 || !Branch(code[initialJump].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[initialJump].operand)) != condition
                || Local(code[initialStore], true) != counter || code[zero].opcode != OpCodes.Ldc_I4_0) return false;
            int root = LastStore(code, cursor, 0, zero);
            if (root < 0 || !CursorRoot(original, code, root, locals)
                || Escapes(code, cursor, root, back) || Escapes(code, counter, initialStore, back)) return false;

            // Require the reviewed invalid-list handler and its exit. Unknown
            // intervening control flow is not silently treated as a guard.
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

            int updateStore = Previous(code, load);
            if (updateStore < start || Local(code[updateStore], true) != cursor) return false;
            for (int i = start; i < updateStore; ++i)
                if (Local(code[i], true) == cursor || Local(code[i], true) == counter) return false;
            return cached ? CachedUpdate(code, updateStore, cursor, start, next, locals)
                : PoolRead(code, Previous(code, updateStore), cursor, start, next, segment, locals);
        }


        private static bool CachedUpdate(List<CodeInstruction> code, int store, int cursor, int start,
            string next, IList<LocalVariableInfo> locals)
        {
            int load = Previous(code, store);
            if (load < start) return false;
            int saved = Local(code[load], false);
            if (saved < 0 || saved == cursor || saved >= locals.Count || locals[saved].LocalType != typeof(ushort)) return false;
            int sourceStore = -1;
            for (int i = load - 1; i >= start; --i) if (Local(code[i], true) == saved) { sourceStore = i; break; }
            int writes = 0;
            for (int i = start; i < store; ++i) if (Local(code[i], true) == saved) ++writes;
            if (writes != 1 || Escapes(code, saved, start, store)) return false;
            return sourceStore >= start && PoolRead(code, Previous(code, sourceStore), cursor, start, next, false, locals);
        }
        private static bool PoolRead(List<CodeInstruction> code, int field, int cursor, int start, string next,
            bool segment, IList<LocalVariableInfo> locals)
        {
            int element = Previous(code, field), index = Previous(code, element), buffer = Previous(code, index);
            int pool = Previous(code, buffer), manager = Previous(code, pool);
            if (manager < start || field < 0) return false;
            int source = Local(code[manager], false);
            Type value = segment ? typeof(NetSegment) : typeof(NetNode);
            Type storage = segment ? typeof(Array16<NetSegment>) : typeof(Array16<NetNode>);
            return source >= 0 && source < locals.Count && locals[source].LocalType == typeof(NetManager)
                && ManagerRoot(code, manager, typeof(NetManager), locals)
                && Field(code[pool], typeof(NetManager), segment ? "m_segments" : "m_nodes")
                && Field(code[buffer], storage, "m_buffer") && ((FieldInfo)code[buffer].operand).FieldType == value.MakeArrayType()
                && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema
                && object.Equals(code[element].operand, value) && Field(code[field], value, next);
        }
        private static bool ArrayWalk(MethodBase original, List<CodeInstruction> code, int bound, bool segment)
        {
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Blt)) return false;
            int start = Skip(code, LabelIndex(code, code[branch].operand));
            if (start < 0 || start >= bound) return false;
            int compare = Previous(code, bound), store = Previous(code, compare), add = Previous(code, store);
            int one = Previous(code, add), load = Previous(code, one);
            if (load < start || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            int cursor = Local(code[compare], false);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (cursor < 0 || cursor >= locals.Count || locals[cursor].LocalType != typeof(int)
                || Local(code[store], true) != cursor || Local(code[load], false) != cursor) return false;
            int enter = Previous(code, start), initialStore = Previous(code, enter), initial = Previous(code, initialStore);
            if (initial < 0 || !Branch(code[enter].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[enter].operand)) != compare
                || Local(code[initialStore], true) != cursor
                || code[initial].opcode != (original.Name == "ClearDecorations" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0)) return false;
            if (Escapes(code, cursor, initialStore, branch)) return false;
            for (int i = start; i < load; ++i) if (Local(code[i], true) == cursor) return false;
            int field = start;
            for (int i = 0; i < 5; ++i) field = Next(code, field);
            if (field < 0 || !PoolRead(code, field, cursor, start, "m_flags", segment, locals)) return false;
            int test = Next(code, field);
            if (test < 0) return false;
            if (original.Name == "SavePaths")
            {
                if (code[test].opcode != OpCodes.Ldc_I4_3) return false;
                test = Next(code, test);
                if (test < 0 || code[test].opcode != OpCodes.And) return false;
                test = Next(code, test);
                if (test < 0 || code[test].opcode != OpCodes.Ldc_I4_1) return false;
                test = Next(code, test);
                if (test < 0 || (code[test].opcode != OpCodes.Bne_Un && code[test].opcode != OpCodes.Bne_Un_S)) return false;
            }
            else if (!Branch(code[test].opcode, OpCodes.Brfalse)) return false;
            return Skip(code, LabelIndex(code, code[test].operand)) == load;
        }
        private static bool TypedLocals(MethodBase original, List<CodeInstruction> code)
        {
            MethodBody body = original.GetMethodBody();
            if (body == null) return false;
            IList<LocalVariableInfo> locals = body.LocalVariables;
            foreach (CodeInstruction c in code)
            {
                int index = Local(c, false);
                if (index < 0) index = Local(c, true);
                if (index < 0) index = Address(c);
                string operationName = c.opcode.Name ?? "";
                bool operation = operationName.StartsWith("ldloc") || operationName.StartsWith("stloc");
                if (!operation) continue;
                if (index < 0 || index >= locals.Count) return false;
                LocalBuilder builder = c.operand as LocalBuilder;
                LocalVariableInfo variable = c.operand as LocalVariableInfo;
                if (builder != null && builder.LocalType != locals[index].LocalType) return false;
                if (variable != null && variable.LocalType != locals[index].LocalType) return false;
            }
            return true;
        }
        private static bool Escapes(List<CodeInstruction> code, int local, int start, int end)
        {
            for (int i = Math.Max(0, start); i <= end && i < code.Count; ++i)
                if (Address(code[i]) == local) return true;
            return false;
        }
        private static int LastStore(List<CodeInstruction> code, int local, int start, int end)
        {
            for (int i = end - 1; i >= start; --i) if (Local(code[i], true) == local) return i;
            return -1;
        }
        private static int Argument(CodeInstruction code)
        {
            if (code.opcode == OpCodes.Ldarg_0) return 0;
            if (code.opcode == OpCodes.Ldarg_1) return 1;
            if (code.opcode == OpCodes.Ldarg_2) return 2;
            if (code.opcode == OpCodes.Ldarg_3) return 3;
            if (code.opcode != OpCodes.Ldarg && code.opcode != OpCodes.Ldarg_S) return -1;
            ParameterInfo p = code.operand as ParameterInfo;
            if (p != null) return p.Position + 1;
            if (code.operand is int) return (int)code.operand;
            if (code.operand is byte) return (byte)code.operand;
            if (code.operand is short) return (short)code.operand;
            return -1;
        }
        private static bool ManagerRoot(List<CodeInstruction> code, int load, Type manager, IList<LocalVariableInfo> locals)
        {
            if (load < 0) return false;
            int local = Local(code[load], false);
            if (local < 0 || local >= locals.Count || locals[local].LocalType != manager) return false;
            int store = LastStore(code, local, 0, load);
            int call = Previous(code, store);
            if (call < 0 || Escapes(code, local, store, load) || code[call].opcode != OpCodes.Call) return false;
            MethodInfo getter = code[call].operand as MethodInfo;
            return getter != null && getter.IsStatic && getter.Name == "get_instance" && getter.ReturnType == manager
                && getter.DeclaringType != null && getter.DeclaringType.IsGenericType
                && getter.DeclaringType.GetGenericTypeDefinition().FullName == "ColossalFramework.Singleton`1"
                && getter.DeclaringType.GetGenericArguments()[0] == manager;
        }
        private static bool GridRoot(List<CodeInstruction> code, int store, Type owner, string field,
            IList<LocalVariableInfo> locals)
        {
            int element = Previous(code, store), add = Previous(code, element), x = Previous(code, add);
            int mul = Previous(code, x), size = Previous(code, mul), z = Previous(code, size);
            int grid = Previous(code, z), manager = Previous(code, grid);
            if (manager < 0 || code[element].opcode != OpCodes.Ldelem_U2 || code[add].opcode != OpCodes.Add
                || code[mul].opcode != OpCodes.Mul || code[size].opcode != OpCodes.Ldc_I4
                || !object.Equals(code[size].operand, 270) || !Field(code[grid], owner, field)) return false;
            int xLocal = Local(code[x], false), zLocal = Local(code[z], false);
            if (xLocal < 0 || zLocal < 0 || xLocal >= locals.Count || zLocal >= locals.Count
                || locals[xLocal].LocalType != typeof(int) || locals[zLocal].LocalType != typeof(int)) return false;
            return owner == typeof(BuildingManager) ? Argument(code[manager]) == 0
                : ManagerRoot(code, manager, typeof(NetManager), locals);
        }
        private static bool CursorRoot(MethodBase original, List<CodeInstruction> code, int store, IList<LocalVariableInfo> locals)
        {
            int source = Previous(code, store);
            if (source < 0) return false;
            if (original.DeclaringType == typeof(Building) && original.Name == "FindAccessLane")
                return GridRoot(code, store, typeof(NetManager), "m_segmentGrid", locals);
            if (original.DeclaringType == typeof(Building) && original.Name == "FindParentNode")
                return GridRoot(code, store, typeof(NetManager), "m_nodeGrid", locals);
            if (!Field(code[source], typeof(Building), "m_netNode")) return false;
            int value = Previous(code, source);
            if (value < 0) return false;
            if (original.DeclaringType == typeof(Building) || original.DeclaringType == typeof(BuildingDecoration))
                return Argument(code[value]) == 0;
            if (original.DeclaringType == typeof(BuildingManager) && original.Name == "ReleasePaths")
                return Argument(code[value]) == 2;
            if (code[value].opcode != OpCodes.Ldelema || !object.Equals(code[value].operand, typeof(Building))) return false;
            int index = Previous(code, value), buffer = Previous(code, index), pool = Previous(code, buffer), manager = Previous(code, pool);
            if (original.DeclaringType == typeof(TransportLine))
            {
                // Index is this.m_building rather than a local/argument value.
                if (!Field(code[index], typeof(TransportLine), "m_building")) return false;
                int self = Previous(code, index);
                if (self < 0 || Argument(code[self]) != 0) return false;
                buffer = Previous(code, self); pool = Previous(code, buffer); manager = Previous(code, pool);
            }
            if (manager < 0 || !Field(code[pool], typeof(BuildingManager), "m_buildings")
                || !Field(code[buffer], typeof(Array16<Building>), "m_buffer")) return false;
            if (original.DeclaringType == typeof(BuildingManager))
            {
                if (Argument(code[manager]) != 0) return false;
                if (original.Name == "UpdateFlags") return Argument(code[index]) == 1;
                int building = Local(code[index], false);
                if (building < 0 || building >= locals.Count || locals[building].LocalType != typeof(ushort)) return false;
                int root = LastStore(code, building, 0, index);
                if (root < 0 || Escapes(code, building, root, index)) return false;
                if (original.Name == "FindNodeOwner") return GridRoot(code, root, typeof(BuildingManager), "m_buildingGrid", locals);
                int input = Previous(code, root);
                return original.Name == "IgnoreOverlap" && input >= 0 && Argument(code[input]) == 1;
            }
            if (!ManagerRoot(code, manager, typeof(BuildingManager), locals)) return false;
            if (original.DeclaringType == typeof(TransportLine)) return true;
            if (original.DeclaringType == typeof(BuildingTool)) return Argument(code[index]) == 0;
            if (original.DeclaringType != typeof(DefaultTool)) return false;
            int selected = Local(code[index], false);
            if (selected < 0 || selected >= locals.Count || locals[selected].LocalType != typeof(ushort)) return false;
            int selectedStore = LastStore(code, selected, 0, index), getterIndex = Previous(code, selectedStore);
            if (getterIndex < 0 || Escapes(code, selected, selectedStore, index)) return false;
            MethodInfo getter = code[getterIndex].operand as MethodInfo;
            int address = Previous(code, getterIndex);
            int instance = address < 0 ? -1 : Address(code[address]);
            return getter != null && code[getterIndex].opcode == OpCodes.Call && getter.DeclaringType == typeof(InstanceID)
                && getter.Name == "get_Building" && getter.ReturnType == typeof(ushort)
                && instance >= 0 && instance < locals.Count && locals[instance].LocalType == typeof(InstanceID);
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
            return actual == op || (op == OpCodes.Brfalse && actual == OpCodes.Brfalse_S)
                || (op == OpCodes.Br && actual == OpCodes.Br_S)
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


