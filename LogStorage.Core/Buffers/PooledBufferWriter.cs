using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LogStorage.Buffers;

/// <summary>
/// <see cref="IBufferWriter{T}"/> respaldado por <see cref="ArrayPool{T}"/>.
/// Reutilizable mediante <see cref="Reset"/>: en régimen estacionario no asigna nada.
/// No es thread-safe; cada instancia pertenece a un único hilo o está protegida por el llamante.
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int WrittenCount => _written;

    public int Capacity => _buffer.Length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written + count > _buffer.Length) throw new InvalidOperationException("Advance supera la capacidad reservada.");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        if (_written >= _buffer.Length) Grow(1);
        _buffer[_written++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarint(ulong value)
    {
        if (_written + Varint.MaxSize > _buffer.Length) Grow(Varint.MaxSize);
        _written += Varint.Write(_buffer.AsSpan(_written), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteZigZag(long value) => WriteVarint(Varint.ZigZag(value));

    public void WriteInt32(int value)
    {
        if (_written + 4 > _buffer.Length) Grow(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_written), value);
        _written += 4;
    }

    public void WriteInt64(long value)
    {
        if (_written + 8 > _buffer.Length) Grow(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_written), value);
        _written += 8;
    }

    public void Write(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;
        if (_written + source.Length > _buffer.Length) Grow(source.Length);
        source.CopyTo(_buffer.AsSpan(_written));
        _written += source.Length;
    }

    /// <summary>Vacía el contenido conservando el búfer alquilado.</summary>
    public void Reset() => _written = 0;

    /// <summary>
    /// Reserva un hueco de <paramref name="size"/> bytes y devuelve su posición,
    /// para rellenarlo después con <see cref="PatchInt32"/>.
    /// </summary>
    public int Reserve(int size)
    {
        if (_written + size > _buffer.Length) Grow(size);
        var offset = _written;
        _buffer.AsSpan(offset, size).Clear();
        _written += size;
        return offset;
    }

    public void PatchInt32(int offset, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset, 4), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint <= 0) sizeHint = 1;
        if (_written + sizeHint > _buffer.Length) Grow(sizeHint);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int needed)
    {
        var target = Math.Max(_buffer.Length * 2, _written + needed);
        var next = ArrayPool<byte>.Shared.Rent(target);
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        if (buffer.Length == 0) return;
        _buffer = [];
        _written = 0;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
