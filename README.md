![logo](./logo.svg)

<p align="center">
  <strong>Guarda años de logs en carpetas y ficheros. Sin base de datos, sin Elastic, sin servidor.</strong>
</p>

<p align="center">
  <code>.NET 10</code> · <code>~1,3 bytes por registro en disco</code> · <code>2.000.000 registros/s de ingesta</code>
</p>

---

## Qué es

Una librería de .NET para escribir y consultar volúmenes muy grandes de logs usando
únicamente el sistema de ficheros. Escribes con una llamada síncrona sin asignaciones, y
buscas por rango de fechas, nivel, servicio o texto en milisegundos, aunque tengas años de
historia.

```
tu aplicación
     ↓  store.Write(...)          ← síncrono, sin asignar, sin E/S
  búfer columnar en memoria
     ↓  (hilo de fondo)
     zstd
     ↓
logs/2026/08/20/segment-00001.lsz   ← formato caliente, consultable
     ↓  (worker, días ya cerrados)
logs/archive/2026/08/logs-2026-08-20.parquet   ← formato de archivo, universal
```

### Por qué ocupa tan poco

Un log típico en JSON ocupa 150-200 bytes. Aquí ocupa **1,3**. No hay magia, hay tres ideas
apiladas:

| Idea | Qué hace |
|---|---|
| **Columnar** | Cada campo va en su propio búfer contiguo. Un millón de niveles seguidos comprime infinitamente mejor que un millón de registros mezclados. |
| **Diccionario por bloque** | `"Usuario creado"` aparece cien millones de veces → se guarda **una vez** por bloque; en cada registro va un entero de 1 byte. |
| **Deltas + zstd** | Los timestamps se guardan como diferencias (1-2 bytes en vez de 8) y el bloque entero pasa por Zstandard. |

### Por qué encuentra rápido

El motor descarta trabajo en cuatro niveles, del más barato al más caro:

1. **Carpeta** — el árbol `yyyy/MM/dd` ya excluye los días fuera de rango. Buscar en un día de hace tres años no toca los otros 1.824.
2. **Bloque** — cada bloque lleva su min/max de tiempo en la cabecera. Si no toca el rango, no se descomprime.
3. **Diccionario** — se descomprime el bloque y se mira su diccionario de cadenas. Si el servicio buscado no está, o ninguna cadena contiene el texto, el bloque se abandona **sin decodificar una sola columna**.
4. **Registro** — solo entonces se comparan campos, y con enteros (ids del diccionario), no con cadenas.

---

## Empezar

```bash
dotnet add package LogStorage.Core   # o referencia el proyecto directamente
```

```csharp
using LogStorage;

await using var store = LogStore.Open("./logs");

store.Information("Pedido creado", service: "ventas");
store.Error("Pago rechazado", service: "pagos", traceId: Activity.Current?.Id);

await foreach (var record in store.QueryAsync(new LogQuery
{
    From = DateTimeOffset.UtcNow.AddDays(-1),
    MinLevel = LogLevel.Warning,
    Contains = "pago",
}))
{
    Console.WriteLine(record);
}
```

Eso es todo. La carpeta se crea sola, los segmentos rotan solos y al cerrar el almacén todo
queda en disco.

---

## El SDK

### Abrir

```csharp
LogStore.Open("./logs");                     // valores por defecto, listo para producción
LogStore.OpenReadOnly("./logs");             // solo consultas; no crea nada ni bloquea nada
LogStore.Open("./logs", o => o.CompressionLevel = 9);
LogStore.Open(new LogStoreOptions { ... });
```

