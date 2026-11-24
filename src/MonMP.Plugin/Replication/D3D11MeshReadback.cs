using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Replication
{
    // Unity 2021's GraphicsBuffer readback cannot access these non-readable scene meshes. Copy
    // their D3D11 buffer into a temporary CPU-readable staging resource instead. No source writes.
    internal static class D3D11MeshReadback
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BufferDescription { internal uint Bytes, Usage, BindFlags, CpuAccess, MiscFlags, Stride; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MappedResource { internal IntPtr Data; internal uint RowPitch, DepthPitch; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetPointer(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDescription(IntPtr self, out BufferDescription description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBuffer(IntPtr self, ref BufferDescription description, IntPtr initialData, out IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapResource(IntPtr self, IntPtr resource, uint subresource, uint map, uint flags, out MappedResource result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapResource(IntPtr self, IntPtr resource, uint subresource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void LockContext(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetProtection(IntPtr self, int enabled);

        private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        internal static byte[] Read(IntPtr source, int minimumBytes)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || source == IntPtr.Zero)
                throw new NotSupportedException("Native seat surface readback requires Direct3D 11.");
            IntPtr device = IntPtr.Zero, context = IntPtr.Zero, threading = IntPtr.Zero, staging = IntPtr.Zero;
            bool entered = false, protectionChanged = false, mapped = false;
            try
            {
                Method<GetDescription>(source, 10)(source, out var original);
                if (minimumBytes <= 0 || original.Bytes < minimumBytes || original.Bytes > 32 * 1024 * 1024)
                    throw new InvalidOperationException("Seat GPU buffer size is invalid.");
                Method<GetPointer>(source, 3)(source, out device);
                Method<GetPointer>(device, 40)(device, out context);
                // The render thread also uses this immediate context. Protect and serialize access
                // through D3D11's own multithread interface while performing the read-only copy.
                var iid = new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, ref iid, out threading));
                protectionChanged = Method<SetProtection>(threading, 5)(threading, 1) == 0;
                Method<LockContext>(threading, 3)(threading); entered = true;
                var description = new BufferDescription { Bytes = original.Bytes, Usage = 3, CpuAccess = 0x20000 };
                Marshal.ThrowExceptionForHR(Method<CreateBuffer>(device, 3)(device, ref description, IntPtr.Zero, out staging));
                Method<CopyResource>(context, 47)(context, staging, source);
                Marshal.ThrowExceptionForHR(Method<MapResource>(context, 14)(context, staging, 0, 1, 0, out var data));
                mapped = true;
                var bytes = new byte[minimumBytes];
                Marshal.Copy(data.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (mapped) Method<UnmapResource>(context, 15)(context, staging, 0);
                if (staging != IntPtr.Zero) Marshal.Release(staging);
                if (entered) Method<LockContext>(threading, 4)(threading);
                if (protectionChanged) Method<SetProtection>(threading, 5)(threading, 0);
                if (threading != IntPtr.Zero) Marshal.Release(threading);
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }
    }
}
