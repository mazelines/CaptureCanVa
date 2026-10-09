using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CaptureCanva.Interop;

/// <summary>
/// Glue between Win32 handles / D3D11 (Vortice) and the WinRT capture API.
/// COM calls go through raw vtables so no built-in COM RCWs are involved.
/// </summary>
internal static unsafe class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // IGraphicsCaptureItemInterop vtable: IUnknown(0..2), CreateForWindow(3), CreateForMonitor(4)
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd) => CreateItem(hwnd, 3);

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, 4);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int slot)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        IntPtr interop = QueryInterface(factory.ThisPtr, GraphicsCaptureItemInteropIid);
        try
        {
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interop)[slot];
            Guid iid = GraphicsCaptureItemIid;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(create(interop, handle, &iid, &itemPtr));
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Returns the D3D11 texture behind a capture frame surface. Caller owns the result.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
        IntPtr access = QueryInterface(surfacePtr, DxgiInterfaceAccessIid);
        try
        {
            // IDirect3DDxgiInterfaceAccess vtable: IUnknown(0..2), GetInterface(3)
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
            Guid iid = Texture2DIid;
            IntPtr texture;
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    private static IntPtr QueryInterface(IntPtr unknown, Guid iid)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr result));
        return result;
    }
}
