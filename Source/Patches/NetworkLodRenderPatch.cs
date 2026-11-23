using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetSegment), "RenderLod", new[] { typeof(RenderManager.CameraInfo), typeof(NetInfo.LodValue) })]
    internal static class NetworkLodRenderPatch
    {
        private static bool _reported;

        private static Exception Finalizer(Exception __exception, NetInfo.LodValue __1)
        {
            if (!(__exception is NullReferenceException) || __1 == null) return __exception;

            StackFrame frame = new StackTrace(__exception, false).GetFrame(0);
            MethodBase method = frame == null ? null : frame.GetMethod();
            return FinishFailedBatch(__exception, __1, method);
        }

        internal static Exception FinishFailedBatch(Exception exception, NetInfo.LodValue lod, MethodBase origin)
        {
            if (!(exception is NullReferenceException) || lod == null || origin == null
                || origin.DeclaringType != typeof(MaterialPropertyBlock)
                || (origin.Name != "SetMatrixArray" && origin.Name != "SetMatrixArrayImpl")) return exception;
            // Abandon the failed draw batch so the frame can finish and the next
            // frame can collect new instances. Prefab and mesh data stay intact.
            int count = lod.m_lodCount;
            lod.m_lodCount = 0;
            if (!_reported)
            {
                _reported = true;
                Logging.Warning("Road LOD matrix submission failed; discarded draw batch (count=" + count
                    + ", left=" + Length(lod.m_leftMatrices) + ", right=" + Length(lod.m_rightMatrices)
                    + ", references=" + lod.m_refCount + ").");
            }
            return null;
        }

        private static int Length(Matrix4x4[] matrices) { return matrices == null ? -1 : matrices.Length; }
    }
}
