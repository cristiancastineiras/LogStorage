using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using LogStorage.Buffers;
using LogStorage.Compression;
using LogStorage.Format;

namespace LogStorage.Storage;

/// <summary>
/// Camino de ingesta completo: recibe registros, los agrupa en bloques, los comprime
/// en segundo plano y los va escribiendo en segmentos que rota solo.
/// </summary>
/// <remarks>
/// <para>Reparto de trabajo:</para>
/// <list type="bullet">
/// <item><b>Hilo que escribe</b>: codifica el registro dentro de un lock cortísimo (una copia
/// de bytes y unas búsquedas en un diccionario). Cero asignaciones, cero E/S, cero <c>Task</c>.</item>
/// <item><b>Hilo de fondo</b>: comprime y escribe. Es el único que toca el fichero, así que
/// no hay locks de E/S.</item>
/// </list>
/// <para>
/// Cuando el consumidor no da abasto, quien escribe se frena solo (semáforo de bloques
/// pendientes) en lugar de tragar memoria hasta reventar.
/// </para>
/// </remarks>
internal sealed class SegmentWriter : IAsyncDisposable
{
    private readonly LogStoreOptions _options;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<BlockBuilder> _pool = new();
    private readonly Channel<WorkItem> _channel;
    private readonly SemaphoreSlim _backpressure;
    private readonly Task _consumer;
    private readonly Timer? _autoFlushTimer;

    // Solo los toca el hilo consumidor.
    private readonly IBlockCodec _codec;
    private readonly PooledBufferWriter _serialized;
    private SegmentFileWriter? _file;
    private DateOnly _currentDay;

    private BlockBuilder _active;
    private long _activeDayStart = long.MaxValue;
    private long _activeDayEnd = long.MinValue;
    private bool _closed;
    private Exception? _fault;

    private long _recordsWritten;
    private long _blocksWritten;
    private long _bytesWritten;

    /// <summary>Bloques ya sacados del búfer activo pero todavía no encolados.</summary>
    private int _handoffs;

    public SegmentWriter(LogStoreOptions options)
    {
        _options = options;
        _codec = BlockCodecs.Create(options.Codec, options.CompressionLevel);
        _serialized = new PooledBufferWriter(Math.Min(options.BlockSizeBytes, 1 << 20));
        _active = new BlockBuilder();
        _backpressure = new SemaphoreSlim(options.MaxPendingBlocks, options.MaxPendingBlocks);
        _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        _consumer = Task.Run(ConsumeAsync);

        if (options.AutoFlushInterval > TimeSpan.Zero)
        {
            _autoFlushTimer = new Timer(static state => ((SegmentWriter)state!).OnAutoFlush(), this, options.AutoFlushInterval, options.AutoFlushInterval);
        }
    }

    /// <summary>Registros ya escritos en disco.</summary>
    public long RecordsWritten => Interlocked.Read(ref _recordsWritten);

    /// <summary>Bloques comprimidos y escritos.</summary>
    public long BlocksWritten => Interlocked.Read(ref _blocksWritten);

    /// <summary>Bytes escritos en disco (ya comprimidos).</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Añade un registro. Es el camino caliente: no asigna ni hace E/S.</summary>
    public void Append(in LogEntry entry)
    {
        ThrowIfFaulted();

        BlockBuilder? full = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, typeof(SegmentWriter));

            // Invariante: un bloque nunca cruza la medianoche UTC. Así cada carpeta de día
            // contiene exactamente los registros de ese día y archivar un día a Parquet no
            // puede arrastrarse (ni perder) registros del día siguiente.
            var ticks = entry.TimestampTicks;
            if (ticks < _activeDayStart || ticks >= _activeDayEnd)
            {
                if (!_active.IsEmpty)
                {
                    full = _active;
                    _active = RentBuilder();
                }
                SetActiveDay(ticks);
            }

            _active.Append(entry);

            if (full is null && _active.EstimatedSize >= _options.BlockSizeBytes)
            {
                full = _active;
                _active = RentBuilder();
            }

