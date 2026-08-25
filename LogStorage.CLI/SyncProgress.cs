namespace LogStorage.Cli;

/// <summary>
/// <see cref="IProgress{T}"/> que invoca en el mismo hilo que informa.
/// El <see cref="Progress{T}"/> de la BCL despacha al pool cuando no hay contexto de
/// sincronización, y en consola eso desordena las líneas.
/// </summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
