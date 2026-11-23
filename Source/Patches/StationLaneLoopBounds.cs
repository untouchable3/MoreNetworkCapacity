using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Reviewed station, building and lane node walks; no position/count fallback.
    internal static class StationLaneLoopBounds
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


            new Shape(typeof(CableCarAI), "FindNextStop", "System.UInt16", "System.UInt16", false, "O"),
            new Shape(typeof(CableCarPathAI), "UpdateNode", "System.UInt16,NetNode&", "System.Void", false, "P"),
            new Shape(typeof(CableCarStationAI), "CalculateLaneNodePassengers", "System.UInt16,System.Int32&", "System.Void", true, "L"),
            new Shape(typeof(CableCarStationAI), "CountLines", "System.UInt16,Building&", "System.Int32", false, "B"),
            new Shape(typeof(CableCarStationAI), "FindLineNode", "System.UInt16,Building&,UnityEngine.Vector3,UnityEngine.Vector3,System.Boolean,System.Boolean,System.UInt16&,System.UInt16&", "System.Void", false, "C"),
            new Shape(typeof(CableCarStationAI), "FindPathSegment", "System.UInt16,Building&", "System.UInt16", false, "B"),
            new Shape(typeof(CableCarStationAI), "FindTargetSegment", "System.UInt16,System.UInt16,System.Boolean,System.UInt16&,System.Boolean&", "System.Boolean", false, "G"),
            new Shape(typeof(CableCarStationAI), "ProduceGoods", "System.UInt16,Building&,Building+Frame&,System.Int32,System.Int32,Citizen+BehaviourData&,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32", "System.Void", false, "B"),
            new Shape(typeof(ParkGateAI), "UpdatePathCost", "System.UInt16,Building&", "System.Void", false, "B"),
            new Shape(typeof(ShelterAI), "SimulationStep", "System.UInt16,Building&,Building+Frame&", "System.Void", false, "Q"),
            new Shape(typeof(TollBoothAI), "UpdatePathCost", "System.UInt16,Building&", "System.Void", false, "B"),
            new Shape(typeof(TransportStationAI), "CalculateLaneNodePassengers", "System.UInt16,System.Int32&", "System.Void", true, "L"),
            new Shape(typeof(TransportStationAI), "ProduceGoods", "System.UInt16,Building&,Building+Frame&,System.Int32,System.Int32,Citizen+BehaviourData&,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32", "System.Void", false, "B"),
            new Shape(typeof(TransportStationAI), "RemoveConnectionLines", "System.UInt16,Building&", "System.Void", false, "C"),
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
            int found = -1;
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || !object.Equals(code[i].operand, 32768)) continue;
                State state = Header(original, code, i, shape.Rules[0] == 'P');
                if (state == null) continue;
                bool valid = shape.Rules[0] == 'P' || shape.Rules[0] == 'G'
                    ? Graph(original, code, state, shape.Rules[0] == 'P') : Chain(original, code, state, shape.Rules[0]);
                if (!valid) continue;
                if (found >= 0) return null;
                found = i;
            }
            return found < 0 ? null : new int[] { found };
        }
        private sealed class State
        {
            internal int Cursor, Counter, Start, Load, Condition, Back, Zero, Exit;
        }
        private static int Step(List<CodeInstruction> code, int index, int count)
        { for (int i = 0; i < count; ++i) index = Next(code, index); return index; }
        private static State Header(MethodBase original, List<CodeInstruction> code, int bound, bool flagTest)
        {
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Ble)) return null;
            int condition = Skip(code, LabelIndex(code, code[branch].operand));
            if (condition <= branch) return null;
            int cursorLoad = flagTest ? Step(code, condition, 3) : condition;
            int back = flagTest ? Step(code, condition, 8) : Next(code, condition);
            if (cursorLoad < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return null;
            int cursor = Local(code[cursorLoad], false), start = Skip(code, LabelIndex(code, code[back].operand));
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one), counter = store < 0 ? -1 : Local(code[store], true);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (start < 0 || load < start || counter < 0 || cursor < 0 || cursor == counter || counter >= locals.Count || cursor >= locals.Count
                || locals[counter].LocalType != typeof(int) || locals[cursor].LocalType != typeof(ushort)
                || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1
                || Local(code[load], false) != counter) return null;
            int jump = Previous(code, start), initial = Previous(code, jump), zero = Previous(code, initial);
            if (zero < 0 || !Branch(code[jump].opcode, OpCodes.Br) || Local(code[initial], true) != counter
                || code[zero].opcode != OpCodes.Ldc_I4_0 || Skip(code, LabelIndex(code, code[jump].operand)) != condition
                || Escapes(code, counter, initial, back) || Escapes(code, cursor, zero, back)) return null;
            if (flagTest)
            {
                int element = Step(code, condition, 4), flags = Step(code, condition, 5);
                int mask = Step(code, condition, 6), and = Step(code, condition, 7);
                if (and < 0 || !Element(code, element, cursor, typeof(NetNode), condition, locals)
                    || !Field(code[flags], typeof(NetNode), "m_flags") || code[mask].opcode != OpCodes.Ldc_I4
                    || !object.Equals(code[mask].operand, 1048576) || code[and].opcode != OpCodes.And) return null;
            }
            if (!Handler(code, branch, condition, back)) return null;
            for (int i = start; i <= back; ++i) if (i != store && Local(code[i], true) == counter) return null;
            return new State { Cursor = cursor, Counter = counter, Start = start, Load = load,
                Condition = condition, Back = back, Zero = zero, Exit = Next(code, back) };
        }
        private static bool Handler(List<CodeInstruction> code, int branch, int condition, int back)
        {
            int h = Next(code, branch);
            if (h < 0 || code[h].opcode != OpCodes.Ldc_I4_1) return false;
            h = Next(code, h);
            if (h < 0 || code[h].opcode != OpCodes.Ldstr || !object.Equals(code[h].operand, "Invalid list detected!\n")) return false;
            h = Next(code, h); if (!Call(code, h, typeof(Environment), "get_StackTrace")) return false;
            h = Next(code, h); MethodInfo concat = h < 0 ? null : code[h].operand as MethodInfo;
            if (!Call(code, h, typeof(string), "Concat") || concat.ReturnType != typeof(string) || concat.GetParameters().Length != 2) return false;
            h = Next(code, h); MethodInfo error = h < 0 ? null : code[h].operand as MethodInfo;
            if (h < 0 || code[h].opcode != OpCodes.Call || error == null || error.Name != "Error"
                || error.DeclaringType == null || !error.DeclaringType.IsGenericType
                || error.DeclaringType.GetGenericTypeDefinition().FullName != "ColossalFramework.CODebugBase`1") return false;
            h = Next(code, h);
            return h >= 0 && Branch(code[h].opcode, OpCodes.Br) && Next(code, h) == condition
                && Skip(code, LabelIndex(code, code[h].operand)) == Next(code, back);
        }
        private static bool Element(List<CodeInstruction> code, int element, int cursor, Type item, int start,
            IList<LocalVariableInfo> locals)
        {
            int index = Previous(code, element), buffer = Previous(code, index), pool = Previous(code, buffer), manager = Previous(code, pool);
            return manager >= start && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema
                && object.Equals(code[element].operand, item) && Field(code[buffer], typeof(Array16<>).MakeGenericType(item), "m_buffer")
                && Field(code[pool], typeof(NetManager), item == typeof(NetNode) ? "m_nodes" : "m_segments")
                && ManagerRoot(code, manager, typeof(NetManager), locals);
        }
        private static bool Link(List<CodeInstruction> code, int store, int cursor, string next, int start,
            IList<LocalVariableInfo> locals)
        {
            int field = Previous(code, store), element = Previous(code, field);
            return field >= start && Field(code[field], typeof(NetNode), next) && Element(code, element, cursor, typeof(NetNode), start, locals);
        }
        private static bool Chain(MethodBase original, List<CodeInstruction> code, State state, char kind)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            int root = LastStore(code, state.Cursor, 0, state.Zero);
            if (root < 0 || !Root(original, code, root, kind, locals) || Escapes(code, state.Cursor, root, state.Back)) return false;
            int update = Previous(code, state.Load);
            if (update < state.Start || Local(code[update], true) != state.Cursor) return false;
            if (kind == 'C') return Removal(code, state, update, locals);
            for (int i = state.Start; i < update; ++i) if (Local(code[i], true) == state.Cursor) return false;
            return Link(code, update, state.Cursor, kind == 'L' ? "m_nextLaneNode" : "m_nextBuildingNode", state.Start, locals);
        }
        private static bool Root(MethodBase original, List<CodeInstruction> code, int store, char kind,
            IList<LocalVariableInfo> locals)
        {
            int field = Previous(code, store), source = Previous(code, field);
            if (field < 0) return false;
            if (kind == 'L') return Argument(code[field]) == 0;
            if (!Field(code[field], typeof(Building), "m_netNode") || source < 0) return false;
            if (kind == 'B' || kind == 'C') return Argument(code[source]) == 2;
            if (code[source].opcode != OpCodes.Ldelema || !object.Equals(code[source].operand, typeof(Building))) return false;
            int index = Previous(code, source), buffer = Previous(code, index), pool = Previous(code, buffer), manager = Previous(code, pool);
            if (manager < 0 || !Field(code[buffer], typeof(Array16<Building>), "m_buffer")
                || !Field(code[pool], typeof(BuildingManager), "m_buildings") || !SingletonCall(code[manager], typeof(BuildingManager))) return false;
            if (kind == 'Q') return Argument(code[index]) == 1;
            int owner = Local(code[index], false);
            if (kind != 'O' || owner < 0 || owner >= locals.Count || locals[owner].LocalType != typeof(ushort)) return false;
            int capture = LastStore(code, owner, 0, index), call = Previous(code, capture), distance = Previous(code, call), argument = Previous(code, distance);
            MethodInfo finder = call < 0 ? null : code[call].operand as MethodInfo;
            return argument >= 0 && StableLocal(code, owner, capture) && finder != null && code[call].opcode == OpCodes.Call
                && finder.DeclaringType == typeof(NetNode) && finder.Name == "FindOwnerBuilding" && finder.IsStatic
                && finder.ReturnType == typeof(ushort) && finder.GetParameters().Length == 2
                && finder.GetParameters()[0].ParameterType == typeof(ushort) && finder.GetParameters()[1].ParameterType == typeof(float)
                && Argument(code[argument]) == 1 && code[distance].opcode == OpCodes.Ldc_R4 && object.Equals(code[distance].operand, 64f);
        }
        private static bool SingletonCall(CodeInstruction code, Type manager)
        {
            MethodInfo call = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && call != null && call.IsStatic && call.Name == "get_instance"
                && call.ReturnType == manager && call.DeclaringType != null && call.DeclaringType.IsGenericType
                && call.DeclaringType.GetGenericTypeDefinition().FullName == "ColossalFramework.Singleton`1"
                && call.DeclaringType.GetGenericArguments()[0] == manager;
        }
        private static bool Removal(List<CodeInstruction> code, State state, int update, IList<LocalVariableInfo> locals)
        {
            int savedLoad = Previous(code, update), previousStore = Previous(code, savedLoad), current = Previous(code, previousStore);
            int rollbackStore = Previous(code, current), rollbackLoad = Previous(code, rollbackStore), release = Previous(code, rollbackLoad);
            if (release < state.Start || Local(code[current], false) != state.Cursor || Local(code[rollbackStore], true) != state.Cursor) return false;
            int previous = Local(code[previousStore], true), saved = Local(code[savedLoad], false);
            if (previous < 0 || saved < 0 || previous == saved || previous == state.Cursor || saved == state.Cursor
                || previous >= locals.Count || saved >= locals.Count || locals[previous].LocalType != typeof(ushort)
                || locals[saved].LocalType != typeof(ushort) || Local(code[rollbackLoad], false) != previous) return false;
            MethodInfo releaseMethod = code[release].operand as MethodInfo;
            int releaseArgument = Previous(code, release), manager = Previous(code, releaseArgument);
            if (manager < state.Start || releaseMethod == null || code[release].opcode != OpCodes.Callvirt
                || releaseMethod.DeclaringType != typeof(NetManager) || releaseMethod.Name != "ReleaseNode"
                || releaseMethod.ReturnType != typeof(void) || releaseMethod.GetParameters().Length != 1
                || releaseMethod.GetParameters()[0].ParameterType != typeof(ushort) || Local(code[releaseArgument], false) != state.Cursor
                || !ManagerRoot(code, manager, typeof(NetManager), locals)) return false;
            int capture = LastStore(code, saved, state.Start, savedLoad), previousInit = LastStore(code, previous, 0, state.Start), zero = Previous(code, previousInit);
            if (zero < 0 || code[zero].opcode != OpCodes.Ldc_I4_0 || capture < state.Start
                || !Link(code, capture, state.Cursor, "m_nextBuildingNode", state.Start, locals)
                || Escapes(code, saved, state.Start, update) || Escapes(code, previous, previousInit, update)) return false;
            for (int i = state.Start; i < update; ++i)
                if (i != rollbackStore && Local(code[i], true) == state.Cursor
                    || i != previousStore && Local(code[i], true) == previous || i != capture && Local(code[i], true) == saved) return false;
            return true;
        }
        private sealed class Slots { internal int Init, Check, Increment, First; }
        private static Slots EightSlots(List<CodeInstruction> code, int slot, int first, IList<LocalVariableInfo> locals)
        {
            if (slot < 0 || slot >= locals.Count || locals[slot].LocalType != typeof(int)) return null;
            int init = LastStore(code, slot, 0, first), zero = Previous(code, init), jump = Next(code, init);
            if (zero < 0 || code[zero].opcode != OpCodes.Ldc_I4_0 || jump < 0 || !Branch(code[jump].opcode, OpCodes.Br)) return null;
            int check = Skip(code, LabelIndex(code, code[jump].operand)), eight = Next(code, check), branch = Next(code, eight);
            if (check <= first || eight < 0 || branch < 0 || Local(code[check], false) != slot
                || code[eight].opcode != OpCodes.Ldc_I4_8 || !Branch(code[branch].opcode, OpCodes.Blt)
                || Skip(code, LabelIndex(code, code[branch].operand)) != first) return null;
            int store = Previous(code, check), add = Previous(code, store), one = Previous(code, add), load = Previous(code, one);
            if (load < first || Local(code[store], true) != slot || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1 || Local(code[load], false) != slot) return null;
            for (int i = 0; i < code.Count; ++i)
                if (Address(code[i]) == slot || i != init && i != store && Local(code[i], true) == slot) return null;
            return new Slots { Init = init, Check = check, Increment = load, First = first };
        }
        private static bool InstanceCall(List<CodeInstruction> code, int at, Type owner, string name, Type parameter)
        {
            MethodInfo call = at < 0 ? null : code[at].operand as MethodInfo;
            return call != null && code[at].opcode == OpCodes.Call && call.DeclaringType == owner && call.Name == name
                && !call.IsStatic && call.ReturnType == typeof(ushort) && call.GetParameters().Length == 1
                && call.GetParameters()[0].ParameterType == parameter;
        }
        private static bool Graph(MethodBase original, List<CodeInstruction> code, State state, bool path)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            int call = -1;
            for (int i = state.Start; i < state.Load; ++i)
                if (InstanceCall(code, i, typeof(NetSegment), "GetOtherNode", typeof(ushort)))
                { if (call >= 0) return false; call = i; }
            int cursorLoad = Previous(code, call), element = Previous(code, cursorLoad), candidateLoad = Previous(code, element);
            int cursorStore = Next(code, call);
            if (candidateLoad < state.Start || Local(code[cursorLoad], false) != state.Cursor || cursorStore < 0
                || Local(code[cursorStore], true) != state.Cursor) return false;
            int candidate = Local(code[candidateLoad], false);
            if (candidate < 0 || candidate >= locals.Count || locals[candidate].LocalType != typeof(ushort)
                || !Element(code, element, candidate, typeof(NetSegment), state.Start, locals)) return false;
            int capture = LastStore(code, candidate, state.Start, candidateLoad), get = Previous(code, capture);
            int slotLoad = Previous(code, get), node = Previous(code, slotLoad);
            if (!InstanceCall(code, get, typeof(NetNode), "GetSegment", typeof(int))
                || !Element(code, node, state.Cursor, typeof(NetNode), state.Start, locals)) return false;
            int first = Previous(code, Previous(code, Previous(code, Previous(code, node))));
            int slot = Local(code[slotLoad], false);
            Slots slots = EightSlots(code, slot, first, locals);
            if (slots == null) return false;
            int selectedLoad = Next(code, cursorStore), previousStore = Next(code, selectedLoad), next = Next(code, previousStore);
            if (next < 0 || Local(code[selectedLoad], false) != candidate) return false;
            int previous = Local(code[previousStore], true);
            if (previous < 0 || previous == state.Cursor || previous == candidate || previous >= locals.Count
                || locals[previous].LocalType != typeof(ushort)) return false;
            int root = LastStore(code, state.Cursor, 0, state.Zero), previousInit = LastStore(code, previous, 0, state.Start);
            if (root < 0 || previousInit < 0 || Escapes(code, state.Cursor, root, state.Back)
                || Escapes(code, previous, previousInit, state.Back)) return false;
            int test = Next(code, capture), zeroBranch = Next(code, test);
            if (test < 0 || zeroBranch < 0 || Local(code[test], false) != candidate || !Branch(code[zeroBranch].opcode, OpCodes.Brfalse)
                || Skip(code, LabelIndex(code, code[zeroBranch].operand)) != slots.Increment) return false;
            int compare = Next(code, zeroBranch), prior = Next(code, compare), equality = Next(code, prior);
            if (!path)
            {
                if (equality < 0 || Local(code[compare], false) != candidate || Argument(code[prior]) != 2
                    || !Branch(code[equality].opcode, OpCodes.Beq) || Skip(code, LabelIndex(code, code[equality].operand)) != slots.Increment) return false;
                compare = Next(code, equality); prior = Next(code, compare); equality = Next(code, prior);
            }
            if (equality < 0 || Local(code[compare], false) != candidate || Local(code[prior], false) != previous
                || !Branch(code[equality].opcode, OpCodes.Beq) || Skip(code, LabelIndex(code, code[equality].operand)) != slots.Increment) return false;
            int afterSlots = Step(code, slots.Check, 3), success;
            if (path)
            {
                if (afterSlots != state.Load || !Branch(code[next].opcode, OpCodes.Br)
                    || Skip(code, LabelIndex(code, code[next].operand)) != state.Load) return false;
                success = next;
                // Each visited path node is marked before the8-slot search;
                // lack of an outgoing edge makes the flag-based continuation stop.
                int markElement = Step(code, state.Start, 4), dup = Next(code, markElement), flags = Next(code, dup);
                int mask = Next(code, flags), and = Next(code, mask), write = Next(code, and);
                if (write < 0 || !Element(code, markElement, state.Cursor, typeof(NetNode), state.Start, locals)
                    || code[dup].opcode != OpCodes.Dup || !Field(code[flags], typeof(NetNode), "m_flags")
                    || code[mask].opcode != OpCodes.Ldc_I4 || !object.Equals(code[mask].operand, -1048577)
                    || code[and].opcode != OpCodes.And || code[write].opcode != OpCodes.Stfld
                    || !object.Equals(code[write].operand, code[flags].operand)) return false;
                int rootCall = Previous(code, root), argument = Previous(code, rootCall), rootElement = Previous(code, argument);
                if (argument < 0 || Argument(code[argument]) != 1 || !InstanceCall(code, rootCall, typeof(NetSegment), "GetOtherNode", typeof(ushort))
                    || !Element(code, rootElement, previous, typeof(NetSegment), 0, locals)) return false;
                int getRoot = Previous(code, previousInit), outerSlotLoad = Previous(code, getRoot), receiver = Previous(code, outerSlotLoad);
                if (receiver < 0 || Argument(code[receiver]) != 2 || !InstanceCall(code, getRoot, typeof(NetNode), "GetSegment", typeof(int))
                    || EightSlots(code, Local(code[outerSlotLoad], false), receiver, locals) == null) return false;
            }
            else
            {
                int rootLoad = Previous(code, root), previousLoad = Previous(code, previousInit);
                if (rootLoad < 0 || previousLoad < 0 || Argument(code[rootLoad]) != 1 || Argument(code[previousLoad]) != 2) return false;
                int movedStore = Next(code, next), jump = Next(code, movedStore);
                int movedInit = Previous(code, Previous(code, slots.Init)), movedZero = Previous(code, movedInit);
                int moved = movedStore < 0 ? -1 : Local(code[movedStore], true);
                int foundBranch = Next(code, afterSlots), notFound = Next(code, foundBranch);
                if (notFound < 0 || moved < 0 || moved >= locals.Count || locals[moved].LocalType != typeof(bool)
                    || code[next].opcode != OpCodes.Ldc_I4_1 || Local(code[movedInit], true) != moved || code[movedZero].opcode != OpCodes.Ldc_I4_0
                    || !Branch(code[jump].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[jump].operand)) != afterSlots
                    || Local(code[afterSlots], false) != moved || !Branch(code[foundBranch].opcode, OpCodes.Brtrue)
                    || Skip(code, LabelIndex(code, code[foundBranch].operand)) != state.Load
                    || !Branch(code[notFound].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[notFound].operand)) != state.Exit) return false;
                for (int i = 0; i < code.Count; ++i)
                    if (Address(code[i]) == moved || i != movedInit && i != movedStore && Local(code[i], true) == moved) return false;
                success = jump;
            }
            for (int i = state.Start; i < state.Load; ++i)
                if (Address(code[i]) == candidate || Address(code[i]) == previous
                    || i != capture && Local(code[i], true) == candidate
                    || i != cursorStore && Local(code[i], true) == state.Cursor
                    || i != previousStore && Local(code[i], true) == previous) return false;
            return success >= state.Start;
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
            if (p != null)
            {
                MethodBase method = p.Member as MethodBase;
                return p.Position + (method != null && method.IsStatic ? 0 : 1);
            }
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
            return actual == op || (op == OpCodes.Beq && actual == OpCodes.Beq_S)
                || (op == OpCodes.Brfalse && actual == OpCodes.Brfalse_S)
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
