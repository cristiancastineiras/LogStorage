using System.Runtime.CompilerServices;

namespace LogStorage.Buffers;

/// <summary>
/// Codificación LEB128 (varint) sin asignaciones.
/// Los enteros pequeños ocupan 1 byte, que es el caso habitual para los ids del
/// diccionario y para los deltas de timestamp.
/// </summary>
internal static class Varint
{
    /// <summary>Bytes máximos que puede ocupar un <see cref="ulong"/> codificado.</summary>
    public const int MaxSize = 10;

    /// <summary>Escribe <paramref name="value"/> en <paramref name="dest"/> y devuelve los bytes usados.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> dest, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            dest[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dest[i++] = (byte)value;
        return i;
    }

    /// <summary>Lee un varint desde <paramref name="src"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read(ReadOnlySpan<byte> src, out int bytesRead)
    {
        ulong result = 0;
        var shift = 0;
        var i = 0;

        while (true)
        {
            if (i >= src.Length) ThrowTruncated();
            var b = src[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) ThrowMalformed();
        }

        bytesRead = i;
        return result;
    }

    /// <summary>Lee un varint y avanza <paramref name="src"/> más allá de los bytes consumidos.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadAndAdvance(ref ReadOnlySpan<byte> src)
    {
        var value = Read(src, out var read);
        src = src[read..];
        return value;
    }

    /// <summary>Número de bytes que ocuparía <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeOf(ulong value)
    {
        var size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }
        return size;
    }

    /// <summary>Mapea un entero con signo a uno sin signo manteniendo pequeños los valores cercanos a cero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>Inversa de <see cref="ZigZag"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    private static void ThrowTruncated() => throw new InvalidDataException("Varint truncado: el búfer termina antes de tiempo.");

    private static void ThrowMalformed() => throw new InvalidDataException("Varint mal formado: excede 64 bits.");
}
