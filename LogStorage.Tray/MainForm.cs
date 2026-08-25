using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace LogStorage.Tray;

/// <summary>
/// El panel de control: estado del servicio, carpeta del almacén y los cuatro ajustes que
/// de verdad se tocan.
/// </summary>
/// <remarks>
/// <para>
/// Deliberadamente <b>no</b> es un visor de logs. Buscar, filtrar y exportar ya lo hace
/// <c>logtool</c> mucho mejor de lo que lo haría una rejilla aquí; el botón "Ver logs" se
/// limita a lanzarlo. Lo que esta ventana aporta es lo que la consola no da: saber de un
/// vistazo si esto está vivo y cambiar la configuración sin editar JSON.
/// </para>
/// <para>
/// Todo lo que lee del almacén va en solo lectura. El panel no puede corromper nada.
/// </para>
/// </remarks>
public sealed class MainForm : Form
{
    private const int Pad = 16;
    private const int LabelWidth = 118;
    private const int FormWidth = 560;

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _trayToggle;

    private readonly Label _stateDot = new();
    private readonly Label _stateText = new();
    private readonly Label _stateDetail = new();
    private readonly Button _startStop = new();
    private readonly Button _restart = new();
    private readonly Button _install = new();

    private readonly ToolTip _tips = new();
    private readonly TextBox _root = new();
    private readonly Button _change = new();
    private readonly Button _open = new();

    private readonly Label _storeLine = new();
    private readonly Label _rangeLine = new();
    private readonly Label _cycleLine = new();
    private readonly Button _refresh = new();

    private readonly NumericUpDown _archiveAfter = new();
    private readonly NumericUpDown _segmentRetention = new();
    private readonly NumericUpDown _archiveRetention = new();
    private readonly TextBox _interval = new();

    private readonly Button _verify = new();
    private readonly Button _viewLogs = new();
    private readonly Button _save = new();
    private readonly Label _status = new();

    private WorkerConfig _config = new();
    private ServiceState _state = ServiceState.NotInstalled;
    private bool _busy;
    private bool _exiting;
    private int _ticksSinceStoreRead;
    private Icon? _shownIcon;

