namespace ElasticMem.App;

/// <summary>Вкладка «Обновление»: проверка, скачивание и установка новой версии в один клик, откат, установка из файла.</summary>
public sealed class UpdateTab : TabPage
{
    private readonly AppSettings _s = AppSettings.Load();
    private readonly string[] _restartArgs;
    private readonly Action<string> _log;
    private readonly TextBox _src = new() { Width = 460, PlaceholderText = "owner/repo GitHub, ссылка на manifest.json или на .zip/.exe" };
    private readonly CheckBox _chkStart = new() { Text = "Проверять при запуске", AutoSize = true };
    private readonly CheckBox _chkAuto = new() { Text = "Устанавливать автоматически", AutoSize = true };
    private readonly Button _btnCheck = new() { Text = "Проверить обновления", AutoSize = true };
    private readonly Button _btnInstall = new() { Text = "⬇ Обновить сейчас", AutoSize = true, Enabled = false };
    private readonly Button _btnFile = new() { Text = "Установить из файла…", AutoSize = true };
    private readonly Button _btnRollback = new() { Text = "↶ Откатить на предыдущую", AutoSize = true };
    private readonly Button _btnDefault = new() { Text = "Сервер по умолчанию", AutoSize = true };
    private readonly Label _lblVer = Ui.L("");
    private readonly Label _lblState = Ui.L("");
    private readonly TextBox _out;
    private UpdateInfo? _found;
    private bool _busy;

    public event Action<string>? Notify;

    public UpdateTab(TextBox output, string[] rawArgs, Action<string> log, string? updatedFrom) : base("Обновление")
    {
        _out = output; _log = log;
        var ra = new List<string>();
        for (int i = 0; i < rawArgs.Length; i++) { if (rawArgs[i] == "--updated") { i++; continue; } ra.Add(rawArgs[i]); }
        _restartArgs = ra.ToArray();

        _src.Text = _s.UpdateSource; _chkStart.Checked = _s.CheckOnStart; _chkAuto.Checked = _s.AutoInstall;
        _lblVer.Text = $"Установлена версия {Updater.Current}  ·  {Updater.ExePath}";
        _src.Leave += (_, _) => SaveSettings();
        _chkStart.CheckedChanged += (_, _) => SaveSettings();
        _chkAuto.CheckedChanged += (_, _) => SaveSettings();
        _btnCheck.Click += async (_, _) => await CheckAsync(interactive: true);
        _btnInstall.Click += async (_, _) => await InstallFoundAsync();
        _btnFile.Click += async (_, _) => await InstallFromFileAsync();
        _btnRollback.Click += (_, _) => Rollback();
        _btnDefault.Click += (_, _) => { _src.Text = ReportUploader.DefaultUpdateSource; SaveSettings(); };

        var hint = Ui.Hint(
            "По умолчанию обновления берутся с сервера разработчика. Обновление в один клик: программа скачивает новую версию, проверяет, что это ElasticMem и что версия новее, " +
            "закрывается, заменяет ElasticMem.exe (старый сохраняется как ElasticMem.exe.bak) и перезапускается с теми же параметрами — " +
            "агент в трее и автозапуск продолжают работать. Если программа установлена в Program Files, Windows спросит права администратора.\r\n" +
            "Источник: «владелец/репозиторий» на GitHub (берётся последний релиз с файлом *win*.zip или ElasticMem.exe), " +
            "ссылка на manifest.json {\"version\":\"0.4.1\",\"url\":\"…zip\",\"sha256\":\"…\"} или прямая ссылка на .zip/.exe. " +
            "Если архив прислали в чате — «Установить из файла…».");

        Controls.Add(new VStack()
            .Add(Row(_lblVer))
            .Add(Row(Ui.L("Источник обновлений:"), _src, _btnDefault, _btnCheck, _btnInstall))
            .Add(Row(_btnFile, _btnRollback, _chkStart, _chkAuto))
            .Add(hint)
            .Add(Row(_lblState))
            .Add(_out, fill: true));

        _btnRollback.Enabled = File.Exists(Updater.BackupPath);
        if (updatedFrom != null)
        {
            string m = $"Обновлено: {updatedFrom} → {Updater.Current}.";
            _lblState.Text = m; Write(m); _log(m);
        }
        Updater.Cleanup();
    }

    private static FlowLayoutPanel Row(params Control[] cs) => Ui.Row(cs);

    private void SaveSettings()
    {
        _s.UpdateSource = _src.Text.Trim(); _s.CheckOnStart = _chkStart.Checked; _s.AutoInstall = _chkAuto.Checked;
        try { _s.Save(); } catch (Exception ex) { Write("Не удалось сохранить настройки: " + ex.Message); }
    }