            if (full is not null) Interlocked.Increment(ref _handoffs);
        }

        if (full is null) return;

        try
        {
            // El hueco se reserva ANTES de encolar: así el consumidor nunca puede devolver
            // más permisos de los que se han pedido, y quien escribe se frena solo cuando
            // la compresión no da abasto.
            _backpressure.Wait();
            _channel.Writer.TryWrite(WorkItem.ForBlock(full, throttled: true));
        }
        finally
        {
            Interlocked.Decrement(ref _handoffs);
        }
    }

    /// <summary>Espera a que no queden bloques a medio traspasar entre el búfer activo y la cola.</summary>
    private void WaitForHandoffs()
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref _handoffs) > 0) spin.SpinOnce();
    }

    private void SetActiveDay(long ticks)
    {
        _activeDayStart = ticks - ticks % TimeSpan.TicksPerDay;
        _activeDayEnd = _activeDayStart + TimeSpan.TicksPerDay;
    }

    /// <summary>Cierra el bloque en curso y espera a que todo lo pendiente esté en disco.</summary>
    public async Task FlushAsync(bool toDisk = false, CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted();
        CloseActiveBlock();
        WaitForHandoffs();

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(WorkItem.ForSignal(signal, toDisk, seal: false))) return;
        await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vuelca lo pendiente y cierra el segmento actual, forzando que el siguiente registro abra uno nuevo.</summary>
    public async Task SealAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted();
        CloseActiveBlock();
        WaitForHandoffs();

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(WorkItem.ForSignal(signal, toDisk: true, seal: true))) return;
        await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnAutoFlush()
    {
        if (Volatile.Read(ref _closed)) return;
        try
        {
            CloseActiveBlock();
        }
        catch
        {
            // El fallo real ya queda registrado en _fault y saldrá en la próxima escritura.
        }
    }

    /// <summary>Manda el bloque en curso a la cola si tiene algo dentro.</summary>
    private void CloseActiveBlock()
    {
        BlockBuilder full;
        lock (_gate)
        {
            if (_closed || _active.IsEmpty) return;
            full = _active;
            _active = RentBuilder();
            Interlocked.Increment(ref _handoffs);
        }

        try
        {
            // Un volcado manual o por temporizador no consume hueco de contrapresión:
            // no es carga sostenida, es puntual.
            _channel.Writer.TryWrite(WorkItem.ForBlock(full, throttled: false));
        }
        finally
        {
            Interlocked.Decrement(ref _handoffs);
        }
    }

    private BlockBuilder RentBuilder() => _pool.TryDequeue(out var builder) ? builder : new BlockBuilder();

    private async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                try
                {
                    if (item.Block is { } block)
                    {
                        try
                        {
                            WriteBlock(block);
                        }
                        finally
                        {
                            block.Reset();
                            _pool.Enqueue(block);
                            if (item.Throttled) _backpressure.Release();
                        }
                    }

                    if (item.Seal) SealCurrentFile();
                    else if (item.FlushToDisk) _file?.Flush(toDisk: true);

                    item.Signal?.TrySetResult();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _fault, ex, null);
                    item.Signal?.TrySetException(ex);
                }
            }
        }
    }

    private void WriteBlock(BlockBuilder block)
    {
        if (block.IsEmpty) return;

        EnsureFile(SegmentPaths.DayOf(block.MinTicks));

        _serialized.Reset();
        block.Serialize(_serialized);
        var raw = _serialized.WrittenSpan;

        var buffer = ArrayPool<byte>.Shared.Rent(_codec.MaxCompressedLength(raw.Length));
        try
        {
            var compressedLength = _codec.Compress(raw, buffer);
            _file!.WriteBlock(buffer.AsSpan(0, compressedLength), raw.Length, block.RecordCount, block.MinTicks, block.MaxTicks);
            Interlocked.Add(ref _bytesWritten, SegmentFormat.BlockHeaderSize + compressedLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Interlocked.Add(ref _recordsWritten, block.RecordCount);
        Interlocked.Increment(ref _blocksWritten);

        var reachedSize = _file!.Length >= _options.MaxSegmentBytes;
        var reachedRecords = _options.MaxSegmentRecords > 0 && _file.RecordCount >= _options.MaxSegmentRecords;
        if (reachedSize || reachedRecords) SealCurrentFile();
    }

    private void EnsureFile(DateOnly day)
    {
        if (_file is not null && _currentDay == day) return;

        SealCurrentFile();

        var directory = SegmentPaths.DayDirectory(_options.RootPath, day);
        var path = SegmentPaths.SegmentFile(directory, SegmentPaths.NextSegmentId(directory));
        _file = new SegmentFileWriter(path, _options.Codec);
        _currentDay = day;
    }

    private void SealCurrentFile()
    {
        if (_file is null) return;
        _file.Seal();
        _file.Dispose();
        _file = null;
    }

    private void ThrowIfFaulted()
    {
        var fault = Volatile.Read(ref _fault);
        if (fault is not null) throw new IOException("El escritor de segmentos falló y dejó de aceptar registros.", fault);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }

        if (_autoFlushTimer is not null) await _autoFlushTimer.DisposeAsync().ConfigureAwait(false);

        // El bloque en curso se manda a la cola aunque _closed ya esté puesto.
        BlockBuilder? pending = null;
        lock (_gate)
        {
            if (!_active.IsEmpty)
            {
                pending = _active;
                _active = new BlockBuilder();
            }
        }
        if (pending is not null) _channel.Writer.TryWrite(WorkItem.ForBlock(pending, throttled: false));

        WaitForHandoffs();
        _channel.Writer.TryWrite(WorkItem.ForSignal(null, toDisk: true, seal: true));
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);

        while (_pool.TryDequeue(out var builder)) builder.Dispose();
        pending?.Dispose();
        _active.Dispose();
        _serialized.Dispose();
        _codec.Dispose();
        _backpressure.Dispose();
    }

    /// <summary>Unidad de trabajo de la cola: un bloque, una señal, o ambas cosas.</summary>
    private readonly struct WorkItem
    {
        public readonly BlockBuilder? Block;
        public readonly TaskCompletionSource? Signal;
        public readonly bool FlushToDisk;
        public readonly bool Seal;

        /// <summary>Indica si este bloque consumió un hueco del semáforo de contrapresión.</summary>
        public readonly bool Throttled;

        private WorkItem(BlockBuilder? block, TaskCompletionSource? signal, bool flushToDisk, bool seal, bool throttled)
        {
            Block = block;
            Signal = signal;
            FlushToDisk = flushToDisk;
            Seal = seal;
            Throttled = throttled;
        }

        public static WorkItem ForBlock(BlockBuilder block, bool throttled) => new(block, null, false, false, throttled);

        public static WorkItem ForSignal(TaskCompletionSource? signal, bool toDisk, bool seal) => new(null, signal, toDisk, seal, false);
    }
}
