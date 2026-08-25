using System.Buffers;
using System.Text;
using LogStorage.Buffers;

namespace LogStorage.Format;

/// <summary>
/// Decodifica el payload de un bloque. Está pensado para reutilizarse entre bloques
/// (los arrays se alquilan una vez y se van reciclando).
/// </summary>
/// <remarks>
/// La decodificación va en dos pasos a propósito:
/// <list type="number">
/// <item><see cref="ParseHeader"/> lee solo el diccionario de cadenas (barato).</item>
/// <item>El motor de consultas mira el diccionario y, si el bloque no puede contener nada
/// que interese, lo descarta sin decodificar ni una sola columna.</item>
/// <item><see cref="ParseColumns"/> decodifica el resto solo si merece la pena.</item>
/// </list>
/// </remarks>
internal sealed class BlockDecoder : IDisposable
{
    private string?[] _strings = new string?[64];
    private long[] _timestamps = [];
    private byte[] _levels = [];
    private int[] _services = [];
    private int[] _hosts = [];
    private int[] _messages = [];
    private int[] _traces = [];
    private int[] _payloadOffsets = [];
    private int[] _payloadLengths = [];
    private byte[] _payloadData = [];

    private int _stringCount;
    private int _recordCount;
    private byte _flags;
    private int _columnsOffset;

    /// <summary>Registros del bloque.</summary>
    public int RecordCount => _recordCount;

    /// <summary>Entradas del diccionario, contando el hueco 0 que representa el valor nulo.</summary>
    public int StringCount => _stringCount;

    public bool HasTrace => (_flags & SegmentFormat.BlockFlagHasTrace) != 0;

    public bool HasPayload => (_flags & SegmentFormat.BlockFlagHasPayload) != 0;

    public string?[] Strings => _strings;

    public long[] Timestamps => _timestamps;

    public byte[] Levels => _levels;

    public int[] Services => _services;

    public int[] Hosts => _hosts;

    public int[] Messages => _messages;

    public int[] Traces => _traces;

    /// <summary>Lee cabecera y diccionario. No toca las columnas de datos.</summary>
    public void ParseHeader(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) throw new InvalidDataException("Bloque vacío.");

        var cursor = raw;
        _flags = cursor[0];
        cursor = cursor[1..];

        var dictionaryCount = checked((int)Varint.ReadAndAdvance(ref cursor));
        _stringCount = dictionaryCount + 1;
        EnsureStrings(_stringCount);
        _strings[0] = null;

        for (var i = 1; i <= dictionaryCount; i++)
        {
            var length = checked((int)Varint.ReadAndAdvance(ref cursor));
            _strings[i] = length == 0 ? string.Empty : Encoding.UTF8.GetString(cursor[..length]);
            cursor = cursor[length..];
        }

