using System.Runtime.InteropServices;
using System.Text;
using LogStorage.Buffers;

namespace LogStorage.Format;

/// <summary>
/// Acumula registros en columnas y produce el payload (sin comprimir) de un bloque.
/// </summary>
/// <remarks>
/// <para>
/// El formato es columnar: cada campo va en su propio búfer contiguo. Eso comprime muchísimo
/// mejor que fila a fila (valores parecidos juntos) y permite al lector saltarse las columnas
/// que no necesita.
/// </para>
/// <para>
/// Las cadenas no se guardan repetidas: se meten una sola vez en el diccionario local del
/// bloque y en la columna solo va un id varint. "Usuario creado" un millón de veces = un
/// millón de bytes, no catorce millones.
/// </para>
/// <para>No es thread-safe: el llamante serializa el acceso (lo hace <c>SegmentWriter</c>).</para>
/// </remarks>
internal sealed class BlockBuilder : IDisposable
{
    private readonly Dictionary<string, int> _dictionary = new(StringComparer.Ordinal);
    private readonly PooledBufferWriter _dictionaryData;
    private readonly PooledBufferWriter _timestamps;
    private readonly PooledBufferWriter _levels;
    private readonly PooledBufferWriter _services;
    private readonly PooledBufferWriter _hosts;
    private readonly PooledBufferWriter _messages;
    private readonly PooledBufferWriter _traces;
    private readonly PooledBufferWriter _payloadLengths;
    private readonly PooledBufferWriter _payloadData;

    private int _recordCount;
    private long _minTicks = long.MaxValue;
    private long _maxTicks = long.MinValue;
    private long _previousTicks;
    private bool _anyTrace;
    private bool _anyPayload;

    public BlockBuilder(int initialCapacity = 64 * 1024)
    {
        _dictionaryData = new PooledBufferWriter(initialCapacity / 4);
        _timestamps = new PooledBufferWriter(initialCapacity);
        _levels = new PooledBufferWriter(initialCapacity / 4);
        _services = new PooledBufferWriter(initialCapacity / 4);
        _hosts = new PooledBufferWriter(initialCapacity / 4);
        _messages = new PooledBufferWriter(initialCapacity / 2);
        _traces = new PooledBufferWriter(initialCapacity / 4);
        _payloadLengths = new PooledBufferWriter(initialCapacity / 4);
        _payloadData = new PooledBufferWriter(initialCapacity / 4);
    }

    public int RecordCount => _recordCount;

    public bool IsEmpty => _recordCount == 0;

    public long MinTicks => _minTicks;

    public long MaxTicks => _maxTicks;

    /// <summary>Cadenas distintas vistas en este bloque.</summary>
    public int DictionaryCount => _dictionary.Count;

    /// <summary>Bytes aproximados que ocupará el payload. Se usa para decidir cuándo cerrar el bloque.</summary>
    public int EstimatedSize =>
        _dictionaryData.WrittenCount
        + _timestamps.WrittenCount
        + _levels.WrittenCount
        + _services.WrittenCount
        + _hosts.WrittenCount
        + _messages.WrittenCount
        + _traces.WrittenCount
        + _payloadLengths.WrittenCount
        + _payloadData.WrittenCount;

    public void Append(in LogEntry entry)
        => Append(entry.TimestampTicks, entry.Level, entry.Service, entry.Host, entry.Message, entry.TraceId, entry.Payload.Span);

    /// <summary>Añade un registro. No asigna nada salvo la primera vez que aparece una cadena nueva.</summary>
    public void Append(long timestampTicks, LogLevel level, string service, string host, string message, string? traceId, ReadOnlySpan<byte> payload)
    {
        if (timestampTicks < _minTicks) _minTicks = timestampTicks;
        if (timestampTicks > _maxTicks) _maxTicks = timestampTicks;

        // Los timestamps consecutivos se diferencian en microsegundos: el delta cabe en 1-3 bytes.
        _timestamps.WriteZigZag(timestampTicks - _previousTicks);
        _previousTicks = timestampTicks;

        _levels.WriteByte((byte)level);
        _services.WriteVarint((uint)Intern(service));
        _hosts.WriteVarint((uint)Intern(host));
        _messages.WriteVarint((uint)Intern(message));

        var traceIdentifier = Intern(traceId);
        if (traceIdentifier != 0) _anyTrace = true;
        _traces.WriteVarint((uint)traceIdentifier);

        _payloadLengths.WriteVarint((uint)payload.Length);
        if (!payload.IsEmpty)
        {
            _payloadData.Write(payload);
            _anyPayload = true;
        }

        _recordCount++;
    }

    /// <summary>
    /// Vuelca el bloque en <paramref name="output"/> con la disposición definida en <see cref="SegmentFormat"/>.
    /// </summary>
    public void Serialize(PooledBufferWriter output)
    {
        byte flags = 0;
        if (_anyTrace) flags |= SegmentFormat.BlockFlagHasTrace;
        if (_anyPayload) flags |= SegmentFormat.BlockFlagHasPayload;

        output.WriteByte(flags);

        output.WriteVarint((uint)_dictionary.Count);
        output.Write(_dictionaryData.WrittenSpan);

        output.WriteVarint((uint)_recordCount);

        WriteColumn(output, _timestamps);
        WriteColumn(output, _levels);
        WriteColumn(output, _services);
        WriteColumn(output, _hosts);
        WriteColumn(output, _messages);
        if (_anyTrace) WriteColumn(output, _traces);
        if (_anyPayload)
        {
            WriteColumn(output, _payloadLengths);
            WriteColumn(output, _payloadData);
        }
    }

    /// <summary>Deja el bloque vacío pero conserva los búferes alquilados: en régimen estacionario no se asigna nada.</summary>
    public void Reset()
    {
        _dictionary.Clear();
        _dictionaryData.Reset();
        _timestamps.Reset();
        _levels.Reset();
        _services.Reset();
        _hosts.Reset();
        _messages.Reset();
        _traces.Reset();
        _payloadLengths.Reset();
        _payloadData.Reset();

        _recordCount = 0;
        _minTicks = long.MaxValue;
        _maxTicks = long.MinValue;
        _previousTicks = 0;
        _anyTrace = false;
        _anyPayload = false;
    }

    private static void WriteColumn(PooledBufferWriter output, PooledBufferWriter column)
    {
        output.WriteVarint((uint)column.WrittenCount);
        output.Write(column.WrittenSpan);
    }

    /// <summary>Devuelve el id de la cadena en el diccionario del bloque. 0 significa <c>null</c>.</summary>
    private int Intern(string? value)
    {
        if (value is null) return 0;

        ref var id = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, value, out var existed);
        if (existed) return id;

        var byteCount = Encoding.UTF8.GetByteCount(value);
        _dictionaryData.WriteVarint((uint)byteCount);
        var destination = _dictionaryData.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, destination);
        _dictionaryData.Advance(byteCount);

        id = _dictionary.Count; // ya incluye la entrada recién añadida => el primer id es 1
        return id;
    }

    public void Dispose()
    {
        _dictionaryData.Dispose();
        _timestamps.Dispose();
        _levels.Dispose();
        _services.Dispose();
        _hosts.Dispose();
        _messages.Dispose();
        _traces.Dispose();
        _payloadLengths.Dispose();
        _payloadData.Dispose();
    }
}