| Opción | Por defecto | Para qué |
|---|---|---|
| `RootPath` | `logs` | Carpeta raíz. |
| `BlockSizeBytes` | 4 MiB | Unidad de compresión y de lectura aleatoria. Más grande comprime mejor; más pequeño afina la búsqueda. |
| `MaxSegmentBytes` | 128 MiB | Tamaño en disco (ya comprimido) al que rota el segmento. |
| `MaxSegmentRecords` | 0 (sin límite) | Alternativa a rotar por tamaño. |
| `Codec` | `Zstd` | `Zstd` o `None`. |
| `CompressionLevel` | 3 | 1 rápido … 22 máximo. 3 es el punto dulce. |
| `MaxPendingBlocks` | 4 | Bloques en vuelo antes de frenar a quien escribe. |
| `AutoFlushInterval` | 5 s | Volcado periódico del bloque en curso. `Zero` lo desactiva. |
| `DefaultService` / `DefaultHost` | `app` / nombre de máquina | Etiquetas por defecto. |
| `ReadOnly` | `false` | Abre sin escritor. |

### Escribir

```csharp
// Atajos
store.Trace / Debug / Information / Warning / Error / Critical(mensaje, service, traceId);

// Control completo
store.Write(LogLevel.Warning, "Reintento", service: "pagos", host: "web-01",
            traceId: activity.Id, payload: utf8Json);

// Con marca de tiempo explícita (importaciones)
store.WriteAt(cuando, LogLevel.Error, "Falló el batch", "batch");

// La vía más rápida: cero asignaciones, ni una
store.Write(new LogEntry(ticksUtc, LogLevel.Information, plantilla, servicio, host));
```

`Write` es **síncrono, thread-safe y no hace E/S**: codifica el registro en un búfer y
vuelve. La compresión y la escritura ocurren en un hilo de fondo. Si ese hilo no da abasto,
quien escribe se frena solo en lugar de tragar memoria.

### Consultar

```csharp
var query = new LogQuery
{
    From = DateTimeOffset.Parse("2023-05-01"),
    To   = DateTimeOffset.Parse("2023-05-02"),   // exclusivo
    MinLevel = LogLevel.Warning,
    Level = LogLevel.Error,          // nivel exacto (alternativa a MinLevel)
    Service = "ventas",
    Host = "web-01",
    TraceId = "00-0af7...-01",
    Contains = "pedido",
    CaseSensitive = false,
    Limit = 100,
    Descending = true,               // del más reciente al más antiguo
    IncludeArchive = true,           // mirar también los Parquet
    IncludeSegments = true,
};

await foreach (var record in store.QueryAsync(query)) { }
long cuantos = await store.CountAsync(query);
```

Atajos: `LogQuery.LastHours(24)`, `LogQuery.OnDay(new DateOnly(2023, 5, 1))`,
`LogQuery.Between(desde, hasta)`.

Los resultados llegan **en streaming**: puedes recorrer mil millones de registros sin que la
memoria suba.

### Mantenimiento

```csharp
var m = store.Maintenance;                            // o new LogStoreMaintenance(options)

await m.ArchiveDayAsync(new DateOnly(2026, 8, 20));    // día → Parquet, borra segmentos
await m.ArchiveOlderThanAsync(hoy.AddDays(-2));        // todos los días cerrados
m.PruneSegmentsOlderThan(hoy.AddDays(-30));            // retención de segmentos
m.PruneArchiveOlderThan(hoy.AddYears(-5));             // retención del archivo
await m.ReindexAsync();                                // reconstruye manifest.idx
var report = m.Verify();                               // integridad (xxHash3 de cada bloque)
```

### Inyección de dependencias

```csharp
builder.Services.AddLogStorage(o => o.RootPath = "/var/log/miapp");
// o desde appsettings.json:
builder.Services.AddLogStorage(builder.Configuration.GetSection("LogStorage"));
```

Registra `ILogStore`, `LogStore` y `LogStoreMaintenance` como singletons.

### Como proveedor de `ILogger`

```csharp
builder.Services.AddLogStorage("./logs");
builder.Logging.AddLogStorage(o => o.MinimumLevel = LogLevel.Information);
```

```csharp
logger.LogInformation("Pedido {PedidoId} creado por {Usuario}", 42, "ana");
```

Por defecto guarda **la plantilla**, no el texto ya formateado:

```
message : "Pedido {PedidoId} creado por {Usuario}"     ← una sola entrada del diccionario
payload : {"category":"Ventas.Pedidos","PedidoId":42,"Usuario":"ana"}
```

