using System.Diagnostics;
using System.Text.Json;
using LogStorage.Buffers;
using MelEventId = Microsoft.Extensions.Logging.EventId;
using MelLogger = Microsoft.Extensions.Logging.ILogger;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;
using MelProvider = Microsoft.Extensions.Logging.ILoggerProvider;

namespace LogStorage.Logging;

/// <summary>
/// Proveedor de <c>ILogger</c> que manda todo lo que se loguea a un <see cref="ILogStore"/>.
/// </summary>
/// <remarks>
/// Es la vía cómoda: sigues usando <c>ILogger</c> de siempre y los logs acaban comprimidos
/// en disco. Si necesitas el máximo rendimiento, llama a <c>store.Write(...)</c> directamente:
/// te ahorras el formateo del mensaje que impone la abstracción de <c>ILogger</c>.
/// </remarks>
[Microsoft.Extensions.Logging.ProviderAlias("LogStorage")]
public sealed class LogStorageLoggerProvider : MelProvider
{
    private readonly ILogStore _store;
    private readonly LogStorageLoggerOptions _options;
    private readonly bool _ownsStore;

    /// <param name="store">Almacén destino.</param>
    /// <param name="options">Ajustes del proveedor. Si es nulo se usan los valores por defecto.</param>
    /// <param name="ownsStore">Si es cierto, al liberar el proveedor se libera también el almacén.</param>
    public LogStorageLoggerProvider(ILogStore store, LogStorageLoggerOptions? options = null, bool ownsStore = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _options = options ?? new LogStorageLoggerOptions();
        _ownsStore = ownsStore;
    }

    public MelLogger CreateLogger(string categoryName) => new Logger(_store, _options, categoryName);

    public void Dispose()
    {
        if (!_ownsStore) return;
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class Logger(ILogStore store, LogStorageLoggerOptions options, string category) : MelLogger
    {
        private static readonly IDisposable NoScope = new NullScope();

        // Un búfer y un escritor JSON por hilo: en régimen estacionario el payload no asigna nada.
        [ThreadStatic] private static PooledBufferWriter? _buffer;
        [ThreadStatic] private static Utf8JsonWriter? _json;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoScope;

        public bool IsEnabled(MelLogLevel logLevel)
            => logLevel != MelLogLevel.None && (byte)logLevel >= (byte)options.MinimumLevel;

        public void Log<TState>(MelLogLevel logLevel, MelEventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var parameters = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var message = ResolveMessage(state, exception, formatter, parameters);
            var traceId = options.CaptureActivity ? Activity.Current?.Id : null;
            var level = (LogLevel)(byte)logLevel;

            if (!options.IncludeParameters)
            {
                store.Write(level, message, options.Service, traceId: traceId);
                return;
            }

            store.Write(level, message, options.Service, traceId: traceId, payload: BuildPayload(eventId, parameters, exception));
        }

        private string ResolveMessage<TState>(
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter,
            IReadOnlyList<KeyValuePair<string, object?>>? parameters)
        {
            if (options.UseMessageTemplate && parameters is not null)
            {
                // El último elemento del estado de ILogger es siempre la plantilla original.
                for (var i = parameters.Count - 1; i >= 0; i--)
                {
                    if (parameters[i].Key == "{OriginalFormat}" && parameters[i].Value is string template) return template;
                }
            }

            return formatter(state, exception);
        }

        /// <summary>Serializa categoría, evento, parámetros con nombre y excepción como JSON UTF-8.</summary>
        private ReadOnlySpan<byte> BuildPayload(MelEventId eventId, IReadOnlyList<KeyValuePair<string, object?>>? parameters, Exception? exception)
        {
            var buffer = _buffer ??= new PooledBufferWriter(1024);
            buffer.Reset();

            var json = _json;
            if (json is null) _json = json = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });
            else json.Reset(buffer);

            json.WriteStartObject();

            if (options.IncludeCategory) json.WriteString("category", category);
            if (eventId.Id != 0) json.WriteNumber("eventId", eventId.Id);

            if (parameters is not null)
            {
                foreach (var parameter in parameters)
                {
                    if (parameter.Key == "{OriginalFormat}") continue;
                    WriteValue(json, parameter.Key, parameter.Value);
                }
            }

            if (exception is not null)
            {
                json.WriteString("exception", exception.GetType().FullName);
                json.WriteString("exceptionMessage", exception.Message);
                if (exception.StackTrace is { } stack) json.WriteString("stackTrace", stack);
            }

            json.WriteEndObject();
            json.Flush();

            return buffer.WrittenSpan;
        }

        private static void WriteValue(Utf8JsonWriter json, string name, object? value)
        {
            switch (value)
            {
                case null: json.WriteNull(name); break;
                case string text: json.WriteString(name, text); break;
                case bool flag: json.WriteBoolean(name, flag); break;
                case int number: json.WriteNumber(name, number); break;
                case long number: json.WriteNumber(name, number); break;
                case double number: json.WriteNumber(name, number); break;
                case decimal number: json.WriteNumber(name, number); break;
                case DateTime moment: json.WriteString(name, moment); break;
                case DateTimeOffset moment: json.WriteString(name, moment); break;
                case Guid id: json.WriteString(name, id); break;
                default: json.WriteString(name, value.ToString()); break;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
