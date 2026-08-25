using LogStorage.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LogStorage;

/// <summary>Registro en el contenedor de dependencias y en el sistema de logging.</summary>
public static class LogStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registra un <see cref="LogStore"/> como singleton, accesible por <see cref="ILogStore"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddLogStorage(options =>
    /// {
    ///     options.RootPath = "/var/log/miapp";
    ///     options.MaxSegmentBytes = 256L * 1024 * 1024;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddLogStorage(this IServiceCollection services, Action<LogStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LogStoreOptions>>().Value;
            return LogStore.Open(options);
        });
        services.TryAddSingleton<ILogStore>(provider => provider.GetRequiredService<LogStore>());
        services.TryAddSingleton(provider => provider.GetRequiredService<LogStore>().Maintenance);

        return services;
    }

    /// <summary>Registra un almacén apuntando a una carpeta, con el resto de valores por defecto.</summary>
    public static IServiceCollection AddLogStorage(this IServiceCollection services, string rootPath)
        => services.AddLogStorage(options => options.RootPath = rootPath);

    /// <summary>Registra un almacén tomando la configuración de una sección de <c>appsettings.json</c>.</summary>
    public static IServiceCollection AddLogStorage(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return services.AddLogStorage(section.Bind);
    }
}

/// <summary>Enganche del almacén con el sistema de logging de .NET.</summary>
public static class LogStorageLoggingBuilderExtensions
{
    /// <summary>
    /// Manda todo lo que se escriba con <c>ILogger</c> al almacén registrado en el contenedor.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddLogStorage("./logs");
    /// builder.Logging.AddLogStorage(o => o.MinimumLevel = LogLevel.Information);
    /// </code>
    /// </example>
    public static ILoggingBuilder AddLogStorage(this ILoggingBuilder builder, Action<LogStorageLoggerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new LogStorageLoggerOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton<ILoggerProvider>(provider =>
            new LogStorageLoggerProvider(provider.GetRequiredService<ILogStore>(), options));

        return builder;
    }

    /// <summary>
    /// Igual que <see cref="AddLogStorage(ILoggingBuilder, Action{LogStorageLoggerOptions})"/> pero
    /// abriendo el almacén ahí mismo, sin tocar el contenedor. Útil en aplicaciones pequeñas.
    /// </summary>
    public static ILoggingBuilder AddLogStorage(this ILoggingBuilder builder, string rootPath, Action<LogStorageLoggerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new LogStorageLoggerOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton<ILoggerProvider>(_ =>
            new LogStorageLoggerProvider(LogStore.Open(rootPath), options, ownsStore: true));

        return builder;
    }
}