    /// <summary>Construye la ventana y la deja lista, pero no la muestra.</summary>
    public MainForm()
    {
        Text = "LogStorage";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(FormWidth, 500);
        Icon = TrayIcons.Unknown;
        BackColor = SystemColors.Window;

        BuildLayout();

        _trayToggle = new ToolStripMenuItem("Iniciar servicio", null, (_, _) => _ = ToggleServiceAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Abrir panel", null, (_, _) => ShowPanel()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripMenuItem("Ver logs en vivo", null, (_, _) => ViewLogs()));
        menu.Items.Add(new ToolStripMenuItem("Abrir carpeta del almacén", null, (_, _) => OpenRoot()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, (_, _) => ExitApplication()));

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Unknown,
            Text = "LogStorage",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowPanel();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => RefreshUi(readStore: ++_ticksSinceStoreRead >= 15);

        LoadConfig();
        RefreshUi(readStore: true);
        _timer.Start();
    }

    // ── Construcción de la ventana ─────────────────────────────────────────────────

    private void BuildLayout()
    {
        var y = Pad;

        // Servicio
        y = Section("Servicio", y);

        _stateDot.SetBounds(Pad, y + 3, 12, 12);
        _stateDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(TrayIcons.ColorFor(_state));
            e.Graphics.FillEllipse(brush, 0, 0, 11, 11);
        };
        Controls.Add(_stateDot);

        _stateText.SetBounds(Pad + 20, y, 240, 20);
        _stateText.Font = new Font(Font, FontStyle.Bold);
        Controls.Add(_stateText);

        _install.SetBounds(FormWidth - Pad - 110, y - 3, 110, 26);
        _install.Text = "Instalar servicio";
        _install.Click += (_, _) => _ = InstallServiceAsync();
        Controls.Add(_install);

        _startStop.SetBounds(FormWidth - Pad - 110, y - 3, 110, 26);
        _startStop.Text = "Iniciar";
        _startStop.Click += (_, _) => _ = ToggleServiceAsync();
        Controls.Add(_startStop);

        _restart.SetBounds(FormWidth - Pad - 220, y - 3, 105, 26);
        _restart.Text = "Reiniciar";
        _restart.Click += (_, _) => _ = RunAsync("Reiniciando el servicio…", ServiceManager.RestartAsync);
        Controls.Add(_restart);

        _stateDetail.SetBounds(Pad + 20, y + 24, FormWidth - 2 * Pad - 24, 18);
        _stateDetail.ForeColor = SystemColors.GrayText;
        _stateDetail.AutoEllipsis = true;
        Controls.Add(_stateDetail);
        y += 52;

        // Carpeta
        y = Section("Carpeta del almacén", y);

        _root.SetBounds(Pad, y, FormWidth - 2 * Pad - 170, 23);
        _root.ReadOnly = true;
        _root.BackColor = SystemColors.Control;
        // El cuadro no da para una ruta larga; el tooltip enseña la de verdad.
        _root.TextChanged += (_, _) => _tips.SetToolTip(_root, _root.Text);
        Controls.Add(_root);

        _change.SetBounds(FormWidth - Pad - 165, y - 1, 80, 26);
        _change.Text = "Cambiar…";
        _change.Click += (_, _) => ChangeRoot();
        Controls.Add(_change);

        _open.SetBounds(FormWidth - Pad - 80, y - 1, 80, 26);
        _open.Text = "Abrir";
        _open.Click += (_, _) => OpenRoot();
        Controls.Add(_open);
        y += 38;

        // Almacén
        y = Section("Contenido", y);

        _storeLine.SetBounds(Pad, y, FormWidth - 2 * Pad - 90, 18);
        Controls.Add(_storeLine);

        _refresh.SetBounds(FormWidth - Pad - 85, y - 3, 85, 26);
        _refresh.Text = "Actualizar";
        _refresh.Click += (_, _) => _ = RefreshStoreAsync(rebuild: true);
        Controls.Add(_refresh);

        _rangeLine.SetBounds(Pad, y + 20, FormWidth - 2 * Pad - 90, 18);
        _rangeLine.ForeColor = SystemColors.GrayText;
        Controls.Add(_rangeLine);

        _cycleLine.SetBounds(Pad, y + 40, FormWidth - 2 * Pad, 18);
        _cycleLine.ForeColor = SystemColors.GrayText;
        Controls.Add(_cycleLine);
        y += 68;

        // Mantenimiento
        y = Section("Mantenimiento", y);

        y = Spin(_archiveAfter, "Archivar tras", "días  ·  pasa los días cerrados a Parquet", 0, 3650, y);
        y = Spin(_segmentRetention, "Borrar segmentos", "días  ·  0 = conservarlos siempre", 0, 3650, y);
        y = Spin(_archiveRetention, "Borrar archivo", "días  ·  0 = conservarlo siempre", 0, 36500, y);

        AddLabel("Cada", Pad, y + 3, LabelWidth);
        _interval.SetBounds(Pad + LabelWidth + 8, y, 70, 23);
        Controls.Add(_interval);
        AddLabel("hh:mm:ss  ·  cada cuánto corre un ciclo", Pad + LabelWidth + 86, y + 3, 300, SystemColors.GrayText);
        y += 34;

        // Acciones
        y += 6;
        _verify.SetBounds(Pad, y, 130, 30);
        _verify.Text = "Verificar integridad";
        _verify.Click += (_, _) => _ = VerifyAsync();
        Controls.Add(_verify);

        _viewLogs.SetBounds(Pad + 138, y, 100, 30);
        _viewLogs.Text = "Ver logs";
        _viewLogs.Click += (_, _) => ViewLogs();
        Controls.Add(_viewLogs);

        _save.SetBounds(FormWidth - Pad - 160, y, 160, 30);
        _save.Text = "Guardar y reiniciar";
        _save.Click += (_, _) => _ = SaveAsync();
        Controls.Add(_save);
        y += 40;

        _status.SetBounds(Pad, y, FormWidth - 2 * Pad, 20);
        _status.ForeColor = SystemColors.GrayText;
        Controls.Add(_status);

        ClientSize = new Size(FormWidth, y + 30);
    }

    private int Section(string title, int y)
    {
        var label = new Label
        {
            Text = title.ToUpperInvariant(),
            ForeColor = SystemColors.Highlight,
            Font = new Font(Font.FontFamily, Font.Size - 0.5f, FontStyle.Bold),
        };
        label.SetBounds(Pad, y, 300, 16);
        Controls.Add(label);

        var rule = new Label { BorderStyle = BorderStyle.Fixed3D };
        rule.SetBounds(Pad, y + 18, FormWidth - 2 * Pad, 2);
        Controls.Add(rule);

        return y + 28;
    }