Cien millones de pedidos comparten una sola cadena de mensaje. Los valores viajan aparte
como JSON UTF-8. Si prefieres el texto plano, `o.UseMessageTemplate = false`.

| Opción | Por defecto | Qué hace |
|---|---|---|
| `MinimumLevel` | `Information` | Por debajo ni se formatea el mensaje. |
| `UseMessageTemplate` | `true` | Guarda la plantilla en vez del texto formateado. |
| `IncludeParameters` | `true` | Parámetros con nombre y excepción como JSON en el payload. |
| `CaptureActivity` | `true` | Rellena `TraceId` con la `Activity` actual. |
| `IncludeCategory` | `true` | Mete la categoría del logger en el payload. |
| `Service` | del almacén | Etiqueta de servicio. |

---

## El CLI: `logtool`

```bash
dotnet run --project LogStorage.CLI -- <comando>
# o publícalo y llámalo logtool
```

| Comando | Para qué |
|---|---|
| `search` | Busca y pinta resultados con colores por nivel. |
| `tail` | Los últimos N registros, con `--follow` para quedarse escuchando. |
| `export` | Vuelca a `ndjson`, `csv` o `json`. |
| `stats` | Resumen del almacén: rango, registros, disco, bytes por registro. |
| `ls` | Un día por línea: dónde vive, cuántos registros, cuánto ocupa. |
| `info` | Radiografía de un `.lsz`: cabecera y tabla de bloques. |
| `verify` | Comprueba la huella xxHash3 y la decodificación de cada bloque. |
| `reindex` | Reconstruye `manifest.idx`. |
| `archive` | Pasa días cerrados a Parquet. |
| `prune` | Borra datos antiguos (pregunta antes salvo `--yes`). |
| `bench` | Genera carga sintética y mide escritura, tamaño y lectura. |

```bash
logtool search --from -2h --contains pedido --level warn -n 50
logtool search --from 2023-05-01 --to 2023-05-02 --service ventas --json
logtool tail -n 30 --follow
logtool export -o incidencia.csv --format csv --from yesterday --level error
logtool info -f logs/2026/08/20/segment-00001.lsz --blocks
logtool archive --before today
logtool prune --before 2021-01-01 --archive --yes
```

Las fechas admiten atajos: `now`, `today`, `yesterday`, `tomorrow`, `-2h`, `-30m`, `-7d`,
o ISO 8601 (`2026-08-20`, `2026-08-20T10:15:00`).

---

## El Worker

`LogStorage.Worker` es un `BackgroundService` que mantiene el almacén solo. Vive aparte de
la aplicación que ingesta y **nunca toca los segmentos que otro proceso está escribiendo**:
solo trabaja con días ya cerrados.

```json
{
  "LogStorage": {
    "RootPath": "logs",
    "Interval": "00:15:00",
    "ArchiveAfterDays": 2,
    "SegmentRetentionDays": 0,
    "ArchiveRetentionDays": 1825,
    "Reindex": true,
    "Verify": false
  }
}
```

Cada ciclo: archiva los días anteriores a `hoy - ArchiveAfterDays`, aplica la retención,
opcionalmente verifica la integridad y reconstruye el manifiesto. Un ciclo que falla no
tumba el worker: se reintenta en el siguiente.

`SegmentRetentionDays` debe ser mayor que `ArchiveAfterDays` o el worker se niega a
arrancar: sería borrar segmentos que aún no se han archivado.

Se puede lanzar desde una consola o instalar como **servicio de Windows**; se da cuenta solo
de cómo lo han arrancado. Después de cada ciclo deja un `status.json` junto a su
configuración con lo que ha hecho.

---

## El panel y el instalador

Para una máquina Windows hay un instalador que reparte el worker, `logtool` y un panel de
control en la bandeja del sistema: estado del servicio, la carpeta del almacén, cuánto
ocupa y los cuatro ajustes de retención, sin editar JSON a mano.

```powershell
.\installer\build.ps1     # publica y compila artifacts\installer\LogStorage-1.0.0-setup.exe
```

