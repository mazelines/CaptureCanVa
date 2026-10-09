using System.Runtime.InteropServices;
using CaptureCanva.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace CaptureCanva.Capture;

public enum FramePixelFormat
{
    /// <summary>Y plane followed by interleaved UV plane, BT.709 limited range. 1.5 bytes per pixel.</summary>
    Nv12,
    /// <summary>Packed BGRA, full-range sRGB. 4 bytes per pixel. Used when the GPU can't convert.</summary>
    Bgra,
}

/// <summary>
/// Captures a monitor or window with Windows.Graphics.Capture and keeps the most recent frame as a
/// fixed-size (<see cref="Width"/> × <see cref="Height"/>) buffer in <see cref="PixelFormat"/>.
/// Cropping, black padding and (when available) BGRA → NV12 conversion all happen on the GPU.
/// </summary>
internal sealed class ScreenCapturer : IDisposable
{
    private const DirectXPixelFormat CapturePixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ID3D11Texture2D _canvas;          // BGRA, output size: cropped + padded frame
    private readonly ID3D11RenderTargetView _canvasRtv;
    private readonly Nv12Converter? _converter;
    private readonly ID3D11Texture2D? _staging;        // CPU-readable canvas copy (BGRA fallback only)
    private readonly PixelRect? _crop;
    private readonly object _gpuLock = new();          // guards all D3D work and disposal
    private readonly object _frameLock = new();        // guards swapping _frame / _backFrame
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private byte[] _frame;
    private byte[] _backFrame;
    private SizeInt32 _poolSize;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public FramePixelFormat PixelFormat { get; }
    public int FrameBytes => _frame.Length;

    /// <summary>Why GPU conversion isn't used, when <see cref="PixelFormat"/> is <see cref="FramePixelFormat.Bgra"/>.</summary>
    public string? GpuConversionUnavailableReason { get; }

    /// <summary>Raised (on a worker thread) when the captured window is closed.</summary>
    public event Action? TargetClosed;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public ScreenCapturer(CaptureTarget target, bool captureCursor, bool showBorder, bool useGpuConversion = true)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device!, out _context!)
            .CheckError();
        _winrtDevice = CaptureInterop.CreateDirect3DDevice(_device);

        _item = target.Mode == CaptureMode.Window
            ? CaptureInterop.CreateItemForWindow(target.Handle)
            : CaptureInterop.CreateItemForMonitor(target.Handle);
        _item.Closed += (_, _) => TargetClosed?.Invoke();

        _crop = target.Crop;
        var size = _crop is { } crop ? new SizeInt32 { Width = crop.Width, Height = crop.Height } : _item.Size;
        // 4:2:0 chroma (NV12 / H.264) needs even dimensions.
        Width = Math.Max(2, size.Width & ~1);
        Height = Math.Max(2, size.Height & ~1);

        _canvas = _device.CreateTexture2D(Describe(Format.B8G8R8A8_UNorm, ResourceUsage.Default,
            BindFlags.RenderTarget | BindFlags.ShaderResource, CpuAccessFlags.None));
        _canvasRtv = _device.CreateRenderTargetView(_canvas);
        _context.ClearRenderTargetView(_canvasRtv, new Color4(0, 0, 0, 1));

        string? gpuError = "disabled";
        if (useGpuConversion)
            _converter = Nv12Converter.TryCreate(_device, _context, _canvas, Width, Height, out gpuError);
        GpuConversionUnavailableReason = _converter == null ? gpuError : null;

        PixelFormat = _converter != null ? FramePixelFormat.Nv12 : FramePixelFormat.Bgra;
        if (_converter == null)
            _staging = _device.CreateTexture2D(Describe(Format.B8G8R8A8_UNorm, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read));

        int frameBytes = _converter?.FrameBytes ?? Width * Height * 4;
        _frame = new byte[frameBytes];
        _backFrame = new byte[frameBytes];
        if (PixelFormat == FramePixelFormat.Nv12)
        {
            // Black in limited-range YUV is Y=16, U=V=128 — shown until the first frame arrives.
            _frame.AsSpan(0, Width * Height).Fill(16);
            _frame.AsSpan(Width * Height).Fill(128);
        }

        _poolSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, CapturePixelFormat, 2, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = captureCursor;
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            try { _session.IsBorderRequired = showBorder; }
            catch { /* Not allowed on this OS build; the yellow border stays. */ }
        }
    }

    private Texture2DDescription Describe(Format format, ResourceUsage usage, BindFlags bind, CpuAccessFlags cpu) => new()
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = bind,
        CPUAccessFlags = cpu,
    };

    public void Start() => _session.StartCapture();

    public bool WaitForFirstFrame(TimeSpan timeout) => _firstFrame.Wait(timeout);

    /// <summary>Copies the latest frame (tightly packed, <see cref="PixelFormat"/>) into <paramref name="destination"/>.</summary>
    public void CopyLatestFrame(byte[] destination)
    {
        lock (_frameLock)
        {
            Buffer.BlockCopy(_frame, 0, destination, 0, _frame.Length);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame == null)
            return;

        lock (_gpuLock)
        {
            if (_disposed)
                return;

            var contentSize = frame.ContentSize;
            if (!RenderFrame(frame, contentSize))
                return;

            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                // Window was resized: grow/shrink the pool so the next frames carry the full content.
                _poolSize = contentSize;
                sender.Recreate(_winrtDevice, CapturePixelFormat, 2, contentSize);
            }
        }

        // Publish the finished back buffer; the writer thread only ever waits for this swap.
        lock (_frameLock)
        {
            (_frame, _backFrame) = (_backFrame, _frame);
        }
        _firstFrame.Set();
    }

    /// <summary>Crops/pads the captured texture onto the canvas, converts it and reads it into the back buffer.</summary>
    private bool RenderFrame(Direct3D11CaptureFrame frame, SizeInt32 contentSize)
    {
        using var texture = CaptureInterop.GetTexture(frame.Surface);
        var desc = texture.Description;
        int srcX = _crop?.X ?? 0;
        int srcY = _crop?.Y ?? 0;
        int w = Math.Min(Width, Math.Min(contentSize.Width, (int)desc.Width) - srcX);
        int h = Math.Min(Height, Math.Min(contentSize.Height, (int)desc.Height) - srcY);
        if (w <= 0 || h <= 0)
            return false;

        if (w < Width || h < Height)
            _context.ClearRenderTargetView(_canvasRtv, new Color4(0, 0, 0, 1)); // black padding
        _context.CopySubresourceRegion(_canvas, 0, 0, 0, 0, texture, 0, new Box(srcX, srcY, 0, srcX + w, srcY + h, 1));

        if (_converter != null)
        {
            _converter.ConvertInto(_backFrame);
            return true;
        }

        _context.CopyResource(_staging!, _canvas);
        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            CopyRows(mapped.DataPointer, (int)mapped.RowPitch, Width * 4, Height);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        return true;
    }

    private void CopyRows(IntPtr source, int sourcePitch, int rowBytes, int rows)
    {
        if (sourcePitch == rowBytes)
        {
            Marshal.Copy(source, _backFrame, 0, rowBytes * rows);
            return;
        }
        for (int y = 0; y < rows; y++)
            Marshal.Copy(source + (nint)y * sourcePitch, _backFrame, y * rowBytes, rowBytes);
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _framePool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _framePool.Dispose();
        _converter?.Dispose();
        _staging?.Dispose();
        _canvasRtv.Dispose();
        _canvas.Dispose();
        _winrtDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
        _firstFrame.Dispose();
    }
}
