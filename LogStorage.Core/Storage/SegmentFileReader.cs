using System.IO.MemoryMappedFiles;
using LogStorage.Format;

namespace LogStorage.Storage;

/// <summary>
/// Lee un segmento mapeándolo en memoria. Los bytes comprimidos se leen directamente
/// desde el mapeo (cero copias) y el sistema operativo se encarga de la caché de páginas.
/// </summary>
/// <remarks>
/// El catálogo de bloques se reconstruye recorriendo las cabeceras de bloque, encadenadas
/// por longitud. Es una pasada de unos cientos de saltos y evita tener que mantener un footer:
/// un segmento cortado por un apagón se lee igual, hasta el último bloque íntegro.
/// </remarks>
internal sealed unsafe class SegmentFileReader : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _pointer;
    private readonly int _length;

    private SegmentFileReader(string path, MemoryMappedFile map, MemoryMappedViewAccessor view, int length)
    {
        Path = path;
        _map = map;
        _view = view;
        _length = length;

        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointer = pointer + _view.PointerOffset;

        Header = SegmentHeader.ReadFrom(Span);
        Blocks = ScanBlocks();
    }

    public string Path { get; }

    public SegmentHeader Header { get; }

    /// <summary>Bloques íntegros encontrados en el fichero, en orden de escritura.</summary>
    public List<BlockDescriptor> Blocks { get; }

    /// <summary>Todo el fichero como span de solo lectura.</summary>
    public ReadOnlySpan<byte> Span => new(_pointer, _length);

    /// <summary>Bytes comprimidos de un bloque, sin copiar.</summary>
    public ReadOnlySpan<byte> BlockPayload(in BlockDescriptor block)
        => new(_pointer + block.PayloadOffset, block.CompressedLength);

    /// <summary>Bytes que ocupa el fichero.</summary>
    public int Length => _length;

    /// <summary>Instante mínimo real, calculado sobre los bloques presentes.</summary>
    public long MinTicks
    {
        get
        {
            var min = long.MaxValue;
            foreach (var block in Blocks)
            {
                if (block.MinTicks < min) min = block.MinTicks;
            }
            return min;
        }
    }

    /// <summary>Instante máximo real, calculado sobre los bloques presentes.</summary>
    public long MaxTicks
    {
        get
        {
            var max = long.MinValue;
            foreach (var block in Blocks)
            {
                if (block.MaxTicks > max) max = block.MaxTicks;
            }
            return max;
        }
    }

    /// <summary>Registros realmente legibles en el fichero.</summary>
    public long RecordCount
    {
        get
        {
            long total = 0;
            foreach (var block in Blocks) total += block.RecordCount;
            return total;
        }
    }

    /// <summary>Abre un segmento. Devuelve null si el fichero no existe o es demasiado corto para ser válido.</summary>
    public static SegmentFileReader? TryOpen(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < SegmentFormat.HeaderSize) return null;
        if (info.Length > int.MaxValue) throw new InvalidDataException($"Segmento demasiado grande para mapear: {path}");

        FileStream? stream = null;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new SegmentFileReader(path, map, view, (int)info.Length);
        }
        catch
        {
            view?.Dispose();
            map?.Dispose();
            stream?.Dispose();
            throw;
        }
    }

    private List<BlockDescriptor> ScanBlocks()
    {
        var blocks = new List<BlockDescriptor>(Math.Max(4, Header.BlockCount));
        var offset = (long)SegmentFormat.HeaderSize;
        var span = Span;

        while (offset + SegmentFormat.BlockHeaderSize <= _length)
        {
            if (!BlockDescriptor.TryReadHeader(span[(int)offset..], offset, out var block)) break;
            if (block.PayloadOffset + block.CompressedLength > _length) break; // cola truncada: paramos aquí

            blocks.Add(block);
            offset += block.TotalLength;
        }

        return blocks;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }
}