    private void Write(string s) => _out.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
    private void State(string s) { _lblState.Text = s; Write(s); }

    private void Busy(bool b)
    {
        _busy = b;
        _btnCheck.Enabled = _btnFile.Enabled = !b;
        _btnInstall.Enabled = !b && _found != null;
        _btnRollback.Enabled = !b && File.Exists(Updater.BackupPath);
    }

    /// <summary>Вызывается при старте программы.</summary>
    public async Task StartupCheckAsync()
    {
        if (!_s.CheckOnStart || string.IsNullOrWhiteSpace(_s.UpdateSource)) return;
        await Task.Delay(3000);
        await CheckAsync(interactive: false);
        if (_found != null && _s.AutoInstall) await InstallFoundAsync();
    }

    private async Task CheckAsync(bool interactive)
    {
        if (_busy) return;
        SaveSettings();
        _found = null; Busy(true);
        try
        {
            State("Проверка обновлений…");
            var info = await Updater.CheckAsync(_s.UpdateSource, CancellationToken.None);
            _s.LastCheck = DateTime.Now; SaveSettings();
            if (info.Version.Major == 0 && info.Version.Minor == 0 && info.Version.Build == 0)
            {
                _found = info; State("Прямая ссылка: версия станет известна после скачивания. Нажмите «Обновить сейчас».");
            }
            else if (info.Version > Updater.Current)
            {
                _found = info;
                State($"Доступна версия {info.Version} (у вас {Updater.Current}), источник: {info.Source}.");
                if (!string.IsNullOrWhiteSpace(info.Notes)) Write("Что нового:" + Environment.NewLine + info.Notes.ReplaceLineEndings(Environment.NewLine));
                Notify?.Invoke($"Доступна ElasticMem {info.Version}");
            }
            else State($"Установлена последняя версия ({Updater.Current}; в источнике {info.Version}).");
        }
        catch (Exception ex)
        {
            State("Проверка не удалась: " + ex.Message);
            if (interactive && ex is HttpRequestException) Write("Проверьте подключение к интернету и адрес источника.");
        }
        finally { Busy(false); }
    }

    private async Task InstallFoundAsync()
    {
        if (_busy || _found is not { } info) return;
        Busy(true);
        try
        {
            State($"Скачивание {info.DownloadUrl}");
            var prog = new Progress<string>(s => _lblState.Text = s);
            string file = await Updater.DownloadAsync(info, prog, CancellationToken.None);
            await PrepareAndLaunch(file, requireNewer: true);
        }
        catch (Exception ex) { State("Обновление не удалось: " + ex.Message); }
        finally { Busy(false); }
    }

    private async Task InstallFromFileAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "ElasticMem (*.zip;*.exe)|*.zip;*.exe", Title = "Выберите архив или exe новой версии" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Busy(true);
        try { await PrepareAndLaunch(dlg.FileName, requireNewer: false); }
        catch (Exception ex) { State("Установка не удалась: " + ex.Message); }
        finally { Busy(false); }
    }

    private async Task PrepareAndLaunch(string file, bool requireNewer)
    {
        State("Проверка файла (SHA-256 " + Updater.Sha256(file)[..16] + "…)");
        var (exe, ver) = await Task.Run(() => Updater.Prepare(file));
        if (ver <= Updater.Current)
        {
            if (requireNewer) { State($"В файле версия {ver}, она не новее установленной {Updater.Current}."); return; }
            if (MessageBox.Show(this, $"В файле версия {ver}, установлена {Updater.Current}. Всё равно установить?", "ElasticMem",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        State($"Установка версии {ver}: программа закроется и перезапустится.");
        _log($"Обновление до {ver}");
        await Task.Delay(400);
        Updater.Launch(exe, _restartArgs);
        Application.Exit();
    }

    private void Rollback()
    {
        if (!File.Exists(Updater.BackupPath)) { State("Резервной копии нет."); return; }
        if (MessageBox.Show(this, "Вернуть предыдущую версию из ElasticMem.exe.bak?", "ElasticMem", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ElasticMem_update", $"rollback_{DateTime.Now:HHmmssfff}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            File.Copy(Updater.BackupPath, tmp, true);
            State("Откат: программа закроется и перезапустится.");
            Updater.Launch(tmp, _restartArgs);
            Application.Exit();
        }
        catch (Exception ex) { State("Откат не удался: " + ex.Message); }
    }
}