El asistente pregunta la carpeta del almacén, registra el servicio y, si se lo pides, mete
`logtool` en el `PATH`. Al desinstalar no borra los logs sin preguntar.

Todo esto, con detalle, en la [guía de integración](./GUIA-INTEGRACION.md).

---

## Rendimiento medido

`logtool bench -n 2000000 -t 4`, portátil Windows 11, .NET 10, zstd nivel 3:

```
Escritura
  Ritmo             2.036.369 registros/s
  En disco          2,48 MB   (1,30 bytes por registro)
  Memoria asignada  39 MB     (20,5 bytes por registro)
  Recolecciones GC  gen0=2 gen1=2 gen2=2

Lectura
  recorrer todo         1.147 ms    2.000.000 resultados
  solo errores            134 ms       99.911 resultados
  un servicio             166 ms      500.000 resultados
  contiene 'Pago'         133 ms      399.423 resultados
  ventana de 1 min         47 ms      240.000 resultados
  últimos 20               27 ms           20 resultados
```

Dos recolecciones de gen0 para dos millones de registros: el camino caliente no asigna.

Para medir en tu máquina:

```bash
dotnet run --project LogStorage.CLI -c Release -- bench -n 2000000 -t 8
dotnet run --project bench/LogStorage.Benchmarks -c Release -- --filter *
```

---

## Estructura del proyecto

```
LogStorage.Core/          la librería (el SDK)
  Buffers/                varint y búferes pooled
  Compression/            zstd y passthrough
  Format/                 formato de bloque y de segmento
  Storage/                escritura, lectura, rutas, manifiesto
  Querying/               motor de consultas
  Archiving/              capa Parquet
  Logging/                proveedor de ILogger
  Hosting/                extensiones de DI
LogStorage.Worker/        servicio de mantenimiento
LogStorage.CLI/           logtool
LogStorage.Tray/          panel de control en la bandeja de Windows
installer/                instalador de Windows (Inno Setup) y su script de compilación
tests/LogStorage.Tests/                131 tests unitarios
tests/LogStorage.IntegrationTests/     39 tests de integración
bench/LogStorage.Benchmarks/           BenchmarkDotNet
```

```bash
dotnet build
dotnet test --project tests/LogStorage.Tests/LogStorage.Tests.csproj
dotnet test --project tests/LogStorage.IntegrationTests/LogStorage.IntegrationTests.csproj
```

---

## Lo que hay que saber antes de usarlo en producción

- **Un solo escritor por carpeta.** Un proceso abre el almacén en escritura; los que quieras en lectura, incluso a la vez.
- **Todo va en UTC.** Las carpetas, los rangos y los archivados. No hay zonas horarias que adivinar.
- **Un bloque nunca cruza la medianoche.** Por eso archivar un día es exacto: ni arrastra ni pierde registros del día siguiente.
- **Un segmento cortado se sigue leyendo.** No hay footer que reconstruir: la cabecera se reescribe tras cada bloque y los bloques se encadenan por longitud. Si se va la luz, se lee hasta el último bloque íntegro y ya.
- **El Parquet ocupa más que los segmentos.** Es lo esperado y está bien: se archiva para que dentro de cinco años lo abra DuckDB, pandas o Spark sin esta librería, no para ahorrar disco. Si solo te importa el tamaño, deja los segmentos y no archives.
- **No hay índice invertido.** `Contains` es una búsqueda de subcadena sobre el diccionario de cada bloque, no un motor full-text. Para el caso de uso ("encuéntrame el pedido X de hace tres años") es de sobra; para relevancia y ranking, no es la herramienta.

---

## Librerías que usa

[ZstdSharp](https://github.com/oleg-st/ZstdSharp) · [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) ·
[MemoryPack](https://github.com/Cysharp/MemoryPack) · [ZLinq](https://github.com/Cysharp/ZLinq) ·
[ZString](https://github.com/Cysharp/ZString) · [ZLogger](https://github.com/Cysharp/ZLogger) ·
[ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework) · [Kokuban](https://github.com/mayuki/Kokuban) ·
`System.IO.MemoryMappedFiles` · `System.Threading.Channels` · `System.IO.Hashing`
