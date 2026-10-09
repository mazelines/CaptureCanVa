using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CaptureCanva.Capture;

/// <summary>
/// Converts a BGRA texture to NV12 (BT.709, limited range) with a compute shader, so the CPU no
/// longer does color conversion and 62% less data goes to the encoder.
/// </summary>
/// <remarks>
/// A shader is used instead of the D3D11 video processor because the video processor's color
/// conversion (matrix, range, chroma filtering) is up to each GPU driver; the shader gives the same
/// exact BT.709 result on every vendor.
/// </remarks>
internal sealed class Nv12Converter : IDisposable
{
    private const string Shader = """
        Texture2D<float4> Source : register(t0);
        RWTexture2D<unorm float> LumaOut : register(u0);
        RWTexture2D<unorm float2> ChromaOut : register(u1);

        // BT.709 coefficients, full-range R'G'B' in, limited-range Y'CbCr out.
        // (Keep this source ASCII-only: it is passed to the compiler by character count.)
        static const float3 Kr = float3(0.2126, 0.7152, 0.0722);

        float Luma(float3 c) { return (16.0 + 219.0 * dot(c, Kr)) / 255.0; }

        // One thread per 2x2 block: four luma samples and one (averaged) chroma sample.
        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            uint width, height;
            LumaOut.GetDimensions(width, height);
            uint2 p = id.xy * 2;
            if (p.x >= width || p.y >= height)
                return;

            float3 c00 = Source.Load(int3(p, 0)).rgb;
            float3 c10 = Source.Load(int3(p + uint2(1, 0), 0)).rgb;
            float3 c01 = Source.Load(int3(p + uint2(0, 1), 0)).rgb;
            float3 c11 = Source.Load(int3(p + uint2(1, 1), 0)).rgb;

            LumaOut[p] = Luma(c00);
            LumaOut[p + uint2(1, 0)] = Luma(c10);
            LumaOut[p + uint2(0, 1)] = Luma(c01);
            LumaOut[p + uint2(1, 1)] = Luma(c11);

            float3 avg = (c00 + c10 + c01 + c11) * 0.25;
            float y = dot(avg, Kr);
            float cb = (avg.b - y) / 1.8556;
            float cr = (avg.r - y) / 1.5748;
            ChromaOut[id.xy] = float2(128.0 + 224.0 * cb, 128.0 + 224.0 * cr) / 255.0;
        }
        """;

    private static readonly Lazy<byte[]> CompiledShader = new(() =>
        Compiler.Compile(Shader, "main", "Nv12Converter.hlsl", "cs_5_0").ToArray());

    private readonly ID3D11DeviceContext _context;
    private readonly int _width;
    private readonly int _height;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11ShaderResourceView _sourceView;
    private readonly ID3D11Texture2D _luma;      // R8, width × height
    private readonly ID3D11Texture2D _chroma;    // R8G8, width/2 × height/2
    private readonly ID3D11UnorderedAccessView _lumaView;
    private readonly ID3D11UnorderedAccessView _chromaView;
    private readonly ID3D11Texture2D _lumaStaging;
    private readonly ID3D11Texture2D _chromaStaging;

    public int FrameBytes => _width * _height * 3 / 2;

    private Nv12Converter(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height)
    {
        _context = context;
        _width = width;
        _height = height;

        var disposables = new List<IDisposable>();
        try
        {
            _shader = Track(device.CreateComputeShader(CompiledShader.Value));
            _sourceView = Track(device.CreateShaderResourceView(source));
            _luma = Track(device.CreateTexture2D(Describe(Format.R8_UNorm, width, height, ResourceUsage.Default, BindFlags.UnorderedAccess, CpuAccessFlags.None)));
            _chroma = Track(device.CreateTexture2D(Describe(Format.R8G8_UNorm, width / 2, height / 2, ResourceUsage.Default, BindFlags.UnorderedAccess, CpuAccessFlags.None)));
            _lumaView = Track(device.CreateUnorderedAccessView(_luma));
            _chromaView = Track(device.CreateUnorderedAccessView(_chroma));
            _lumaStaging = Track(device.CreateTexture2D(Describe(Format.R8_UNorm, width, height, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read)));
            _chromaStaging = Track(device.CreateTexture2D(Describe(Format.R8G8_UNorm, width / 2, height / 2, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read)));
        }
        catch
        {
            for (int i = disposables.Count - 1; i >= 0; i--)
                disposables[i].Dispose();
            throw;
        }

        T Track<T>(T item) where T : IDisposable
        {
            disposables.Add(item);
            return item;
        }
    }

    private static Texture2DDescription Describe(Format format, int width, int height, ResourceUsage usage, BindFlags bind, CpuAccessFlags cpu) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = bind,
        CPUAccessFlags = cpu,
    };

    /// <summary>Returns a converter for <paramref name="source"/>, or null (with the reason) if the GPU can't do it.</summary>
    public static Nv12Converter? TryCreate(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source,
        int width, int height, out string? error)
    {
        try
        {
            error = null;
            return new Nv12Converter(device, context, source, width, height);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Converts the source texture and copies the result (Y plane, then UV plane) into <paramref name="destination"/>.</summary>
    public void ConvertInto(byte[] destination)
    {
        _context.CSSetShader(_shader);
        _context.CSSetShaderResource(0, _sourceView);
        _context.CSSetUnorderedAccessViews(0, [_lumaView, _chromaView]);
        _context.Dispatch((uint)(_width / 2 + 7) / 8, (uint)(_height / 2 + 7) / 8, 1);
        _context.CSSetUnorderedAccessViews(0, [null!, null!]);
        _context.CSSetShaderResource(0, null!);

        _context.CopyResource(_lumaStaging, _luma);
        _context.CopyResource(_chromaStaging, _chroma);
        ReadPlane(_lumaStaging, destination, 0, _width, _height);
        ReadPlane(_chromaStaging, destination, _width * _height, _width, _height / 2); // R8G8 row = width bytes
    }

    private void ReadPlane(ID3D11Texture2D staging, byte[] destination, int offset, int rowBytes, int rows)
    {
        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int pitch = (int)mapped.RowPitch;
            if (pitch == rowBytes)
            {
                Marshal.Copy(mapped.DataPointer, destination, offset, rowBytes * rows);
                return;
            }
            for (int y = 0; y < rows; y++)
                Marshal.Copy(mapped.DataPointer + (nint)y * pitch, destination, offset + y * rowBytes, rowBytes);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _chromaStaging.Dispose();
        _lumaStaging.Dispose();
        _chromaView.Dispose();
        _lumaView.Dispose();
        _chroma.Dispose();
        _luma.Dispose();
        _sourceView.Dispose();
        _shader.Dispose();
    }
}
