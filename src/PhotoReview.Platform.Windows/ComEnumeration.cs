using System.Collections;
using System.Runtime.InteropServices;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// Enumerates an automation collection (Shell.Application <c>Windows()</c>, <c>Items()</c>, <c>Verbs()</c>) while owning
/// the underlying <c>IEnumVARIANT</c> RCW, so it can be released deterministically
/// (<see cref="Marshal.FinalReleaseComObject"/>) like every other RCW on these paths, instead of being left to the
/// finalizer (which is what a plain <c>foreach</c>/<c>Cast</c> over an <see cref="IEnumerable"/> does: the
/// enumerator wrapper hides its inner COM object). Falls back to the plain <see cref="IEnumerable"/> when the object
/// does not expose <c>_NewEnum</c> as an <c>IEnumVARIANT</c>.
/// </summary>
internal static class ComEnumeration
{
    [ComImport, Guid("00020404-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumVariant
    {
        [PreserveSig]
        int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 0)] object[] values, out int fetched);
    }

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchRaw
    {
        [PreserveSig] int GetTypeInfoCount(out int count);
        [PreserveSig] int GetTypeInfo(int index, int lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names, int count, int lcid, [Out] int[] ids);
        [PreserveSig] int Invoke(int dispId, ref Guid iid, int lcid, ushort flags, ref DispParams parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argumentError);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public IntPtr Arguments;
        public IntPtr NamedArgumentIds;
        public int ArgumentCount;
        public int NamedArgumentCount;
    }

    private const int DispIdNewEnum = -4;
    private const ushort DispatchMethod = 1, DispatchPropertyGet = 2;
    private const short VtDispatch = 9, VtUnknown = 13;
    private const int VariantSize = 24; // sizeof(VARIANT) on x64 (16 on x86; 24 covers both)

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr variant);

    /// <summary>Yields the collection's elements; the enumerator RCW is released when the iteration ends or is abandoned.</summary>
    /// <param name="collection">The automation collection.</param>
    /// <param name="onEnumeratorOpened">Test seam: receives the enumerator RCW that will be released.</param>
    internal static IEnumerable<object> Enumerate(object collection, Action<object>? onEnumeratorOpened = null)
    {
        IEnumVariant? enumerator = TryOpen(collection);
        if (enumerator is null)
        {
            foreach (var item in (IEnumerable)collection) yield return item;
            yield break;
        }
        try
        {
            onEnumeratorOpened?.Invoke(enumerator);
            var buffer = new object[1];
            while (true)
            {
                var hr = enumerator.Next(1, buffer, out var fetched);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                if (fetched == 0) yield break;
                var item = buffer[0];
                buffer[0] = null!;
                yield return item;
            }
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>
    /// Calls DISPID_NEWENUM through IDispatch and wraps the returned IUnknown as the raw <c>IEnumVARIANT</c> RCW.
    /// <c>Type.InvokeMember("_NewEnum")</c> cannot be used: the runtime's custom marshaler turns its result into a managed
    /// <c>EnumeratorViewOfEnumVariant</c>, whose inner COM object is private and so can never be released explicitly.
    /// </summary>
    private static IEnumVariant? TryOpen(object collection)
    {
        if (!Marshal.IsComObject(collection) || collection is not IDispatchRaw dispatch) return null;
        var variant = Marshal.AllocHGlobal(VariantSize);
        try
        {
            for (var i = 0; i < VariantSize; i++) Marshal.WriteByte(variant, i, 0);
            var iid = Guid.Empty;
            var parameters = new DispParams();
            var hr = dispatch.Invoke(DispIdNewEnum, ref iid, 0, DispatchMethod | DispatchPropertyGet, ref parameters, variant, IntPtr.Zero, IntPtr.Zero);
            if (hr < 0) return null;
            var vt = Marshal.ReadInt16(variant);
            if (vt != VtUnknown && vt != VtDispatch) return null;
            var unknown = Marshal.ReadIntPtr(variant, 8);
            if (unknown == IntPtr.Zero) return null;
            var wrapper = Marshal.GetObjectForIUnknown(unknown); // takes its own reference; the VARIANT's is cleared below
            if (wrapper is IEnumVariant typed) return typed;
            Release(wrapper);
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            _ = VariantClear(variant);
            Marshal.FreeHGlobal(variant);
        }
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
