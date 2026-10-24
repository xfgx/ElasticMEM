using System.Runtime.InteropServices;
using ElasticMem.Core.Backend.Vmm;
namespace ElasticMem.Vmm;

// ntdll XPRESS (COMPRESSION_FORMAT_XPRESS | ENGINE_STANDARD): the same codec family the SYS driver uses
// through RtlCompressBuffer in kernel mode, here called from the VMM process. No driver required.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class NtXpressCodec : IPageCodec, IDisposable
{
    const ushort Xpress = 3, EngineStandard = 0;
    [DllImport("ntdll.dll")] static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint workspace, out uint fragment);
    [DllImport("ntdll.dll")] static extern int RtlCompressBuffer(ushort format, byte* source, uint sourceSize, byte* destination, uint destinationSize, uint chunkSize, out uint finalSize, void* workspace);
    [DllImport("ntdll.dll")] static extern int RtlDecompressBuffer(ushort format, byte* destination, uint destinationSize, byte* source, uint sourceSize, out uint finalSize);
    void* workspace; bool disposed;
    public NtXpressCodec()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ntdll XPRESS requires Windows");
        int status = RtlGetCompressionWorkSpaceSize(Xpress | EngineStandard, out uint bytes, out _);
        if (status < 0 || bytes == 0 || bytes > 1024 * 1024) throw new NotSupportedException("RtlGetCompressionWorkSpaceSize failed: 0x" + status.ToString("X8"));
        workspace = NativeMemory.AllocZeroed(bytes);
    }
    public string Name => "ntdll_xpress_standard";
    public bool TryCompress(ReadOnlySpan<byte> page, Span<byte> destination, out int written)
    {
        ObjectDisposedException.ThrowIf(disposed, this); written = 0;
        if (page.Length != 4096 || destination.Length < 4096) throw new ArgumentException("4096-byte page and buffer required");
        fixed (byte* s = page) fixed (byte* d = destination)
        {
            // STATUS_BUFFER_ALL_ZEROS (0x117) is a success code with valid output; STATUS_BUFFER_TOO_SMALL means incompressible.
            int status = RtlCompressBuffer(Xpress | EngineStandard, s, 4096, d, 4096, 4096, out uint size, workspace);
            if (status < 0 || size == 0 || size >= 4096) return false;
            written = (int)size; return true;
        }
    }
    public bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> page, out int written)
    {
        ObjectDisposedException.ThrowIf(disposed, this); written = 0;
        if (page.Length != 4096 || source.Length is < 1 or >= 4096) return false;
        fixed (byte* s = source) fixed (byte* d = page)
        {
            int status = RtlDecompressBuffer(Xpress, d, 4096, s, (uint)source.Length, out uint size);
            if (status < 0) return false;
            written = (int)size; return true;
        }
    }
    public void Dispose() { if (disposed) return; NativeMemory.Free(workspace); workspace = null; disposed = true; }
}
