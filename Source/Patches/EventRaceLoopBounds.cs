using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Event chains, generated road walks, building-node chains and spatial
    // grids share counters but not their provenance or completion semantics.
    internal static class EventRaceLoopBounds
    {
        private sealed class Shape
        {
            internal readonly string Type, Name, Parameters, Result, Kinds;
            internal readonly bool Static;
            internal Shape(string type, string name, string parameters, string result, bool isStatic, string kinds)
            { Type = type; Name = name; Parameters = parameters; Result = result; Static = isStatic; Kinds = kinds; }
        }
        private static readonly Shape[] Shapes = new Shape[] {
            new Shape("EventManager", "BreakEventRoute", "System.UInt16", "System.Void", false, "UR"),
            new Shape("EventManager", "BuildEventRoute", "System.UInt16", "System.Void", false, "Q"),
            new Shape("EventManager", "CalculateEventRoute", "System.UInt16", "System.Void", false, "R"),
            new Shape("EventManager", "UpdateEventRoute", "System.UInt16,EventRouteData+Flags,System.Boolean", "System.Void", false, "R"),
            new Shape("EventManager", "UpdateEventRouteRoadWarnings", "System.UInt16", "System.Void", false, "R"),
            new Shape("EventManager", "UpdateProp", "System.UInt16,PropInstance&", "System.Void", false, "G"),
            new Shape("RaceEventAI", "GenerateTrackPath", "System.Single,EventRouteData&,EventManager+EventType,System.Boolean,System.Int32&,System.UInt16[]&", "UnityEngine.Vector3[]", false, "R"),
            new Shape("RaceEventAI", "GenerateTrackSpline", "EventRouteData&,System.Boolean", "RouteSpline", false, "R"),
            new Shape("RaceRoadBaseAI", "FindStartSegment", "System.UInt16,Building&,System.UInt16&,System.UInt16&", "System.Boolean", true, "B"),
            new Shape("RaceStartBuildingAI", "CalculatePosition", "System.UInt16,Building,ColossalFramework.Math.Randomizer,CitizenInfo,UnityEngine.Vector3&,UnityEngine.Vector3&,UnityEngine.Vector2&,CitizenInstance+Flags&", "System.Boolean", true, "R"),
            new Shape("RaceStartBuildingAI", "InvertRoadDirection", "System.UInt16,Building&,System.UInt16", "System.Void", false, "B")
        };
        private static Shape Reviewed(MethodBase original)
        {
            MethodInfo m = original as MethodInfo;
            if (m == null || m.DeclaringType == null || m.DeclaringType.Assembly != typeof(NetManager).Assembly) return null;
            List<string> args = new List<string>(); foreach (ParameterInfo p in m.GetParameters()) args.Add(p.ParameterType.FullName);
            foreach (Shape s in Shapes)
                if (m.DeclaringType.FullName == s.Type && m.Name == s.Name && m.IsStatic == s.Static
                    && m.ReturnType.FullName == s.Result && string.Join(",", args.ToArray()) == s.Parameters) return s;
            return null;
        }
        internal static bool Handles(MethodBase original) { return Reviewed(original) != null; }
        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            Shape shape = Reviewed(original);
            if (shape == null || code == null || !LocalsAgree(original, code)) return null;
            List<int> found = new List<int>(); string kinds = "";
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || (!object.Equals(code[i].operand, 32768) && !object.Equals(code[i].operand, 36864))) continue;
                char kind = '\0';
                foreach (char candidate in shape.Kinds)
                    if (Guard(original, code, i, candidate)) { if (kind != '\0') return null; kind = candidate; }
                if (kind != '\0') { kinds += kind; found.Add(i); }
            }
            return kinds == shape.Kinds ? found.ToArray() : null;
        }
        private static bool Guard(MethodBase original, List<CodeInstruction> code, int bound, char kind)
        {
            if (!object.Equals(code[bound].operand, kind == 'B' ? 32768 : 36864)) return false;
            OpCode predicate = kind == 'G' || original.Name == "CalculatePosition" ? OpCodes.Blt : OpCodes.Ble;
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, predicate)) return false;
            int condition = Skip(code, LabelIndex(code, code[branch].operand));
            if (condition <= branch) return false;
            int cursor = Local(code[condition], false), back = Next(code, condition);
            if (cursor < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return false;
            int start = Skip(code, LabelIndex(code, code[back].operand)), exit = Next(code, back);
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one), counter = store < 0 ? -1 : Local(code[store], true);
            if (start < 0 || load < start || counter < 0 || cursor == counter || Local(code[load], false) != counter
                || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1
                || !TypeIs(original, code[store], counter, typeof(int)) || !TypeIs(original, code[condition], cursor, typeof(ushort))
                || !Handler(code, branch, condition, exit)) return false;
            int entry = Previous(code, start), initial = LastStore(code, counter, start), zero = Previous(code, initial);
            if (zero < 0 || code[zero].opcode != OpCodes.Ldc_I4_0 || !Branch(code[entry].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[entry].operand)) != condition || !Stable(code, counter, initial + 1, load)) return false;
            for (int i = start; i <= back; ++i) if (Address(code[i]) == cursor || Address(code[i]) == counter) return false;
            int cursorStore = LastStore(code, cursor, start, load);
            if (cursorStore < start || !Stable(code, cursor, start, cursorStore)) return false;
            if (kind == 'B' || kind == 'G')
            {
                string next = kind == 'B' ? "m_nextBuildingNode" : "m_nextGridSegment";
                Type item = kind == 'B' ? typeof(NetNode) : typeof(NetSegment);
                if (!ReadLink(original, code, Previous(code, cursorStore), cursor, start, back + 1, item, next)) return false;
                int seed = LastStore(code, cursor, start), value = Previous(code, seed);
                return seed >= 0 && (kind == 'B' ? BuildingRoot(original, code, value, start) : GridRoot(original, code, value, start));
            }
            if (!SeedLocal(original, code, cursor, start, start, 0)) return false;
            int source = Previous(code, cursorStore), saved = Local(code[source], false);
            if (saved < 0 || saved == cursor || saved == counter || !TypeIs(original, code[source], saved, typeof(ushort))) return false;
            if (kind == 'Q') return Generated(original, code, start, load, cursor, cursorStore, saved, exit);
            int savedStore = LastStore(code, saved, start, cursorStore), link = Previous(code, savedStore);
            if (savedStore < start || !ReadLink(original, code, link, cursor, start, back + 1, typeof(NetSegment), "m_nextEventRouteSegment")
                || !Stable(code, saved, start, savedStore) || !Stable(code, saved, savedStore + 1, load)) return false;
            if (kind == 'U') return Unlink(original, code, savedStore, cursorStore, saved, cursor, start, load, exit);
            // A route walk also chooses the opposite endpoint; it is not the
            // unlink-only loop with an otherwise similar segment counter.
            int endpointStore, currentNode, nextNode;
            if (!EndpointAfter(original, code, savedStore, cursor, start, load, out endpointStore, out currentNode, out nextNode)) return false;
            if (!Stable(code, nextNode, start, endpointStore) || !Stable(code, nextNode, endpointStore + 1, load)) return false;
            if (!NodeRoot(original, code, currentNode, start, cursor, false, 0)) return false;
            for (int i = start; i < load; ++i) if (Address(code[i]) == currentNode || Address(code[i]) == nextNode) return false;
            int publication = -1;
            for (int i = start; i < load; ++i) if (Local(code[i], true) == currentNode)
            {
                if (publication >= 0 || Local(code[Previous(code, i)], false) != nextNode) return false;
                publication = i;
            }
            if (original.Name == "CalculatePosition" ? publication >= 0 : publication < cursorStore) return false;
            return Completion(original, code, start, load, saved, cursorStore, currentNode, publication, exit, false);
        }
        private static bool Unlink(MethodBase original, List<CodeInstruction> code, int savedStore, int cursorStore, int saved, int cursor, int start, int end, int exit)
        {
            int read = Next(code, savedStore), arg = Next(code, read), compare = Next(code, arg);
            if (compare < 0 || Local(code[read], false) != saved || code[arg].opcode != OpCodes.Ldarg_1
                || !Branch(code[compare].opcode, OpCodes.Bne_Un)
                || Skip(code, LabelIndex(code, code[compare].operand)) != Previous(code, cursorStore)) return false;
            int array = Next(code, compare), index = Next(code, array), element = Next(code, index), zero = Next(code, element);
            int field = Next(code, zero), jump = Next(code, field);
            return field >= 0 && jump >= 0 && Next(code, jump) == Previous(code, cursorStore)
                && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema
                && NetArray(original, code, array, typeof(NetSegment), end)
                && object.Equals(code[element].operand, typeof(NetSegment)) && code[zero].opcode == OpCodes.Ldc_I4_0
                && StoreField(code[field], typeof(NetSegment), "m_nextEventRouteSegment") && Branch(code[jump].opcode, OpCodes.Br)
                && Skip(code, LabelIndex(code, code[jump].operand)) == exit;
        }
        private static bool EndpointAfter(MethodBase original, List<CodeInstruction> code, int after, int cursor, int start, int end,
            out int endpointStore, out int currentNode, out int nextNode)
        {
            endpointStore = currentNode = nextNode = -1;
            int array = Next(code, after), index = Next(code, array), element = Next(code, index), first = Next(code, element);
            int old = Next(code, first), compare = Next(code, old), left = Next(code, compare), leftIndex = Next(code, left);
            int leftElement = Next(code, leftIndex), leftField = Next(code, leftElement), jump = Next(code, leftField);
            int right = Next(code, jump), rightIndex = Next(code, right), rightElement = Next(code, rightIndex), rightField = Next(code, rightElement);
            int store = Next(code, rightField);
            if (store < 0 || store >= end || !Field(code[first], typeof(NetSegment), "m_startNode")
                || !Field(code[leftField], typeof(NetSegment), "m_endNode") || !Field(code[rightField], typeof(NetSegment), "m_startNode")
                || !Element(code, index, element, cursor, typeof(NetSegment))
                || !Element(code, leftIndex, leftElement, cursor, typeof(NetSegment))
                || !Element(code, rightIndex, rightElement, cursor, typeof(NetSegment))
                || !NetArray(original, code, array, typeof(NetSegment), end) || !NetArray(original, code, left, typeof(NetSegment), end)
                || !NetArray(original, code, right, typeof(NetSegment), end)
                || !Branch(code[compare].opcode, OpCodes.Bne_Un) || Skip(code, LabelIndex(code, code[compare].operand)) != right
                || !Branch(code[jump].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[jump].operand)) != store) return false;
            currentNode = Local(code[old], false); nextNode = Local(code[store], true);
            if (currentNode < 0 || nextNode < 0 || currentNode == nextNode || nextNode == cursor || currentNode == cursor
                || !TypeIs(original, code[old], currentNode, typeof(ushort)) || !TypeIs(original, code[store], nextNode, typeof(ushort))) return false;
            endpointStore = store; return true;
        }
        private static bool Generated(MethodBase original, List<CodeInstruction> code, int start, int end, int cursor, int cursorStore, int candidate, int exit)
        {
            // BuildEventRoute constructs nextEventRouteSegment from neighboring
            // roads, rather than reading an already-built event linked list.
            int zeroes = 0, nodeChoices = 0, neighborChoices = 0, linkWrites = 0;
            int companion = -1, savedCompanion = -1, companionStore = -1, savedCompanionStore = -1;
            for (int i = cursorStore + 1; i < end; ++i)
                if (Local(code[i], true) >= 0)
                {
                    int nextNode = Local(code[Previous(code, i)], false);
                    if (nextNode < 0) return false;
                    // Locate its reviewed opposite-endpoint expression.
                    for (int j = start; j < cursorStore; ++j)
                    {
                        int endpointStore, currentNode, savedNode;
                        if (!EndpointAfter(original, code, j, candidate, start, end, out endpointStore, out currentNode, out savedNode)
                            || savedNode != nextNode || Local(code[i], true) != currentNode) continue;
                        companion = currentNode; savedCompanion = savedNode; companionStore = i; savedCompanionStore = endpointStore;
                    }
                    break;
                }
            if (companion < 0 || !GeneratedNodeSeed(original, code, companion, cursor, start)) return false;
            for (int i = start; i < end; ++i)
            {
                if (Address(code[i]) == companion || Address(code[i]) == savedCompanion) return false;
                if (Local(code[i], true) == companion && i != companionStore || Local(code[i], true) == savedCompanion && i != savedCompanionStore) return false;
            }
            for (int i = start; i < cursorStore; ++i)
            {
                if (Address(code[i]) == candidate) return false;
                if (Local(code[i], true) == candidate)
                {
                    int value = Previous(code, i);
                    if (code[value].opcode == OpCodes.Ldc_I4_0) { if (++zeroes != 1 || nodeChoices != 0 || neighborChoices != 0) return false; continue; }
                    int selected = Local(code[value], false);
                    if (selected < 0 || selected == cursor || !TypeIs(original, code[value], selected, typeof(ushort))) return false;
                    if (NodeChoice(original, code, selected, i, companion, start)) ++nodeChoices;
                    else if (NeighborChoice(original, code, selected, i, cursor, start)) ++neighborChoices;
                    else return false;
                }
                if (StoreField(code[i], typeof(NetSegment), "m_nextEventRouteSegment"))
                {
                    int value = Previous(code, i);
                    if (Local(code[value], false) != candidate) continue;
                    int element = Previous(code, value), index = Previous(code, element), array = Previous(code, index);
                    if (!Element(code, index, element, cursor, typeof(NetSegment)) || !NetArray(original, code, array, typeof(NetSegment), end)) return false;
                    ++linkWrites;
                }
            }
            return zeroes == 1 && nodeChoices == 2 && neighborChoices == 1 && linkWrites == 1
                && Completion(original, code, start, end, candidate, cursorStore, companion, companionStore, exit, true);
        }
        private static bool GeneratedNodeSeed(MethodBase original, List<CodeInstruction> code, int node, int segment, int start)
        {
            int seed = LastStore(code, node, start);
            if (seed < 0 || !Stable(code, node, seed + 1, start)) return false;
            for (int i = 0; i < seed; ++i)
            {
                int endpoint, current, next;
                if (!EndpointAfter(original, code, i, segment, 0, start, out endpoint, out current, out next) || endpoint != seed || next != node) continue;
                int initial = LastStore(code, current, i), field = Previous(code, initial);
                if (field < 0 || !Field(code[field], typeof(EventRouteData), "m_startNode") || !RouteRead(original, code, field, start, 0)) return false;
                for (int j = initial + 1; j < start; ++j)
                {
                    if (Local(code[j], true) == current) return false;
                    if (Address(code[j]) != current) continue;
                    int other = Next(code, j), call = Next(code, other);
                    if (Address(code[other]) < 0 || !StartDiscovery(code, call)) return false;
                }
                return true;
            }
            return false;
        }
        private static bool Completion(MethodBase original, List<CodeInstruction> code, int start, int end, int saved, int cursorStore,
            int node, int publication, int exit, bool generated)
        {
            bool nodeRing = original.DeclaringType == typeof(RaceEventAI) || original.Name == "UpdateEventRouteRoadWarnings";
            int matches = 0;
            for (int i = start; i < end; ++i)
            {
                int right = Next(code, i), compare = Next(code, right);
                if (compare < 0 || !Branch(code[compare].opcode, OpCodes.Bne_Un)) continue;
                if (nodeRing)
                {
                    int sentinel = Local(code[right], false), ringJump = Next(code, compare);
                    if (Local(code[i], false) != node || i <= publication || sentinel < 0 || sentinel == node
                        || !TypeIs(original, code[right], sentinel, typeof(ushort)) || !Stable(code, sentinel, start, end)
                        || !NodeRoot(original, code, sentinel, start, -1, true, 0)
                        || !Branch(code[ringJump].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[ringJump].operand)) != exit
                        || Skip(code, LabelIndex(code, code[compare].operand)) != end) continue;
                    ++matches; continue;
                }
                int initial = Local(code[right], false);
                if (Local(code[i], false) != saved || initial < 0 || initial == saved || !SeedLocal(original, code, initial, start, start, 0)) continue;
                int target = Skip(code, LabelIndex(code, code[compare].operand));
                if (target <= compare || target > cursorStore) return false;
                int jump = generated ? Previous(code, target) : Next(code, compare);
                if (!Branch(code[jump].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[jump].operand)) != exit) return false;
                if (original.Name == "CalculateEventRoute" || original.Name == "CalculatePosition")
                {
                    int zeroBranch = Previous(code, i), zeroRead = Previous(code, zeroBranch);
                    if (Local(code[zeroRead], false) != saved || !Branch(code[zeroBranch].opcode, OpCodes.Brfalse)
                        || Skip(code, LabelIndex(code, code[zeroBranch].operand)) != jump || target != Previous(code, cursorStore)) return false;
                }
                if (original.Name == "UpdateEventRoute")
                {
                    int stop = Previous(code, i), update = Previous(code, stop), nextNode = Previous(code, update), manager = Previous(code, nextNode);
                    int test = Previous(code, manager), value = Previous(code, test);
                    MethodInfo refresh = update < 0 ? null : code[update].operand as MethodInfo;
                    if (value < start || Local(code[value], false) != saved || !Branch(code[test].opcode, OpCodes.Brtrue)
                        || Skip(code, LabelIndex(code, code[test].operand)) != i || !Branch(code[stop].opcode, OpCodes.Br)
                        || Skip(code, LabelIndex(code, code[stop].operand)) != exit || !Manager(original, code, manager, end, typeof(NetManager))
                        || Local(code[nextNode], false) != Local(code[Previous(code, publication)], false)
                        || code[update].opcode != OpCodes.Callvirt || refresh == null || refresh.DeclaringType != typeof(NetManager)
                        || refresh.Name != "UpdateNode" || refresh.ReturnType != typeof(void) || refresh.GetParameters().Length != 1
                        || refresh.GetParameters()[0].ParameterType != typeof(ushort)) return false;
                }
                ++matches;
            }
            return matches == 1;
        }
        private static bool NodeRoot(MethodBase original, List<CodeInstruction> code, int local, int before, int cursor, bool endAllowed, int depth)
        {
            if (depth > 10 || local < 0) return false;
            int epoch = 0; bool found = false;
            for (int i = 0; i < before; ++i) if (Singleton(code[i], typeof(NetManager))) epoch = i;
            for (int i = epoch; i < before; ++i)
            {
                if (Address(code[i]) == local) return false;
                if (Local(code[i], true) != local) continue;
                found = true; int value = Previous(code, i), alias = Local(code[value], false);
                if (alias >= 0)
                { if (alias == local || !NodeRoot(original, code, alias, value, cursor, false, depth + 1)) return false; continue; }
                if (Field(code[value], typeof(EventRouteData), endAllowed ? "m_endNode" : "m_startNode")
                    && RouteRead(original, code, value, before, 0)) continue;
                if (original.Name == "BreakEventRoute")
                {
                    int element = Previous(code, value), index = Previous(code, element), array = Previous(code, index);
                    if (Field(code[value], typeof(NetSegment), "m_startNode") && code[index].opcode == OpCodes.Ldarg_1
                        && code[element].opcode == OpCodes.Ldelema && object.Equals(code[element].operand, typeof(NetSegment))
                        && NetArray(original, code, array, typeof(NetSegment), before)) continue;
                    MethodInfo call = code[value].operand as MethodInfo;
                    int next = Previous(code, value), first = Previous(code, next), manager = Previous(code, first);
                    if (code[value].opcode == OpCodes.Callvirt && call != null && call.DeclaringType == typeof(NetManager)
                        && call.Name == "GetNodeBetweenSegments" && call.ReturnType == typeof(ushort) && call.GetParameters().Length == 2
                        && call.GetParameters()[0].ParameterType == typeof(ushort) && call.GetParameters()[1].ParameterType == typeof(ushort)
                        && code[first].opcode == OpCodes.Ldarg_1 && Local(code[next], false) == cursor
                        && Manager(original, code, manager, before, typeof(NetManager))) continue;
                }
                int right = RouteEndpoint(original, code, value, "m_startNode", before);
                int jump = Previous(code, right), leftField = Previous(code, jump);
                int left = RouteEndpoint(original, code, leftField, "m_endNode", before), compare = Previous(code, left), old = Previous(code, compare);
                int lhs = Previous(code, Previous(code, old));
                if (right < 0 || left < 0 || !Branch(code[jump].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[jump].operand)) != i
                    || !Branch(code[compare].opcode, OpCodes.Bne_Un) || Skip(code, LabelIndex(code, code[compare].operand)) != right
                    || !Field(code[old], typeof(EventRouteData), "m_startNode") || !RouteRead(original, code, old, before, 0)
                    || RouteEndpoint(original, code, lhs, "m_startNode", before) < 0) return false;
            }
            return found;
        }
        private static int RouteEndpoint(MethodBase original, List<CodeInstruction> code, int field, string name, int end)
        {
            if (field < 0 || !Field(code[field], typeof(NetSegment), name)) return -1;
            int element = Previous(code, field), routeField = Previous(code, element), route = Previous(code, routeField), array = Previous(code, route);
            return array >= 0 && code[element].opcode == OpCodes.Ldelema && object.Equals(code[element].operand, typeof(NetSegment))
                && Field(code[routeField], typeof(EventRouteData), "m_startSegment") && RouteRead(original, code, routeField, end, 0)
                && NetArray(original, code, array, typeof(NetSegment), end) ? array : -1;
        }
        private static bool StartDiscovery(List<CodeInstruction> code, int call)
        {
            if (!Call(code, call, typeof(RaceRoadBaseAI), "FindStartSegment")) return false;
            MethodInfo m = (MethodInfo)code[call].operand; ParameterInfo[] p = m.GetParameters();
            return m.IsStatic && m.ReturnType == typeof(bool) && p.Length == 4 && p[0].ParameterType == typeof(ushort)
                && p[1].ParameterType == typeof(Building).MakeByRefType() && p[2].ParameterType == typeof(ushort).MakeByRefType()
                && p[3].ParameterType == typeof(ushort).MakeByRefType();
        }
        private static bool NodeChoice(MethodBase original, List<CodeInstruction> code, int choice, int before, int node, int start)
        {
            int store = LastStore(code, choice, start, before), call = Previous(code, store), slot = Previous(code, call);
            int element = Previous(code, slot), index = Previous(code, element), array = Previous(code, index);
            MethodInfo method = call < 0 ? null : code[call].operand as MethodInfo;
            int s = slot < 0 ? -1 : Local(code[slot], false);
            return array >= start && code[call].opcode == OpCodes.Call && method != null && method.DeclaringType == typeof(NetNode)
                && method.Name == "GetSegment" && method.ReturnType == typeof(ushort) && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(int) && s >= 0 && TypeIs(original, code[slot], s, typeof(int))
                && Element(code, index, element, node, typeof(NetNode)) && NetArray(original, code, array, typeof(NetNode), before)
                && Stable(code, choice, store + 1, before);
        }
        private static bool NeighborChoice(MethodBase original, List<CodeInstruction> code, int choice, int before, int cursor, int start)
        {
            int copies = 0, phi = 0;
            for (int i = start; i < before; ++i)
            {
                if (Address(code[i]) == choice) return false;
                if (Local(code[i], true) != choice) continue;
                int field = Previous(code, i);
                if (Local(code[field], false) == cursor) { ++copies; continue; }
                FieldInfo f = code[field].operand as FieldInfo;
                if (f == null || f.DeclaringType != typeof(NetSegment) || (f.Name != "m_startRightSegment" && f.Name != "m_endRightSegment")) return false;
                if (!ReadLink(original, code, field, choice, start, before, typeof(NetSegment), f.Name)) return false;
                int rightArray = Previous(code, Previous(code, Previous(code, field))), jump = Previous(code, rightArray), leftField = Previous(code, jump);
                string left = f.Name == "m_startRightSegment" ? "m_startLeftSegment" : "m_endLeftSegment";
                if (!ReadLink(original, code, leftField, choice, start, before, typeof(NetSegment), left)
                    || !Branch(code[jump].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[jump].operand)) != i) return false;
                ++phi;
            }
            return copies == 1 && phi == 2;
        }

        private static bool SeedLocal(MethodBase original, List<CodeInstruction> code, int local, int before, int end, int depth)
        {
            if (depth > 12 || local < 0) return false; bool found = false;
            int epoch = 0;
            for (int i = 0; i < before; ++i) if (Singleton(code[i], typeof(NetManager))) epoch = i;
            for (int i = epoch; i < before; ++i)
            {
                if (Address(code[i]) == local)
                {
                    // Only the reviewed start-road discovery can refine the
                    // pre-loop segment seed by reference.
                    int call = Next(code, i);
                    if (call >= 0 && Address(code[call]) >= 0) call = Next(code, call);
                    if (original.Name != "BuildEventRoute" || !StartDiscovery(code, call)) return false;
                }
                if (Local(code[i], true) != local) continue;
                found = true;
                int value = Previous(code, i), source = Local(code[value], false);
                if (source >= 0)
                { if (source == local || !SeedLocal(original, code, source, value, end, depth + 1)) return false; continue; }
                if (Field(code[value], typeof(EventRouteData), "m_startSegment") && RouteRead(original, code, value, end, depth + 1)) continue;
                if (Field(code[value], typeof(NetSegment), "m_nextEventRouteSegment"))
                {
                    int element = Previous(code, value), index = Previous(code, element), array = Previous(code, index);
                    if (Field(code[index], typeof(EventRouteData), "m_startSegment")) array = Previous(code, array);
                    if (!NetArray(original, code, array, typeof(NetSegment), end) || code[element].opcode != OpCodes.Ldelema) return false;
                    if (original.Name == "BreakEventRoute" && code[index].opcode == OpCodes.Ldarg_1) continue;
                    if (Field(code[index], typeof(EventRouteData), "m_startSegment") && RouteRead(original, code, index, end, depth + 1)) continue;
                }
                return false;
            }
            return found;
        }
        private static bool RouteRead(MethodBase original, List<CodeInstruction> code, int field, int end, int depth)
        {
            if (depth > 12) return false;
            int source = Previous(code, field);
            int argument = Argument(original, code[source]);
            if (argument >= 0)
            {
                int position = argument - (original.IsStatic ? 0 : 1);
                return position >= 0 && position < original.GetParameters().Length
                    && original.GetParameters()[position].ParameterType == typeof(EventRouteData).MakeByRefType();
            }
            int local = Address(code[source]);
            if (local >= 0)
            {
                if (!TypeIs(original, code[source], local, typeof(EventRouteData))) return false;
                int init = LastStore(code, local, source), value = Previous(code, init);
                if (value < 0) return false;
                if (code[value].opcode == OpCodes.Ldobj && object.Equals(code[value].operand, typeof(EventRouteData)))
                    return RouteAddress(original, code, Previous(code, value), end);
                MethodInfo get = code[value].operand as MethodInfo;
                int index = Previous(code, value), routes = Previous(code, index), owner = Previous(code, routes);
                return code[value].opcode == OpCodes.Callvirt && get != null && get.DeclaringType == typeof(FastList<EventRouteData>)
                    && get.Name == "get_Item" && get.ReturnType == typeof(EventRouteData) && owner >= 0 && code[owner].opcode == OpCodes.Ldarg_0
                    && Field(code[routes], typeof(EventManager), "m_eventRoutes");
            }
            return RouteAddress(original, code, source, end);
        }
        private static bool RouteAddress(MethodBase original, List<CodeInstruction> code, int element, int end)
        {
            int index = Previous(code, element), array = Previous(code, index);
            if (array < 0 || code[element].opcode != OpCodes.Ldelema || !object.Equals(code[element].operand, typeof(EventRouteData))) return false;
            if (Field(code[array], typeof(FastList<EventRouteData>), "m_buffer"))
            {
                int routes = Previous(code, array), owner = Previous(code, routes);
                return owner >= 0 && code[owner].opcode == OpCodes.Ldarg_0 && Field(code[routes], typeof(EventManager), "m_eventRoutes");
            }
            int local = Local(code[array], false);
            if (local < 0 || !TypeIs(original, code[array], local, typeof(EventRouteData[]))) return false;
            int init = LastStore(code, local, array), buffer = Previous(code, init), field = Previous(code, buffer), ownerUse = Previous(code, field);
            if (ownerUse < 0 || !Field(code[buffer], typeof(FastList<EventRouteData>), "m_buffer")
                || !Field(code[field], typeof(EventManager), "m_eventRoutes")) return false;
            if (Singleton(code[ownerUse], typeof(EventManager))) return Stable(code, local, init + 1, end);
            int manager = Local(code[ownerUse], false), managerInit = LastStore(code, manager, ownerUse), call = Previous(code, managerInit);
            return call >= 0 && Singleton(code[call], typeof(EventManager)) && Stable(code, manager, managerInit + 1, end)
                && Stable(code, local, init + 1, end);
        }
        private static bool BuildingRoot(MethodBase original, List<CodeInstruction> code, int field, int end)
        {
            if (field < 0 || !Field(code[field], typeof(Building), "m_netNode")) return false;
            int source = Previous(code, field), arg = Argument(original, code[source]);
            if (arg >= 0)
            {
                int position = arg - (original.IsStatic ? 0 : 1);
                return position >= 0 && position < original.GetParameters().Length
                    && original.GetParameters()[position].ParameterType == typeof(Building).MakeByRefType();
            }
            int index = Previous(code, source), buffer = Previous(code, index), pool = Previous(code, buffer), manager = Previous(code, pool);
            return manager >= 0 && code[source].opcode == OpCodes.Ldelema && object.Equals(code[source].operand, typeof(Building))
                && Field(code[buffer], typeof(Array16<Building>), "m_buffer") && Field(code[pool], typeof(BuildingManager), "m_buildings")
                && Manager(original, code, manager, end, typeof(BuildingManager));
        }
        private static bool GridRoot(MethodBase original, List<CodeInstruction> code, int read, int end)
        {
            int add = Previous(code, read), x = Previous(code, add), mul = Previous(code, x), width = Previous(code, mul);
            int z = Previous(code, width), grid = Previous(code, z), manager = Previous(code, grid);
            return manager >= 0 && code[read].opcode == OpCodes.Ldelem_U2 && code[add].opcode == OpCodes.Add && code[mul].opcode == OpCodes.Mul
                && code[width].opcode == OpCodes.Ldc_I4 && object.Equals(code[width].operand, 270)
                && Local(code[x], false) >= 0 && TypeIs(original, code[x], Local(code[x], false), typeof(int))
                && Local(code[z], false) >= 0 && TypeIs(original, code[z], Local(code[z], false), typeof(int)) && Field(code[grid], typeof(NetManager), "m_segmentGrid")
                && Manager(original, code, manager, end, typeof(NetManager));
        }
        private static bool ReadLink(MethodBase original, List<CodeInstruction> code, int field, int cursor, int start, int end, Type item, string name)
        {
            int element = Previous(code, field), index = Previous(code, element), array = Previous(code, index);
            return array >= start && Field(code[field], item, name) && Element(code, index, element, cursor, item)
                && NetArray(original, code, array, item, end);
        }
        private static bool Element(List<CodeInstruction> code, int index, int element, int cursor, Type item)
        { return index >= 0 && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema && object.Equals(code[element].operand, item); }
        private static bool NetArray(MethodBase original, List<CodeInstruction> code, int use, Type item, int end)
        {
            if (use < 0) return false;
            Type poolType = item == typeof(NetNode) ? typeof(Array16<NetNode>) : typeof(Array16<NetSegment>);
            string poolName = item == typeof(NetNode) ? "m_nodes" : "m_segments";
            int buffer = use, init = -1, local = Local(code[use], false);
            if (local >= 0)
            {
                if (!TypeIs(original, code[use], local, item.MakeArrayType())) return false;
                init = LastStore(code, local, use); buffer = Previous(code, init);
                if (!Stable(code, local, init + 1, end)) return false;
            }
            int pool = Previous(code, buffer), owner = Previous(code, pool);
            if (owner < 0 || !Field(code[buffer], poolType, "m_buffer") || !Field(code[pool], typeof(NetManager), poolName)) return false;
            return Singleton(code[owner], typeof(NetManager)) || Manager(original, code, owner, end, typeof(NetManager));
        }
        private static bool Manager(MethodBase original, List<CodeInstruction> code, int use, int end, Type type)
        {
            if (use < 0) return false; int local = Local(code[use], false);
            if (local < 0 || !TypeIs(original, code[use], local, type)) return false;
            int init = LastStore(code, local, use), call = Previous(code, init);
            return call >= 0 && Singleton(code[call], type) && Stable(code, local, init + 1, end);
        }
        private static bool Handler(List<CodeInstruction> code, int branch, int condition, int exit)
        {
            int i = Next(code, branch); if (i < 0 || code[i].opcode != OpCodes.Ldc_I4_1) return false;
            i = Next(code, i); if (i < 0 || code[i].opcode != OpCodes.Ldstr || !object.Equals(code[i].operand, "Invalid list detected!\n")) return false;
            i = Next(code, i); if (!Call(code, i, typeof(Environment), "get_StackTrace")) return false;
            i = Next(code, i); MethodInfo concat = i < 0 ? null : code[i].operand as MethodInfo;
            if (!Call(code, i, typeof(string), "Concat") || concat.ReturnType != typeof(string) || concat.GetParameters().Length != 2) return false;
            i = Next(code, i); if (!Call(code, i, typeof(CODebugBase<LogChannel>), "Error")) return false;
            i = Next(code, i);
            return i >= 0 && Branch(code[i].opcode, OpCodes.Br) && Next(code, i) == condition && Skip(code, LabelIndex(code, code[i].operand)) == exit;
        }
        private static bool LocalsAgree(MethodBase original, List<CodeInstruction> code)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables; Dictionary<int, Type> added = new Dictionary<int, Type>();
            foreach (CodeInstruction c in code)
            {
                if (!c.opcode.Name.StartsWith("ldloc") && !c.opcode.Name.StartsWith("stloc")) continue;
                int index = Local(c, false); if (index < 0) index = Local(c, true); if (index < 0) index = Address(c);
                if (index < 0) return false;
                LocalVariableInfo variable = c.operand as LocalVariableInfo;
                if (index < locals.Count)
                { if (variable != null && (variable.LocalIndex != index || variable.LocalType != locals[index].LocalType)) return false; }
                else
                { Type previous; if (variable == null || variable.LocalIndex != index || added.TryGetValue(index, out previous) && previous != variable.LocalType) return false; added[index] = variable.LocalType; }
            }
            return true;
        }
        private static bool TypeIs(MethodBase original, CodeInstruction access, int index, Type type)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (index < 0 || index >= locals.Count) return false;
            LocalVariableInfo variable = access.operand as LocalVariableInfo;
            return (variable != null ? variable.LocalType : locals[index].LocalType) == type;
        }
        private static bool Stable(List<CodeInstruction> code, int local, int start, int end)
        { for (int i = start; i < end; ++i) if (Local(code[i], true) == local || Address(code[i]) == local) return false; return true; }
        private static bool Field(CodeInstruction code, Type type, string name)
        { FieldInfo f = code.operand as FieldInfo; return code.opcode == OpCodes.Ldfld && f != null && f.DeclaringType == type && f.Name == name; }
        private static bool StoreField(CodeInstruction code, Type type, string name)
        { FieldInfo f = code.operand as FieldInfo; return code.opcode == OpCodes.Stfld && f != null && f.DeclaringType == type && f.Name == name; }
        private static bool Call(List<CodeInstruction> code, int i, Type type, string name)
        { MethodInfo m = i < 0 ? null : code[i].operand as MethodInfo; return m != null && code[i].opcode == OpCodes.Call && m.DeclaringType == type && m.Name == name; }
        private static bool Singleton(CodeInstruction code, Type type)
        {
            MethodInfo m = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && m != null && m.DeclaringType == typeof(Singleton<>).MakeGenericType(type)
                && m.Name == "get_instance" && m.ReturnType == type && m.IsStatic && m.GetParameters().Length == 0;
        }
        private static bool Branch(OpCode actual, OpCode expected)
        {
            return actual == expected || expected == OpCodes.Br && actual == OpCodes.Br_S
                || expected == OpCodes.Brtrue && actual == OpCodes.Brtrue_S || expected == OpCodes.Brfalse && actual == OpCodes.Brfalse_S
                || expected == OpCodes.Blt && actual == OpCodes.Blt_S || expected == OpCodes.Ble && actual == OpCodes.Ble_S
                || expected == OpCodes.Bne_Un && actual == OpCodes.Bne_Un_S;
        }
        private static int Argument(MethodBase original, CodeInstruction code)
        {
            if (code.opcode == OpCodes.Ldarg_0) return 0; if (code.opcode == OpCodes.Ldarg_1) return 1;
            if (code.opcode == OpCodes.Ldarg_2) return 2; if (code.opcode == OpCodes.Ldarg_3) return 3;
            if (code.opcode != OpCodes.Ldarg && code.opcode != OpCodes.Ldarg_S) return -1;
            ParameterInfo p = code.operand as ParameterInfo; return p != null ? p.Position + (original.IsStatic ? 0 : 1) : Operand(code.operand);
        }
        private static int LabelIndex(List<CodeInstruction> code, object operand)
        {
            if (!(operand is Label)) return -1; int result = -1;
            for (int i = 0; i < code.Count; ++i) if (code[i].labels.Contains((Label)operand)) { if (result >= 0) return -1; result = i; }
            return result;
        }
        private static int Skip(List<CodeInstruction> code, int index)
        { if (index < 0) return -1; while (index < code.Count && code[index].opcode == OpCodes.Nop) ++index; return index < code.Count ? index : -1; }
        private static int Next(List<CodeInstruction> code, int index) { return index < 0 ? -1 : Skip(code, index + 1); }
        private static int Previous(List<CodeInstruction> code, int index)
        { --index; while (index >= 0 && code[index].opcode == OpCodes.Nop) --index; return index; }
        private static int LastStore(List<CodeInstruction> code, int local, int end) { return LastStore(code, local, 0, end); }
        private static int LastStore(List<CodeInstruction> code, int local, int start, int end)
        { for (int i = end - 1; i >= start; --i) if (Local(code[i], true) == local) return i; return -1; }
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
        { LocalVariableInfo v = value as LocalVariableInfo; if (v != null) return v.LocalIndex; if (value is int) return (int)value; if (value is byte) return (byte)value; if (value is short) return (short)value; return -1; }
    }
}