    private void AddLabel(string text, int x, int y, int width, Color? color = null)
    {
        var label = new Label { Text = text, ForeColor = color ?? ForeColor, AutoEllipsis = true };
        label.SetBounds(x, y, width, 18);
        Controls.Add(label);
    }

    private int Spin(NumericUpDown spin, string caption, string hint, int minimum, int maximum, int y)
    {
        AddLabel(caption, Pad, y + 3, LabelWidth);

        spin.SetBounds(Pad + LabelWidth + 8, y, 70, 23);
        spin.Minimum = minimum;
        spin.Maximum = maximum;
        spin.TextAlign = HorizontalAlignment.Right;
        Controls.Add(spin);

        AddLabel(hint, Pad + LabelWidth + 86, y + 3, FormWidth - Pad * 2 - LabelWidth - 86, SystemColors.GrayText);
        return y + 30;
    }

    // ── Estado ─────────────────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        _config = WorkerConfigFile.Load();
        _root.Text = _config.RootPath;
        _archiveAfter.Value = Clamp(_archiveAfter, _config.ArchiveAfterDays);
        _segmentRetention.Value = Clamp(_segmentRetention, _config.SegmentRetentionDays);
        _archiveRetention.Value = Clamp(_archiveRetention, _config.ArchiveRetentionDays);
        _interval.Text = _config.Interval.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static decimal Clamp(NumericUpDown spin, int value)
        => Math.Clamp(value, (int)spin.Minimum, (int)spin.Maximum);

    private void RefreshUi(bool readStore)
    {
        _state = ServiceManager.GetState();
        _stateDot.Invalidate();

        var installed = _state != ServiceState.NotInstalled;
        _install.Visible = !installed;
        _startStop.Visible = installed;
        _restart.Visible = installed;

        _stateText.Text = _state switch
        {
            ServiceState.Running => "En ejecución",
            ServiceState.Stopped => "Detenido",
            ServiceState.Starting => "Arrancando…",
            ServiceState.Stopping => "Parando…",
            ServiceState.NotInstalled => "No instalado",
            _ => "Estado desconocido",
        };

        _startStop.Text = _state == ServiceState.Running ? "Detener" : "Iniciar";
        _startStop.Enabled = !_busy && _state is ServiceState.Running or ServiceState.Stopped;
        _restart.Enabled = !_busy && _state == ServiceState.Running;
        _install.Enabled = !_busy && Paths.WorkerExecutable is not null;

        _trayToggle.Text = _state == ServiceState.Running ? "Detener servicio" : "Iniciar servicio";
        _trayToggle.Enabled = installed && !_busy;

        // Solo se reasigna cuando cambia de verdad: repintar la bandeja cada dos segundos
        // hace parpadear el icono.
        var icon = TrayIcons.For(_state);
        if (!ReferenceEquals(icon, _shownIcon))
        {
            _shownIcon = icon;
            _tray.Icon = icon;
            Icon = icon;
        }

        var status = WorkerStatusFile.TryRead();
        UpdateStatusLines(status);

        _tray.Text = $"LogStorage · {_stateText.Text}";

        if (readStore)
        {
            _ticksSinceStoreRead = 0;
            _ = RefreshStoreAsync(rebuild: false);
        }
    }

    private void UpdateStatusLines(WorkerStatus? status)
    {
        _stateDetail.Text = _state switch
        {
            ServiceState.NotInstalled when Paths.WorkerExecutable is null
                => "No se encuentra LogStorage.Worker.exe junto al panel.",
            ServiceState.NotInstalled
                => "Pulsa «Instalar servicio» para que arranque con Windows.",
            ServiceState.Running when status?.NextCycleUtc is { } next
                => $"Siguiente ciclo {Format.Countdown(next)}.",
            _ => "El mantenimiento archiva y poda sin tocar lo que se está escribiendo.",
        };

        // El estado se enseña siempre que exista, aunque no haya servicio registrado: el
        // worker también se puede lanzar a mano o desde una tarea programada.
        if (status is null)
        {
            _cycleLine.Text = _state == ServiceState.NotInstalled ? string.Empty : "Último ciclo: todavía ninguno.";
            _cycleLine.ForeColor = SystemColors.GrayText;
            return;
        }

        _cycleLine.Text = $"Último ciclo: {Format.Moment(status.LastCycleUtc)} · {status.Message}";
        _cycleLine.ForeColor = status.Ok ? SystemColors.GrayText : Color.FromArgb(180, 40, 40);
    }

