namespace LogStorage.Worker;

/// <summary>
/// <see cref="IProgress{T}"/> que invoca en el mismo hilo que informa, para que las líneas
/// de log salgan en orden en vez de despacharse al pool.
/// </summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
