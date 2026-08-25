namespace LogStorage.Tests;

/// <summary>Carpeta temporal única que se borra sola al terminar el test.</summary>
public sealed class TempStore : IDisposable
{
    public TempStore(string? name = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "logstorage-tests", $"{name ?? "t"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>Carpeta raíz del almacén de pruebas.</summary>
    public string Root { get; }

    /// <summary>Opciones apuntando a esta carpeta, con bloques pequeños para que los tests roten rápido.</summary>
    public LogStoreOptions Options(Action<LogStoreOptions>? configure = null)
    {
        var options = new LogStoreOptions
        {
            RootPath = Root,
            BlockSizeBytes = 8 * 1024,
            MaxSegmentBytes = 256 * 1024,
            AutoFlushInterval = TimeSpan.Zero,
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>Abre un almacén de escritura sobre esta carpeta.</summary>
    public LogStore Open(Action<LogStoreOptions>? configure = null) => LogStore.Open(Options(configure));

    /// <summary>Ficheros de segmento que hay ahora mismo, ordenados.</summary>
    public string[] Segments()
    {
        var files = Directory.Exists(Root)
            ? Directory.GetFiles(Root, "*.lsz", SearchOption.AllDirectories)
            : [];
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>Bytes que ocupa el almacén en disco.</summary>
    public long TotalBytes()
    {
        long total = 0;
        foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) total += new FileInfo(file).Length;
        return total;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // En Windows a veces el antivirus tiene el fichero un instante más. No es fallo del test.
        }
    }
}