    private async Task RefreshStoreAsync(bool rebuild)
    {
        var root = _root.Text;
        if (rebuild) SetStatus("Recorriendo los segmentos…");

        try
        {
            var snapshot = rebuild
                ? await StoreReader.RefreshAsync(root)
                : await StoreReader.ReadAsync(root);

            if (IsDisposed) return;

            if (!snapshot.Exists)
            {
                _storeLine.Text = "La carpeta todavía no existe. Se creará cuando algo escriba en ella.";
                _rangeLine.Text = string.Empty;
            }
            else if (snapshot.Days == 0)
            {
                _storeLine.Text = snapshot.HasManifest
                    ? "Sin datos todavía."
                    : "Sin manifiesto. Pulsa «Actualizar» para recorrer los segmentos.";
                _rangeLine.Text = string.Empty;
            }
            else
            {
                var days = snapshot.Days == 1 ? "1 día" : $"{snapshot.Days} días";
                _storeLine.Text = $"{days} · {Format.Count(snapshot.Records)} registros · {Format.Bytes(snapshot.Bytes)}";
                _rangeLine.Text = string.Create(CultureInfo.InvariantCulture,
                    $"De {snapshot.OldestDay} a {snapshot.NewestDay} · {snapshot.BytesPerRecord:0.00} bytes por registro");
            }

            if (rebuild) SetStatus("Manifiesto reconstruido.");
        }
        catch (Exception ex)
        {
            _storeLine.Text = "No se ha podido leer el almacén.";
            _rangeLine.Text = ex.Message;
        }
    }

    // ── Acciones ───────────────────────────────────────────────────────────────────

    private Task ToggleServiceAsync() => _state == ServiceState.Running
        ? RunAsync("Deteniendo el servicio…", ServiceManager.StopAsync)
        : RunAsync("Arrancando el servicio…", ServiceManager.StartAsync);

    private async Task InstallServiceAsync()
    {
        var worker = Paths.WorkerExecutable;
        if (worker is null)
        {
            Warn("No se encuentra LogStorage.Worker.exe. Debe estar en la misma carpeta que este panel.");
            return;
        }

        if (MessageBox.Show(
                this,
                $"Se registrará el servicio «{Paths.ServiceDisplayName}» para que arranque con Windows.\n\n"
                + $"Ejecutable: {worker}\nDatos: {Paths.DataDirectory}\n\nHará falta confirmar el aviso de administrador.",
                "Instalar el servicio",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        Directory.CreateDirectory(Paths.DataDirectory);
        if (!File.Exists(Paths.ConfigFile)) WorkerConfigFile.Save(_config);

        await RunAsync("Instalando el servicio…", () => ServiceManager.InstallAsync(worker));
        if (ServiceManager.GetState() == ServiceState.Stopped)
        {
            await RunAsync("Arrancando el servicio…", ServiceManager.StartAsync);
        }
    }

    private void ChangeRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Carpeta donde se guardan los logs",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_root.Text) ? _root.Text : Paths.DataDirectory,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _root.Text = dialog.SelectedPath;
        SetStatus("Carpeta cambiada. Pulsa «Guardar y reiniciar» para aplicarlo.");
        _ = RefreshStoreAsync(rebuild: false);
    }

    private void OpenRoot()
    {
        var root = _root.Text;
        if (string.IsNullOrWhiteSpace(root)) return;

        try
        {
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"No se ha podido abrir la carpeta: {ex.Message}");
        }
    }

    private void ViewLogs()
    {
        var tool = Paths.LogToolExecutable;
        if (tool is null)
        {
            Warn("No se encuentra logtool.exe junto al panel.");
            return;
        }

        try
        {
            // Se abre en una consola nueva: el seguimiento en vivo se ve mucho mejor ahí que
            // en una rejilla, y así no reimplementamos la búsqueda dentro de la ventana.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{tool}\" tail -r \"{_root.Text}\" -n 40 --follow\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Warn($"No se ha podido lanzar logtool: {ex.Message}");
        }
    }

