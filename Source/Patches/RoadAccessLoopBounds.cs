using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Reviewed building road-access and placement traversals, including head guards and cached pools.
    internal static class RoadAccessLoopBounds
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

            new Shape(typeof(AirportAuxBuildingAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "BS"),
            new Shape(typeof(AirportCargoGateAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "BS"),
            new Shape(typeof(AirportGateAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "BS"),
            new Shape(typeof(BuildingTool), "SnapToCanal", "UnityEngine.Vector3,UnityEngine.Vector3&,UnityEngine.Vector3&,System.Boolean&,System.Single,System.Boolean", "System.Boolean", true, "T"),
            new Shape(typeof(BuildingTool), "SnapToPath", "UnityEngine.Vector3,UnityEngine.Vector3&,UnityEngine.Vector3&,System.Single,System.Boolean,System.Boolean,System.Boolean", "System.Boolean", true, "T"),
            new Shape(typeof(CampusBuildingAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "BS"),
            new Shape(typeof(CanalAI), "CheckBuildPosition", "System.Boolean,System.Boolean,System.Boolean,System.Boolean,NetTool+ControlPoint&,NetTool+ControlPoint&,NetTool+ControlPoint&,BuildingInfo&,UnityEngine.Vector3&,UnityEngine.Vector3&,System.Int32&", "ToolBase+ToolErrors", false, "S"),
            new Shape(typeof(CommonBuildingAI), "BuildingCollapsed", "System.UInt16,Building&,Building+Frame&", "System.Void", false, "C"),
            new Shape(typeof(CommonBuildingAI), "FindEventAccess", "UnityEngine.Vector3,System.UInt16&", "System.Boolean", false, "H"),
            new Shape(typeof(CommonBuildingAI), "FindRoadAccess", "UnityEngine.Vector3,System.UInt16&", "System.Boolean", false, "H"),
            new Shape(typeof(MuseumAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "BS"),
            new Shape(typeof(ParkBuildingAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "BS"),
            new Shape(typeof(PlayerBuildingAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "H"),
            new Shape(typeof(RaceBuildingAI), "CheckRoadAccess", "System.UInt16,Building&", "System.Void", false, "B"),
            new Shape(typeof(RaceBuildingAI), "FindRoadAccess", "System.UInt16,Building&,UnityEngine.Vector3,System.UInt16&,System.Boolean,System.Boolean", "System.Boolean", false, "H"),
            new Shape(typeof(RaceEndBuildingAI), "CreateBuilding", "System.UInt16,Building&", "System.Void", false, "A"),
            new Shape(typeof(RaceStandAI), "FindEventRoad", "UnityEngine.Vector3,System.Single,System.UInt16&", "System.Boolean", false, "R"),
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
            if (shape == null || code == null || !TypedLocals(original, code)) return null;
            List<int> found = new List<int>(); string kinds = "";
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4) continue;
                char match = '\0';
                foreach (char rule in "BSHCTAR")
                {
                    if (shape.Rules.IndexOf(rule) < 0) continue;
                    int constant = rule == 'S' || rule == 'H' || rule == 'R' ? 36864 : 32768;
                    if (!object.Equals(code[i].operand, constant)) continue;
                    bool segment = rule == 'S' || rule == 'H' || rule == 'T' || rule == 'R';
                    bool valid = rule == 'H' ? HeadGuard(original, code, i)
                        : Guard(original, code, i, rule == 'B' || rule == 'A' ? OpCodes.Ble : OpCodes.Blt,
                            segment, rule == 'C', segment ? "m_nextGridSegment" : "m_nextBuildingNode", rule);
                    if (!valid) continue;
                    if (match != '\0') return null;
                    match = rule;
                }
                if (match != '\0') { kinds += match; found.Add(i); }
            }
            return kinds == shape.Rules ? found.ToArray() : null;
        }
        private static bool Guard(MethodBase original, List<CodeInstruction> code, int bound,
            OpCode predicate, bool segment, bool cached, string next, char kind)
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
            if (root < 0 || !CursorRoot(original, code, root, segment, locals)
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
                : PoolRead(code, Previous(code, updateStore), cursor, start, next, segment, kind, locals);
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
            return sourceStore >= start && PoolRead(code, Previous(code, sourceStore), cursor, start, next, false, 'B', locals);
        }

        private static bool PoolRead(List<CodeInstruction> code, int field, int cursor, int start, string next,
            bool segment, char kind, IList<LocalVariableInfo> locals)
        {
            int element = Previous(code, field), index = Previous(code, element), source = Previous(code, index);
            if (source < start || !Field(code[field], segment ? typeof(NetSegment) : typeof(NetNode), next)
                || code[element].opcode != OpCodes.Ldelema
                || !object.Equals(code[element].operand, segment ? typeof(NetSegment) : typeof(NetNode))
                || Local(code[index], false) != cursor) return false;
            Type storage = segment ? typeof(Array16<NetSegment>) : typeof(Array16<NetNode>);
            Type array = segment ? typeof(NetSegment[]) : typeof(NetNode[]);
            if (kind == 'A' || kind == 'R')
            {
                int cached = Local(code[source], false);
                if (cached < 0 || cached >= locals.Count || locals[cached].LocalType != array) return false;
                int store = LastStore(code, cached, 0, source), buffer = Previous(code, store);
                if (store < 0 || store >= start || !StableLocal(code, cached, store)
                    || !Field(code[buffer], storage, "m_buffer")) return false;
                int pool = Previous(code, buffer), manager = Previous(code, pool);
                return manager >= 0 && Field(code[pool], typeof(NetManager), segment ? "m_segments" : "m_nodes")
                    && ManagerRoot(code, manager, typeof(NetManager), locals);
            }
            if (!Field(code[source], storage, "m_buffer")) return false;
            int poolSource = Previous(code, source);
            if (poolSource < 0) return false;
            if (kind == 'T')
            {
                int cached = Local(code[poolSource], false);
                if (cached < 0 || cached >= locals.Count || locals[cached].LocalType != storage) return false;
                int store = LastStore(code, cached, 0, poolSource), pool = Previous(code, store), manager = Previous(code, pool);
                return manager >= 0 && store < start && StableLocal(code, cached, store)
                    && Field(code[pool], typeof(NetManager), "m_segments") && SingletonCall(code[manager], typeof(NetManager));
            }
            int load = Previous(code, poolSource);
            return load >= start && Field(code[poolSource], typeof(NetManager), segment ? "m_segments" : "m_nodes")
                && ManagerRoot(code, load, typeof(NetManager), locals);
        }
        private static bool SingletonCall(CodeInstruction code, Type manager)
        {
            MethodInfo call = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && call != null && call.IsStatic && call.Name == "get_instance"
                && call.ReturnType == manager && call.DeclaringType != null && call.DeclaringType.IsGenericType
                && call.DeclaringType.GetGenericTypeDefinition().FullName == "ColossalFramework.Singleton`1"
                && call.DeclaringType.GetGenericArguments()[0] == manager;
        }
        private static bool CursorRoot(MethodBase original, List<CodeInstruction> code, int store, bool segment,
            IList<LocalVariableInfo> locals)
        {
            int field = Previous(code, store);
            if (field < 0) return false;
            if (!segment) return Field(code[field], typeof(Building), "m_netNode")
                && Previous(code, field) >= 0 && Argument(code[Previous(code, field)]) == 2;
            int element = Previous(code, store), add = Previous(code, element), x = Previous(code, add);
            int mul = Previous(code, x), size = Previous(code, mul), z = Previous(code, size), grid = Previous(code, z);
            if (grid < 0 || code[element].opcode != OpCodes.Ldelem_U2 || code[add].opcode != OpCodes.Add
                || code[mul].opcode != OpCodes.Mul || code[size].opcode != OpCodes.Ldc_I4
                || !object.Equals(code[size].operand, 270)) return false;
            int xl = Local(code[x], false), zl = Local(code[z], false);
            if (xl < 0 || zl < 0 || xl >= locals.Count || zl >= locals.Count
                || locals[xl].LocalType != typeof(int) || locals[zl].LocalType != typeof(int)) return false;
            if (original.DeclaringType != typeof(BuildingTool))
            {
                int manager = Previous(code, grid);
                return manager >= 0 && Field(code[grid], typeof(NetManager), "m_segmentGrid")
                    && ManagerRoot(code, manager, typeof(NetManager), locals);
            }
            // Snap helpers cache the grid array separately from the segment pool.
            int cached = Local(code[grid], false);
            if (cached < 0 || cached >= locals.Count || locals[cached].LocalType != typeof(ushort[])) return false;
            int gridStore = LastStore(code, cached, 0, grid), gridField = Previous(code, gridStore), getter = Previous(code, gridField);
            return getter >= 0 && StableLocal(code, cached, gridStore)
                && Field(code[gridField], typeof(NetManager), "m_segmentGrid") && SingletonCall(code[getter], typeof(NetManager));
        }
        private static int HandlerExit(List<CodeInstruction> code, int branch, int body)
        {
            int h = Next(code, branch);
            if (h < 0 || code[h].opcode != OpCodes.Ldc_I4_1) return -1;
            h = Next(code, h);
            if (h < 0 || code[h].opcode != OpCodes.Ldstr || !object.Equals(code[h].operand, "Invalid list detected!\n")) return -1;
            h = Next(code, h);
            if (!Call(code, h, typeof(Environment), "get_StackTrace")) return -1;
            h = Next(code, h);
            MethodInfo concat = h < 0 ? null : code[h].operand as MethodInfo;
            if (!Call(code, h, typeof(string), "Concat") || concat.ReturnType != typeof(string)
                || concat.GetParameters().Length != 2) return -1;
            h = Next(code, h);
            MethodInfo error = h < 0 ? null : code[h].operand as MethodInfo;
            if (h < 0 || code[h].opcode != OpCodes.Call || error == null || error.Name != "Error"
                || error.DeclaringType == null || !error.DeclaringType.IsGenericType
                || error.DeclaringType.GetGenericTypeDefinition().FullName != "ColossalFramework.CODebugBase`1") return -1;
            h = Next(code, h);
            if (h < 0 || !Branch(code[h].opcode, OpCodes.Br) || Next(code, h) != body) return -1;
            return Skip(code, LabelIndex(code, code[h].operand));
        }
        private static bool HeadGuard(MethodBase original, List<CodeInstruction> code, int bound)
        {
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Blt)) return false;
            int body = Skip(code, LabelIndex(code, code[branch].operand));
            if (body <= branch) return false;
            int exit = HandlerExit(code, branch, body);
            if (exit <= body) return false;
            int back = Previous(code, exit), condition = Previous(code, back);
            if (condition < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return false;
            int start = Skip(code, LabelIndex(code, code[back].operand));
            int store = Previous(code, bound), add = Previous(code, store), one = Previous(code, add);
            int dup = Previous(code, one), load = Previous(code, dup);
            if (load < 0 || start != load || code[dup].opcode != OpCodes.Dup || code[one].opcode != OpCodes.Ldc_I4_1
                || code[add].opcode != OpCodes.Add) return false;
            int counter = Local(code[load], false), cursor = Local(code[condition], false);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (counter < 0 || cursor < 0 || counter == cursor || counter >= locals.Count || cursor >= locals.Count
                || locals[counter].LocalType != typeof(int) || locals[cursor].LocalType != typeof(ushort)
                || Local(code[store], true) != counter) return false;
            int initialJump = Previous(code, start);
            if (initialJump < 0 || !Branch(code[initialJump].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[initialJump].operand)) != condition) return false;
            int root = Previous(code, initialJump), initial = LastStore(code, counter, 0, start), zero = Previous(code, initial);
            if (root < 0 || zero < 0 || Local(code[root], true) != cursor || initial >= root
                || code[zero].opcode != OpCodes.Ldc_I4_0 || !CursorRoot(original, code, root, true, locals)
                || Escapes(code, cursor, root, back) || Escapes(code, counter, initial, back)) return false;
            int update = Previous(code, condition);
            if (update < body || Local(code[update], true) != cursor
                || !PoolRead(code, Previous(code, update), cursor, body, "m_nextGridSegment", true, 'H', locals)) return false;
            for (int i = body; i < update; ++i)
                if (Local(code[i], true) == cursor || Local(code[i], true) == counter) return false;
            return true;
        }
        private static bool TypedLocals(MethodBase original, List<CodeInstruction> code)
        {
            MethodBody body = original.GetMethodBody();
            if (body == null) return false;
            IList<LocalVariableInfo> locals = body.LocalVariables;
            Dictionary<int, Type> extra = new Dictionary<int, Type>();
            foreach (CodeInstruction c in code)
            {
                int index = Local(c, false);
                if (index < 0) index = Local(c, true);
                if (index < 0) index = Address(c);
                string operationName = c.opcode.Name ?? "";
                bool operation = operationName.StartsWith("ldloc") || operationName.StartsWith("stloc");
                if (!operation) continue;
                if (index < 0) return false;
                LocalBuilder builder = c.operand as LocalBuilder;
                LocalVariableInfo variable = c.operand as LocalVariableInfo;
                if (index >= locals.Count)
                {
                    Type declared = builder != null ? builder.LocalType : variable != null ? variable.LocalType : null;
                    if (declared == null) return false;
                    Type previous;
                    if (extra.TryGetValue(index, out previous) && previous != declared) return false;
                    extra[index] = declared;
                    continue;
                }
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
        private static bool StableLocal(List<CodeInstruction> code, int local, int initialization)
        {
            if (initialization < 0) return false;
            for (int i = 0; i < code.Count; ++i)
                if (Address(code[i]) == local || (i != initialization && Local(code[i], true) == local)) return false;
            return Local(code[initialization], true) == local;
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
            if (call < 0 || !StableLocal(code, local, store) || code[call].opcode != OpCodes.Call) return false;
            MethodInfo getter = code[call].operand as MethodInfo;
            return getter != null && getter.IsStatic && getter.Name == "get_instance" && getter.ReturnType == manager
                && getter.DeclaringType != null && getter.DeclaringType.IsGenericType
                && getter.DeclaringType.GetGenericTypeDefinition().FullName == "ColossalFramework.Singleton`1"
                && getter.DeclaringType.GetGenericArguments()[0] == manager;
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
