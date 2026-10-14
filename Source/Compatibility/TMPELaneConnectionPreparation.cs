using System;
using System.Reflection;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class TMPELaneConnectionPreparation
    {
        private const string Manager = "TrafficManager.Manager.Impl.LaneConnection.LaneConnectionManager";
        internal static void Prepare(Assembly assembly)
        {
            Type type = assembly.GetType(Manager, false);
            if (type == null) throw new InvalidOperationException("Required TM:PE lane-connection manager type missing.");
            object instance = ArrayCompatibility.GetStaticInstance(type, "Instance", "Instance", Manager);
            if (instance == null) throw new InvalidOperationException("Required TM:PE lane-connection manager unavailable.");
            PrepareInstance(instance);
        }
        internal static void PrepareInstance(object instance)
        {
            if (instance == null) throw new InvalidOperationException("Required TM:PE lane-connection manager unavailable.");
            MethodInfo initialize = instance.GetType().GetMethod("OnBeforeLoadData", BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (initialize == null || initialize.ReturnType != typeof(void))
                throw new InvalidOperationException("Required TM:PE lane-connection initializer missing.");
            try { initialize.Invoke(instance, null); }
            catch (Exception e) { throw new InvalidOperationException("Required TM:PE lane-connection initialization failed.", e); }
            Validate(instance);
        }
        internal static void Validate(object instance)
        {
            foreach (string name in new[] { "Road", "Track" })
            {
                FieldInfo field = ArrayCompatibility.FindField(instance.GetType(), name);
                object sub = field == null ? null : field.GetValue(instance);
                FieldInfo database = sub == null ? null : ArrayCompatibility.FindField(sub.GetType(), "connectionDataBase_");
                if (database == null || database.GetValue(sub) == null)
                    throw new InvalidOperationException("Required TM:PE " + name + " lane-connection database not initialized.");
            }
        }
    }
}
