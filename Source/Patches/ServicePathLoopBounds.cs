using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Reviewed service/path loops have different exit semantics. Resolve the
    // complete method without changing instructions; no positional fallback.
    internal static class ServicePathLoopBounds
    {
        private sealed class Shape
        {
            internal readonly string Type, Name, Parameters, Result, Kinds;
            internal readonly bool Static;
            internal Shape(string type, string name, string parameters, string result, bool isStatic, string kinds)
            { Type = type; Name = name; Parameters = parameters; Result = result; Static = isStatic; Kinds = kinds; }
        }
        private static readonly Shape[] Shapes = new Shape[] {
            new Shape("CoverageManager", "FindFireCoverage", "UnityEngine.Vector3", "System.Byte", false, "G"),
            new Shape("CoverageManager", "SetMode", "ItemClass+Service,ItemClass+SubService,ItemClass+SubService,ItemClass+Level,System.Single,System.Boolean", "System.Void", false, "C"),
            new Shape("CoverageManager", "UpdateCoverage", "", "System.Void", false, "DNS"),
            new Shape("DisasterHelpers", "DestroyNetSegments", "System.Int32,InstanceManager+Group,UnityEngine.Vector3,System.Single,System.Single,System.Single,System.Single", "System.Void", true, "G"),
            new Shape("LegacyMetroUtils", "PatchSegment", "NetInfo,System.UInt16,NetSegment&", "System.Void", true, "L"),
            new Shape("PathFind", "PathFindImplementation", "System.UInt32,PathUnit&", "System.Void", false, "P"),
            new Shape("PathManager", "FindPathPosition", "UnityEngine.Vector3,ItemClass+Service,ItemClass+Service,ItemClass+Service,ItemClass+SubService,ItemClass+Level,NetInfo+LaneType,VehicleInfo+VehicleType,VehicleInfo+VehicleCategory,VehicleInfo+VehicleType,System.Boolean,System.Boolean,System.Single,System.Boolean,System.Boolean,System.Boolean,PathUnit+Position&,PathUnit+Position&,System.Single&,System.Single&", "System.Boolean", true, "G"),
            new Shape("UnlockManager", "FixMilestones", "", "System.Void", false, "U"),
            new Shape("WaterManager", "UpdateGrid", "System.Single,System.Single,System.Single,System.Single", "System.Void", false, "G"),
            new Shape("ZoneBlock", "CalculateBlock1", "System.UInt16", "System.Void", false, "G")
        };
        private static Shape Reviewed(MethodBase original)
        {
            MethodInfo m = original as MethodInfo;
            if (m == null || m.DeclaringType == null || m.DeclaringType.Assembly != typeof(NetManager).Assembly) return null;
            List<string> args = new List<string>();
            foreach (ParameterInfo p in m.GetParameters()) args.Add(p.ParameterType.FullName);
            foreach (Shape s in Shapes)
                if (m.DeclaringType.FullName == s.Type && m.Name == s.Name && m.IsStatic == s.Static
                    && m.ReturnType.FullName == s.Result && string.Join(",", args.ToArray()) == s.Parameters) return s;
            return null;
        }
        internal static bool Handles(MethodBase original) { return Reviewed(original) != null; }
        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            Shape s = Reviewed(original);
            if (s == null || code == null || !LocalsAgree(original, code)) return null;
            List<int> found = new List<int>(); string kinds = "";
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || (!object.Equals(code[i].operand, 32768) && !object.Equals(code[i].operand, 36864))) continue;
                char kind = '\0';
                foreach (char k in s.Kinds)
                {
                    bool match = k == 'G' || k == 'L' || k == 'P' ? Chain(original, code, i, k) : ArrayLoop(original, code, i, k);
                    if (!match) continue;
                    if (kind != '\0') return null;
                    kind = k;
                }
                if (kind != '\0') { kinds += kind; found.Add(i); }
            }
            return kinds == s.Kinds ? found.ToArray() : null;
        }

        // A matching local index is insufficient when a transpiler supplies a
        // different LocalBuilder type for an existing slot. Check every access,
        // including increment reads, aliases and addresses, before any matching.
        // Explicit, consistently typed unrelated new locals are allowed. Key
        // cursor/counter recognizers still require a reviewed original slot.
        private static bool LocalsAgree(MethodBase original, List<CodeInstruction> code)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            Dictionary<int, Type> added = new Dictionary<int, Type>();
            foreach (CodeInstruction c in code)
            {
                if (!LocalOpcode(c.opcode)) continue;
                int local = Local(c, false);
                if (local < 0) local = Local(c, true);
                if (local < 0) local = Address(c);
                if (local < 0) return false;
                LocalBuilder builder = c.operand as LocalBuilder;
                LocalVariableInfo variable = c.operand as LocalVariableInfo;
                if (local >= locals.Count)
                {
                    Type declared = builder != null ? builder.LocalType : variable != null ? variable.LocalType : null;
                    int declaredIndex = builder != null ? builder.LocalIndex : variable != null ? variable.LocalIndex : -1;
                    Type previous;
                    if (declared == null || declaredIndex != local || added.TryGetValue(local, out previous) && previous != declared) return false;
                    added[local] = declared;
                    continue;
                }
                if (builder != null && (builder.LocalIndex != local || builder.LocalType != locals[local].LocalType)) return false;
                if (variable != null && (variable.LocalIndex != local || variable.LocalType != locals[local].LocalType)) return false;
            }
            return true;
        }
        private static bool LocalOpcode(OpCode op)
        {
            return op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Stloc || op == OpCodes.Stloc_S
                || op == OpCodes.Ldloca || op == OpCodes.Ldloca_S
                || op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3
                || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;
        }

        private static bool Chain(MethodBase original, List<CodeInstruction> code, int bound, char kind)
        {
            int expected = kind == 'G' && original.DeclaringType != typeof(WaterManager) ? 36864 : 32768;
            if (!object.Equals(code[bound].operand, expected)) return false;
            int branch = Next(code, bound);
            OpCode predicate = kind == 'P' ? OpCodes.Bne_Un : kind == 'L' ? OpCodes.Ble : OpCodes.Blt;
            if (branch < 0 || !Branch(code[branch].opcode, predicate)) return false;
            int condition = Skip(code, LabelIndex(code, code[branch].operand));
            if (condition <= branch) return false;
            int cursor = Local(code[condition], false), back = Next(code, condition);
            if (cursor < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return false;
            int start = Skip(code, LabelIndex(code, code[back].operand));
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            if (start < 0 || load < start || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            int counter = Local(code[store], true);
            if (counter < 0 || counter == cursor || Local(code[load], false) != counter
                || !TypeIs(original, code[store], counter, typeof(int)) || !TypeIs(original, code[condition], cursor, typeof(ushort))) return false;
            for (int i = start; i <= back; ++i)
                if (Address(code[i]) == cursor || Address(code[i]) == counter) return false;
            int exit = Next(code, back);
            if (kind == 'P')
            {
                int jump = Next(code, branch);
                if (jump < 0 || !Branch(code[jump].opcode, OpCodes.Br) || Next(code, jump) != condition
                    || Skip(code, LabelIndex(code, code[jump].operand)) != exit) return false;
            }
            else if (!Handler(code, branch, condition, exit)) return false;

            int updateStore = Previous(code, load), saved = -1;
            if (updateStore < start || Local(code[updateStore], true) != cursor) return false;
            string next = kind == 'G' ? "m_nextGridSegment" : "m_nextLaneNode";
            if (kind == 'G' && original.DeclaringType.FullName == "DisasterHelpers" || kind == 'L')
            {
                int use = Previous(code, updateStore); saved = Local(code[use], false);
                if (saved < 0 || saved == cursor || saved == counter || !TypeIs(original, code[use], saved, typeof(ushort))) return false;
                int writes = 0;
                for (int i = start; i < updateStore; ++i)
                {
                    if (Address(code[i]) == saved) return false;
                    if (Local(code[i], true) != saved) continue;
                    ++writes;
                    if (!Link(original, code, Previous(code, i), cursor, start, next, kind == 'G', kind == 'P'))
                    {
                        if (kind != 'G' || writes != 2 || !SecondaryLink(original, code, i, cursor, saved, start)) return false;
                    }
                    else if (writes != 1) return false;
                }
                if (writes != (kind == 'G' ? 2 : 1)) return false;
            }
            else if (!Link(original, code, Previous(code, updateStore), cursor, start, next, kind == 'G', kind == 'P')) return false;
            for (int i = start; i < updateStore; ++i)
                if (Local(code[i], true) == cursor || Local(code[i], true) == counter) return false;

            int entry = Previous(code, start);
            if (entry < 0 || !Branch(code[entry].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[entry].operand)) != condition) return false;
            if (kind == 'P') return PathSeed(original, code, start, cursor, counter, exit);
            int counterStore = Previous(code, entry), zero = Previous(code, counterStore), seed = Previous(code, zero);
            if (seed < 0 || code[zero].opcode != OpCodes.Ldc_I4_0 || Local(code[counterStore], true) != counter
                || Local(code[seed], true) != cursor) return false;
            return kind == 'G' ? GridSeed(original, code, seed, cursor, back + 1) : LaneSeed(original, code, seed, start);
        }
        private static bool Handler(List<CodeInstruction> code, int branch, int condition, int exit)
        {
            int i = Next(code, branch);
            if (i < 0 || code[i].opcode != OpCodes.Ldc_I4_1) return false;
            i = Next(code, i);
            if (i < 0 || code[i].opcode != OpCodes.Ldstr || !object.Equals(code[i].operand, "Invalid list detected!\n")) return false;
            i = Next(code, i); if (!Call(code, i, typeof(Environment).FullName, "get_StackTrace")) return false;
            i = Next(code, i);
            MethodInfo concat = i < 0 ? null : code[i].operand as MethodInfo;
            if (!Call(code, i, typeof(string).FullName, "Concat") || concat.ReturnType != typeof(string)
                || concat.GetParameters().Length != 2) return false;
            i = Next(code, i); MethodInfo error = i < 0 ? null : code[i].operand as MethodInfo;
            if (i < 0 || code[i].opcode != OpCodes.Call || error == null || error.Name != "Error"
                || error.DeclaringType != typeof(CODebugBase<LogChannel>) || error.ReturnType != typeof(void)) return false;
            i = Next(code, i);
            return i >= 0 && Branch(code[i].opcode, OpCodes.Br) && Next(code, i) == condition
                && Skip(code, LabelIndex(code, code[i].operand)) == exit;
        }
        private static bool GridSeed(MethodBase original, List<CodeInstruction> code, int store, int cursor, int end)
        {
            int read = Previous(code, store), add = Previous(code, read), x = Previous(code, add);
            int mul = Previous(code, x), width = Previous(code, mul), z = Previous(code, width), grid = Previous(code, z);
            if (grid < 0 || code[read].opcode != OpCodes.Ldelem_U2 || code[add].opcode != OpCodes.Add
                || code[mul].opcode != OpCodes.Mul || code[width].opcode != OpCodes.Ldc_I4
                || !object.Equals(code[width].operand, 270)) return false;
            int a = Local(code[x], false), b = Local(code[z], false);
            if (a < 0 || b < 0 || a == b || !TypeIs(original, code[x], a, typeof(int)) || !TypeIs(original, code[z], b, typeof(int))) return false;
            if (Field(code[grid], typeof(NetManager), "m_segmentGrid"))
                return Manager(original, code, Previous(code, grid), store);
            int local = Local(code[grid], false);
            if (local < 0 || !TypeIs(original, code[grid], local, typeof(ushort[]))) return false;
            int init = LastStore(code, local, grid), field = Previous(code, init), source = Previous(code, field);
            return source >= 0 && Field(code[field], typeof(NetManager), "m_segmentGrid") && Singleton(code[source])
                && Stable(code, local, init + 1, end);
        }
        private static bool LaneSeed(MethodBase original, List<CodeInstruction> code, int store, int end)
        {
            int field = Previous(code, store), element = Previous(code, field), conversion = Previous(code, element);
            int lane = Previous(code, conversion), buffer = Previous(code, lane), pool = Previous(code, buffer), manager = Previous(code, pool);
            int local = lane < 0 ? -1 : Local(code[lane], false);
            if (manager < 0 || !Field(code[field], typeof(NetLane), "m_nodes") || code[element].opcode != OpCodes.Ldelema
                || !object.Equals(code[element].operand, typeof(NetLane)) || code[conversion].opcode != OpCodes.Conv_U
                || local < 0 || !TypeIs(original, code[lane], local, typeof(uint))
                || !Field(code[buffer], typeof(Array32<NetLane>), "m_buffer") || !Field(code[pool], typeof(NetManager), "m_lanes")
                || !Manager(original, code, manager, end)) return false;
            int init = LastStore(code, local, lane), sourceField = Previous(code, init), arg = Previous(code, sourceField);
            return arg >= 0 && code[arg].opcode == OpCodes.Ldarg_2 && Field(code[sourceField], typeof(NetSegment), "m_lanes")
                && Stable(code, local, init + 1, lane);
        }
        private static bool PathSeed(MethodBase original, List<CodeInstruction> code, int start, int cursor, int counter, int exit)
        {
            int seed = LastStore(code, cursor, start), field = Previous(code, seed), element = Previous(code, field);
            int conversion = Previous(code, element), lane = Previous(code, conversion), address = Previous(code, lane);
            int buffer = Previous(code, address), arg = Previous(code, buffer);
            Type item = typeof(PathFind).GetNestedType("BufferItem", BindingFlags.Public | BindingFlags.NonPublic);
            if (arg < 0 || code[arg].opcode != OpCodes.Ldarg_0 || !Field(code[buffer], typeof(PathFind), "m_laneBuffer")
                || !Field(code[lane], item, "m_laneID") || Address(code[address]) < 0
                || !TypeIs(original, code[address], Address(code[address]), item) || code[conversion].opcode != OpCodes.Conv_U
                || code[element].opcode != OpCodes.Ldelema || !object.Equals(code[element].operand, typeof(NetLane))
                || !Field(code[field], typeof(NetLane), "m_nodes") || !ThisBuffer(original, code, buffer, start, typeof(NetLane))) return false;
            int counterStore = LastStore(code, counter, arg), zero = Previous(code, counterStore);
            if (zero < 0 || code[zero].opcode != OpCodes.Ldc_I4_0 || Next(code, counterStore) != arg) return false;
            int check = Next(code, seed), jump = Next(code, check);
            if (jump < 0 || Local(code[check], false) != cursor || !Branch(code[jump].opcode, OpCodes.Brfalse)
                || Skip(code, LabelIndex(code, code[jump].operand)) != exit) return false;
            return Stable(code, cursor, seed + 1, start) && Stable(code, counter, counterStore + 1, start);
        }
        private static bool Link(MethodBase original, List<CodeInstruction> code, int field, int cursor, int start,
            string next, bool segment, bool path)
        {
            Type item = segment ? typeof(NetSegment) : typeof(NetNode);
            int element = Previous(code, field), index = Previous(code, element), buffer = Previous(code, index);
            int source = Previous(code, buffer);
            if (source < start || !Field(code[field], item, next) || code[element].opcode != OpCodes.Ldelema
                || !object.Equals(code[element].operand, item) || Local(code[index], false) != cursor) return false;
            if (path) return code[source].opcode == OpCodes.Ldarg_0 && Field(code[buffer], typeof(PathFind), "m_nodeBuffer")
                && ThisBuffer(original, code, buffer, field, typeof(NetNode));
            Type pool = segment ? typeof(Array16<NetSegment>) : typeof(Array16<NetNode>);
            if (!Field(code[buffer], pool, "m_buffer")) return false;
            string name = segment ? "m_segments" : "m_nodes";
            if (Field(code[source], typeof(NetManager), name)) return Manager(original, code, Previous(code, source), field);
            int local = Local(code[source], false);
            if (local < 0 || !TypeIs(original, code[source], local, pool)) return false;
            int init = LastStore(code, local, start), poolField = Previous(code, init), call = Previous(code, poolField);
            return call >= 0 && Field(code[poolField], typeof(NetManager), name) && Singleton(code[call])
                && Stable(code, local, init + 1, field);
        }
        private static bool SecondaryLink(MethodBase original, List<CodeInstruction> code, int store, int cursor, int saved, int start)
        {
            int field = Previous(code, store), element = Previous(code, field), index = Previous(code, element);
            int secondary = index < 0 ? -1 : Local(code[index], false);
            if (secondary < 0 || secondary == saved || secondary == cursor || !TypeIs(original, code[index], secondary, typeof(ushort))
                || !Link(original, code, field, secondary, start, "m_nextGridSegment", true, false)) return false;
            int init = LastStore(code, secondary, store), call = Previous(code, init), input = Previous(code, call);
            if (input < start || Local(code[input], false) != cursor || !Call(code, call, "DefaultTool", "FindSecondarySegment")
                || !Stable(code, secondary, init + 1, store)) return false;
            MethodInfo secondaryMethod = code[call].operand as MethodInfo;
            ParameterInfo[] parameters = secondaryMethod.GetParameters();
            if (!secondaryMethod.IsStatic || secondaryMethod.ReturnType != typeof(ushort) || parameters.Length != 1
                || parameters[0].ParameterType != typeof(ushort)) return false;
            int source = Previous(code, Previous(code, index));
            int compare = Previous(code, source), compareRight = Previous(code, compare), compareLeft = Previous(code, compareRight);
            if (compareLeft < start || Local(code[compareLeft], false) != saved || Local(code[compareRight], false) != secondary
                || !Branch(code[compare].opcode, OpCodes.Bne_Un) || Skip(code, LabelIndex(code, code[compare].operand)) != Next(code, store)) return false;
            int zeroJump = Previous(code, compareLeft), zeroLoad = Previous(code, zeroJump), destroy = Previous(code, zeroLoad);
            if (destroy < init || Local(code[zeroLoad], false) != secondary || !Branch(code[zeroJump].opcode, OpCodes.Brfalse)
                || !Call(code, destroy, "DisasterHelpers", "DestroySegment")) return false;
            int publish = -1;
            for (int i = store + 1; i < code.Count; ++i) if (Local(code[i], true) == cursor) { publish = i; break; }
            int savedLoad = Previous(code, publish);
            return savedLoad >= 0 && Local(code[savedLoad], false) == saved
                && Skip(code, LabelIndex(code, code[zeroJump].operand)) == savedLoad;
        }
        private static bool ThisBuffer(MethodBase original, List<CodeInstruction> code, int use, int end, Type item)
        {
            string fieldName = item == typeof(NetNode) ? "m_nodeBuffer" : "m_laneBuffer";
            FieldInfo target = AccessTools.Field(typeof(PathFind), fieldName);
            if (target == null || target.FieldType != item.MakeArrayType()) return false;
            int init = -1;
            for (int i = 0; i < use; ++i) if (code[i].opcode == OpCodes.Stfld && target.Equals(code[i].operand)) init = i;
            int buffer = Previous(code, init), pool = Previous(code, buffer), manager = Previous(code, pool), self = Previous(code, manager);
            Type poolType = item == typeof(NetNode) ? typeof(Array16<NetNode>) : typeof(Array32<NetLane>);
            if (self < 0 || code[self].opcode != OpCodes.Ldarg_0 || !Field(code[buffer], poolType, "m_buffer")
                || !Field(code[pool], typeof(NetManager), item == typeof(NetNode) ? "m_nodes" : "m_lanes")
                || !Manager(original, code, manager, init)) return false;
            for (int i = init + 1; i < end; ++i)
                if ((code[i].opcode == OpCodes.Stfld || code[i].opcode == OpCodes.Ldflda) && target.Equals(code[i].operand)) return false;
            return true;
        }

        private static bool ArrayLoop(MethodBase original, List<CodeInstruction> code, int bound, char kind)
        {
            if (!object.Equals(code[bound].operand, kind == 'S' || kind == 'U' ? 36864 : 32768)) return false;
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Blt)) return false;
            int start = Skip(code, LabelIndex(code, code[branch].operand));
            int compare = Previous(code, bound), store = Previous(code, compare), add = Previous(code, store);
            int one = Previous(code, add), load = Previous(code, one), cursor = compare < 0 ? -1 : Local(code[compare], false);
            if (start < 0 || load < start || cursor < 0 || !TypeIs(original, code[compare], cursor, typeof(int))
                || !TypeIs(original, code[store], cursor, typeof(int)) || Local(code[store], true) != cursor
                || Local(code[load], false) != cursor || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            int entry = Previous(code, start), init = Previous(code, entry), value = Previous(code, init);
            if (value < 0 || !Branch(code[entry].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[entry].operand)) != compare
                || Local(code[init], true) != cursor || code[value].opcode != (kind == 'U' ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0)
                || !Stable(code, cursor, start, load)) return false;
            if (kind == 'D' || kind == 'N')
            {
                int field = Next(code, start), index = Next(code, field), op = Next(code, index);
                if (code[start].opcode != OpCodes.Ldarg_0 || !Field(code[field], typeof(CoverageManager), "m_distances")
                    || Local(code[index], false) != cursor) return false;
                if (kind == 'D')
                {
                    int write = Next(code, op);
                    return op >= 0 && code[op].opcode == OpCodes.Ldc_R4 && object.Equals(code[op].operand, 1000000f)
                        && write >= 0 && code[write].opcode == OpCodes.Stelem_R4 && Next(code, write) == load;
                }
                return op >= 0 && code[op].opcode == OpCodes.Ldelem_R4 && CoverageWrites(original, code, start, load, cursor, false);
            }
            bool segment = kind == 'S' || kind == 'U';
            int fieldIndex;
            if (!ArrayItem(original, code, start, cursor, segment, load, out fieldIndex)) return false;
            if (kind == 'C')
            {
                int write = Next(code, fieldIndex);
                return code[fieldIndex].opcode == OpCodes.Ldc_I4 && object.Equals(code[fieldIndex].operand, 255)
                    && write >= 0 && StoreField(code[write], typeof(NetNode), "m_coverage") && Next(code, write) == load;
            }
            if (kind == 'S') return Field(code[fieldIndex], typeof(NetSegment), "m_startNode")
                && CoverageWrites(original, code, start, load, cursor, true);
            int five = Next(code, fieldIndex), and = Next(code, five), expected = Next(code, and), skip = Next(code, expected);
            return kind == 'U' && Field(code[fieldIndex], typeof(NetSegment), "m_flags") && five >= 0 && code[five].opcode == OpCodes.Ldc_I4_5
                && and >= 0 && code[and].opcode == OpCodes.And && expected >= 0 && code[expected].opcode == OpCodes.Ldc_I4_1
                && skip >= 0 && Branch(code[skip].opcode, OpCodes.Bne_Un) && Skip(code, LabelIndex(code, code[skip].operand)) == load;
        }
        private static bool ArrayItem(MethodBase original, List<CodeInstruction> code, int start, int cursor, bool segment, int end, out int next)
        {
            int pool = Next(code, start), buffer = Next(code, pool), index = Next(code, buffer), element = Next(code, index);
            next = Next(code, element); Type item = segment ? typeof(NetSegment) : typeof(NetNode);
            Type poolType = segment ? typeof(Array16<NetSegment>) : typeof(Array16<NetNode>);
            return next >= 0 && Manager(original, code, start, end)
                && Field(code[pool], typeof(NetManager), segment ? "m_segments" : "m_nodes") && Field(code[buffer], poolType, "m_buffer")
                && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema && object.Equals(code[element].operand, item);
        }
        private static bool CoverageWrites(MethodBase original, List<CodeInstruction> code, int start, int end, int cursor, bool segment)
        {
            int count = 0; Type item = segment ? typeof(NetSegment) : typeof(NetNode);
            string field = segment ? "m_fireCoverage" : "m_coverage";
            for (int i = start; i < end; ++i)
            {
                if (!StoreField(code[i], item, field)) continue;
                ++count;
                // The destination array address occurs before the value expression.
                bool destination = false;
                for (int j = i - 1; j >= start; --j)
                {
                    int next;
                    if (!ArrayItem(original, code, j, cursor, segment, end, out next) || next >= i) continue;
                    bool interrupted = false;
                    for (int k = next; k < i; ++k) if (code[k].opcode == OpCodes.Ldelema || code[k].opcode == OpCodes.Stfld) interrupted = true;
                    if (!interrupted) { destination = true; break; }
                }
                if (!destination) return false;
            }
            return count == 2;
        }

        private static bool Manager(MethodBase original, List<CodeInstruction> code, int use, int end)
        {
            if (use < 0) return false; int local = Local(code[use], false);
            if (local < 0 || !TypeIs(original, code[use], local, typeof(NetManager))) return false;
            int init = LastStore(code, local, use), call = Previous(code, init);
            return call >= 0 && Singleton(code[call]) && Stable(code, local, init + 1, end);
        }
        private static bool Stable(List<CodeInstruction> code, int local, int begin, int end)
        {
            for (int i = begin; i < end; ++i) if (Local(code[i], true) == local || Address(code[i]) == local) return false;
            return true;
        }
        private static bool Singleton(CodeInstruction code)
        {
            MethodInfo m = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && m != null && m.DeclaringType == typeof(Singleton<NetManager>)
                && m.Name == "get_instance" && m.ReturnType == typeof(NetManager) && m.IsStatic && m.GetParameters().Length == 0;
        }
        private static bool TypeIs(MethodBase original, CodeInstruction access, int local, Type type)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (local < 0 || local >= locals.Count) return false;
            LocalBuilder b = access.operand as LocalBuilder; LocalVariableInfo v = access.operand as LocalVariableInfo;
            return (b != null ? b.LocalType : v != null ? v.LocalType : locals[local].LocalType) == type;
        }
        private static bool Field(CodeInstruction code, Type type, string name)
        {
            FieldInfo f = code.operand as FieldInfo;
            return code.opcode == OpCodes.Ldfld && f != null && f.DeclaringType == type && f.Name == name;
        }
        private static bool StoreField(CodeInstruction code, Type type, string name)
        {
            FieldInfo f = code.operand as FieldInfo;
            return code.opcode == OpCodes.Stfld && f != null && f.DeclaringType == type && f.Name == name;
        }
        private static bool Call(List<CodeInstruction> code, int i, string type, string name)
        {
            MethodInfo m = i < 0 ? null : code[i].operand as MethodInfo;
            return m != null && code[i].opcode == OpCodes.Call && m.DeclaringType.FullName == type && m.Name == name;
        }
        private static bool Branch(OpCode actual, OpCode expected)
        {
            return actual == expected || expected == OpCodes.Br && actual == OpCodes.Br_S
                || expected == OpCodes.Brtrue && actual == OpCodes.Brtrue_S || expected == OpCodes.Brfalse && actual == OpCodes.Brfalse_S
                || expected == OpCodes.Blt && actual == OpCodes.Blt_S || expected == OpCodes.Ble && actual == OpCodes.Ble_S
                || expected == OpCodes.Bne_Un && actual == OpCodes.Bne_Un_S;
        }
        private static int LabelIndex(List<CodeInstruction> code, object value)
        {
            if (!(value is Label)) return -1; int result = -1;
            for (int i = 0; i < code.Count; ++i) if (code[i].labels.Contains((Label)value)) { if (result >= 0) return -1; result = i; }
            return result;
        }
        private static int Skip(List<CodeInstruction> code, int index)
        { if (index < 0) return -1; while (index < code.Count && code[index].opcode == OpCodes.Nop) ++index; return index < code.Count ? index : -1; }
        private static int Next(List<CodeInstruction> code, int index) { return index < 0 ? -1 : Skip(code, index + 1); }
        private static int Previous(List<CodeInstruction> code, int index)
        { --index; while (index >= 0 && code[index].opcode == OpCodes.Nop) --index; return index; }
        private static int LastStore(List<CodeInstruction> code, int local, int end)
        { for (int i = end - 1; i >= 0; --i) if (Local(code[i], true) == local) return i; return -1; }
        private static int Address(CodeInstruction code)
        { return code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S ? Operand(code.operand) : -1; }
        private static int Local(CodeInstruction code, bool store)
        {
            if (code.opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (code.opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (code.opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (code.opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            return code.opcode == (store ? OpCodes.Stloc : OpCodes.Ldloc) || code.opcode == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S) ? Operand(code.operand) : -1;
        }
        private static int Operand(object value)
        {
            LocalBuilder b = value as LocalBuilder; if (b != null) return b.LocalIndex;
            LocalVariableInfo v = value as LocalVariableInfo; if (v != null) return v.LocalIndex;
            if (value is int) return (int)value; if (value is byte) return (byte)value; if (value is short) return (short)value;
            return -1;
        }
    }
}
