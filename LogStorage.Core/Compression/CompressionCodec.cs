namespace LogStorage.Compression;

/// <summary>Identificador del algoritmo con el que se comprimió un bloque. Se guarda en la cabecera del segmento.</summary>
public enum CompressionCodec : byte
{
    /// <summary>Sin comprimir. Útil en tests y en discos ya comprimidos.</summary>
    None = 0,

    /// <summary>Zstandard. Relación compresión/velocidad muy difícil de batir para logs.</summary>
    Zstd = 1,
}