        _recordCount = checked((int)Varint.ReadAndAdvance(ref cursor));
        _columnsOffset = raw.Length - cursor.Length;
    }

    /// <summary>Decodifica todas las columnas. Hay que pasarle el mismo buffer que a <see cref="ParseHeader"/>.</summary>
    public void ParseColumns(ReadOnlySpan<byte> raw)
    {
        var count = _recordCount;
        EnsureColumns(count);

        var cursor = raw[_columnsOffset..];

        ReadTimestamps(NextColumn(ref cursor), _timestamps, count);
        NextColumn(ref cursor)[..count].CopyTo(_levels);
        ReadIds(NextColumn(ref cursor), _services, count);
        ReadIds(NextColumn(ref cursor), _hosts, count);
        ReadIds(NextColumn(ref cursor), _messages, count);

        if (HasTrace) ReadIds(NextColumn(ref cursor), _traces, count);
        else Array.Clear(_traces, 0, count);

        if (HasPayload)
        {
            ReadIds(NextColumn(ref cursor), _payloadLengths, count);
            var data = NextColumn(ref cursor);
            if (data.Length > _payloadData.Length) Resize(ref _payloadData, data.Length);
            data.CopyTo(_payloadData);

            var offset = 0;
            for (var i = 0; i < count; i++)
            {
                _payloadOffsets[i] = offset;
                offset += _payloadLengths[i];
            }
        }
        else
        {
            Array.Clear(_payloadLengths, 0, count);
        }
    }

    /// <summary>Datos adicionales del registro indicado. Devuelve una copia, así que es seguro conservarlos.</summary>
    public ReadOnlyMemory<byte> GetPayload(int index)
    {
        var length = _payloadLengths[index];
        if (length == 0) return default;
        return _payloadData.AsSpan(_payloadOffsets[index], length).ToArray();
    }

    /// <summary>Busca una cadena exacta en el diccionario. Devuelve -1 si no está en este bloque.</summary>
    public int FindExact(string value)
    {
        for (var i = 1; i < _stringCount; i++)
        {
            if (string.Equals(_strings[i], value, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Indica si alguna cadena del bloque contiene el texto buscado.</summary>
    public bool AnyContains(string needle, StringComparison comparison)
    {
        for (var i = 1; i < _stringCount; i++)
        {
            if (_strings[i] is { } value && value.Contains(needle, comparison)) return true;
        }
        return false;
    }

    /// <summary>
    /// Marca qué entradas del diccionario contienen el texto buscado.
    /// Se calcula una vez por bloque en lugar de una vez por registro: con un diccionario de
    /// 5.000 cadenas y 2 millones de registros son 5.000 comparaciones en vez de 2.000.000.
    /// </summary>
    public bool[] RentContainsMask(string needle, StringComparison comparison, out bool anyMatch)
    {
        var mask = ArrayPool<bool>.Shared.Rent(_stringCount);
        anyMatch = false;
        mask[0] = false;
        for (var i = 1; i < _stringCount; i++)
        {
            var match = _strings[i] is { } value && value.Contains(needle, comparison);
            mask[i] = match;
            anyMatch |= match;
        }
        return mask;
    }

    public static void ReturnMask(bool[] mask) => ArrayPool<bool>.Shared.Return(mask);

    private static ReadOnlySpan<byte> NextColumn(ref ReadOnlySpan<byte> cursor)
    {
        var length = checked((int)Varint.ReadAndAdvance(ref cursor));
        if (length > cursor.Length) throw new InvalidDataException("Columna truncada dentro del bloque.");
        var column = cursor[..length];
        cursor = cursor[length..];
        return column;
    }

    private static void ReadTimestamps(ReadOnlySpan<byte> column, long[] destination, int count)
    {
        var position = 0;
        long previous = 0;
        for (var i = 0; i < count; i++)
        {
            ulong raw = 0;
            var shift = 0;
            byte current;
            do
            {
                current = column[position++];
                raw |= (ulong)(current & 0x7F) << shift;
                shift += 7;
            }
            while ((current & 0x80) != 0);

            previous += Varint.UnZigZag(raw);
            destination[i] = previous;
        }
    }

    private static void ReadIds(ReadOnlySpan<byte> column, int[] destination, int count)
    {
        var position = 0;
        for (var i = 0; i < count; i++)
        {
            uint value = 0;
            var shift = 0;
            byte current;
            do
            {
                current = column[position++];
                value |= (uint)(current & 0x7F) << shift;
                shift += 7;
            }
            while ((current & 0x80) != 0);

            destination[i] = (int)value;
        }
    }

    private void EnsureStrings(int size)
    {
        if (_strings.Length >= size) return;
        _strings = new string?[Math.Max(size, _strings.Length * 2)];
    }

    private void EnsureColumns(int count)
    {
        if (_timestamps.Length >= count) return;

        var size = Math.Max(count, 1024);
        Return(ref _timestamps);
        Return(ref _levels);
        Return(ref _services);
        Return(ref _hosts);
        Return(ref _messages);
        Return(ref _traces);
        Return(ref _payloadOffsets);
        Return(ref _payloadLengths);

        _timestamps = ArrayPool<long>.Shared.Rent(size);
        _levels = ArrayPool<byte>.Shared.Rent(size);
        _services = ArrayPool<int>.Shared.Rent(size);
        _hosts = ArrayPool<int>.Shared.Rent(size);
        _messages = ArrayPool<int>.Shared.Rent(size);
        _traces = ArrayPool<int>.Shared.Rent(size);
        _payloadOffsets = ArrayPool<int>.Shared.Rent(size);
        _payloadLengths = ArrayPool<int>.Shared.Rent(size);
    }

    private static void Resize(ref byte[] buffer, int size)
    {
        Return(ref buffer);
        buffer = ArrayPool<byte>.Shared.Rent(size);
    }

    private static void Return<T>(ref T[] buffer)
    {
        if (buffer.Length == 0) return;
        ArrayPool<T>.Shared.Return(buffer);
        buffer = [];
    }

    public void Dispose()
    {
        Return(ref _timestamps);
        Return(ref _levels);
        Return(ref _services);
        Return(ref _hosts);
        Return(ref _messages);
        Return(ref _traces);
        Return(ref _payloadOffsets);
        Return(ref _payloadLengths);
        Return(ref _payloadData);
        _strings = [];
        _stringCount = 0;
        _recordCount = 0;
    }
}