    private async Task VerifyAsync()
    {
        if (!Directory.Exists(_root.Text))
        {
            Warn("La carpeta del almacén todavía no existe.");
            return;
        }

        SetBusy(true, "Verificando la huella de cada bloque…");
        try
        {
            var report = await StoreReader.VerifyAsync(_root.Text);

            var summary = string.Create(CultureInfo.InvariantCulture,
                $"{report.Segments} segmentos · {report.Blocks} bloques · {report.Records:N0} registros");

            if (report.Ok)
            {
                MessageBox.Show(this, $"Integridad correcta.\n\n{summary}", "Verificación",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Integridad correcta.");
            }
            else
            {
                var problems = string.Join(Environment.NewLine, report.Problems.Take(15));
                if (report.Problems.Count > 15) problems += $"{Environment.NewLine}… y {report.Problems.Count - 15} más.";

                MessageBox.Show(this, $"{report.Problems.Count} problemas.\n\n{summary}\n\n{problems}", "Verificación",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus($"{report.Problems.Count} problemas de integridad.");
            }
        }
        catch (Exception ex)
        {
            Warn($"La verificación ha fallado: {ex.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task SaveAsync()
    {
        if (!TimeSpan.TryParse(_interval.Text, CultureInfo.InvariantCulture, out var interval))
        {
            Warn("El intervalo tiene que ser hh:mm:ss, por ejemplo 00:15:00.");
            _interval.Focus();
            return;
        }

        var candidate = new WorkerConfig
        {
            RootPath = _root.Text.Trim(),
            Interval = interval,
            InitialDelay = _config.InitialDelay,
            CompressionLevel = _config.CompressionLevel,
            ArchiveAfterDays = (int)_archiveAfter.Value,
            SegmentRetentionDays = (int)_segmentRetention.Value,
            ArchiveRetentionDays = (int)_archiveRetention.Value,
            Reindex = _config.Reindex,
            Verify = _config.Verify,
        };

        if (candidate.Validate() is { } problem)
        {
            Warn(problem);
            return;
        }

        SetBusy(true, "Guardando…");
        try
        {
            WorkerConfigFile.Save(candidate);
            _config = candidate;
            Directory.CreateDirectory(candidate.RootPath);
        }
        catch (Exception ex)
        {
            SetBusy(false, null);
            Warn($"No se ha podido guardar la configuración: {ex.Message}");
            return;
        }

        if (_state == ServiceState.Running)
        {
            SetStatus("Reiniciando el servicio para aplicar los cambios…");
            try
            {
                await ServiceManager.RestartAsync();
                SetStatus("Guardado y aplicado.");
            }
            catch (Exception ex)
            {
                SetStatus("Guardado, pero el servicio no se ha reiniciado.");
                Warn(Explain(ex));
            }
        }
        else
        {
            SetStatus("Guardado. Se aplicará cuando arranque el servicio.");
        }

        SetBusy(false, null);
        await RefreshStoreAsync(rebuild: false);
    }

    private async Task RunAsync(string message, Func<Task> action)
    {
        SetBusy(true, message);
        try
        {
            await action();
            SetStatus(null);
        }
        catch (Exception ex)
        {
            Warn(Explain(ex));
            SetStatus(null);
        }
        finally
        {
            SetBusy(false, null);
            RefreshUi(readStore: false);
        }
    }

    /// <summary>Traduce los fallos habituales a algo que se pueda leer sin ser administrador de sistemas.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        Win32Exception { NativeErrorCode: 1223 } => "Se ha cancelado el aviso de administrador, así que no se ha hecho nada.",
        System.ServiceProcess.TimeoutException => "El servicio no ha respondido a tiempo. Míralo en services.msc.",
        _ => ex.Message,
    };

    // ── Ventana y bandeja ──────────────────────────────────────────────────────────

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;

        foreach (var button in new[] { _startStop, _restart, _install, _save, _verify, _refresh, _change })
        {
            button.Enabled = !busy;
        }

        if (message is not null) SetStatus(message);
        if (!busy) RefreshUi(readStore: false);
    }

    private void SetStatus(string? message) => _status.Text = message ?? string.Empty;

    private void Warn(string message)
        => MessageBox.Show(this, message, "LogStorage", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ShowPanel()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        RefreshUi(readStore: true);
    }

    private void ExitApplication()
    {
        _exiting = true;
        _tray.Visible = false;
        Application.Exit();
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Cerrar la ventana la esconde en la bandeja; para salir de verdad, el menú.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
