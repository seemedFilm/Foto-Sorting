using System.ComponentModel;
using System.Text;

namespace FotoSortierer;

// Oberfläche wird im Code aufgebaut – öffnet in Visual Studio daher direkt die Codeansicht.
[DesignerCategory("")]
public sealed class MainForm : Form
{
    private readonly TextBox _txtSource = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtTarget = new() { Dock = DockStyle.Fill };
    private readonly Button _btnSource = new() { Text = "Durchsuchen …", AutoSize = true };
    private readonly Button _btnTarget = new() { Text = "Durchsuchen …", AutoSize = true };

    private readonly RadioButton _rbCopy = new() { Text = "Kopieren", Checked = true, AutoSize = true };
    private readonly RadioButton _rbMove = new() { Text = "Verschieben", AutoSize = true };
    private readonly CheckBox _chkSubfolders = new() { Text = "Unterordner einbeziehen", Checked = true, AutoSize = true };
    private readonly CheckBox _chkDryRun = new() { Text = "Testlauf (nichts verändern)", AutoSize = true };
    private readonly CheckBox _chkDeleteEmpty = new() { Text = "Leere Quellordner entfernen", AutoSize = true, Enabled = false };

    private readonly Button _btnStart = new() { Text = "Sortieren starten", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnCancel = new() { Text = "Abbrechen", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 20 };
    private readonly Label _lblStatus = new() { Text = "Bereit.", AutoSize = true, Anchor = AnchorStyles.Left };

    private readonly TextBox _txtLog = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9f),
        BackColor = SystemColors.Window
    };

    // Log-Zeilen werden gepuffert und per Timer gesammelt ausgegeben (schnell auch bei 50.000 Dateien)
    private readonly StringBuilder _logBuffer = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 200 };
    private CancellationTokenSource? _cts;

    public MainForm()
    {
        Text = "Foto-Sortierer – Dateien nach Jahr-Monat (yyyy-MM) sortieren";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 480);
        Size = new Size(900, 620);

        BuildLayout();

        _btnSource.Click += (_, _) => PickFolder(_txtSource, "Quellordner wählen");
        _btnTarget.Click += (_, _) => PickFolder(_txtTarget, "Zielordner wählen");
        _rbMove.CheckedChanged += (_, _) => _chkDeleteEmpty.Enabled = _rbMove.Checked;
        _btnStart.Click += async (_, _) => await StartAsync();
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); _btnCancel.Enabled = false; _lblStatus.Text = "Wird abgebrochen …"; };
        _logTimer.Tick += (_, _) => FlushLog();
        FormClosing += OnFormClosing;

        AcceptButton = _btnStart;
        _logTimer.Start();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 3,
            RowCount = 6
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label { Text = "Quellordner:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        root.Controls.Add(_txtSource, 1, 0);
        root.Controls.Add(_btnSource, 2, 0);

        root.Controls.Add(new Label { Text = "Zielordner:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        root.Controls.Add(_txtTarget, 1, 1);
        root.Controls.Add(_btnTarget, 2, 1);

        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 6, 0, 6) };
        var modePanel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 24, 0) };
        modePanel.Controls.AddRange([_rbCopy, _rbMove]);   // eigenes Panel = eigene Radio-Gruppe
        options.Controls.AddRange([modePanel, _chkSubfolders, _chkDryRun, _chkDeleteEmpty]);
        root.Controls.Add(options, 0, 2);
        root.SetColumnSpan(options, 3);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([_btnStart, _btnCancel, _lblStatus]);
        root.Controls.Add(buttons, 0, 3);
        root.SetColumnSpan(buttons, 3);

        root.Controls.Add(_progress, 0, 4);
        root.SetColumnSpan(_progress, 3);

        root.Controls.Add(_txtLog, 0, 5);
        root.SetColumnSpan(_txtLog, 3);

        Controls.Add(root);
    }

    private void PickFolder(TextBox box, string description)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(box.Text) ? box.Text : ""
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            box.Text = dlg.SelectedPath;
            // Komfort: Ziel vorbelegen
            if (box == _txtSource && string.IsNullOrWhiteSpace(_txtTarget.Text))
                _txtTarget.Text = Path.Combine(dlg.SelectedPath, "Sortiert");
        }
    }

    private async Task StartAsync()
    {
        var source = _txtSource.Text.Trim();
        var target = _txtTarget.Text.Trim();

        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, "Bitte einen existierenden Quellordner wählen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "Bitte einen Zielordner angeben.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_rbMove.Checked && !_chkDryRun.Checked &&
            MessageBox.Show(this,
                $"Die Dateien werden aus\n{source}\nnach\n{target}\nVERSCHOBEN.\n\nFortfahren?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var options = new SortOptions
        {
            SourceFolder = source,
            TargetFolder = target,
            Move = _rbMove.Checked,
            IncludeSubfolders = _chkSubfolders.Checked,
            DryRun = _chkDryRun.Checked,
            DeleteEmptyFolders = _rbMove.Checked && _chkDeleteEmpty.Checked
        };

        _txtLog.Clear();
        _progress.Value = 0;
        SetRunning(true);
        _cts = new CancellationTokenSource();

        var progress = new Progress<SortProgress>(p =>
        {
            if (p.LogLine != null)
                lock (_logBuffer) _logBuffer.AppendLine(p.LogLine);
            if (p.Total > 0 && _cts != null)
            {
                _progress.Maximum = p.Total;
                _progress.Value = Math.Min(p.Processed, p.Total);
                _lblStatus.Text = $"{p.Processed} / {p.Total}";
            }
        });

        try
        {
            var result = await Task.Run(() => FileSorter.Run(options, progress, _cts.Token));
            _lblStatus.Text = $"Fertig – {result.Sorted} einsortiert, {result.Duplicates} Duplikate, {result.Errors} Fehler"
                              + (options.DryRun ? " (Testlauf)" : "");
            if (result.LogFile != null)
                AppendLog($"Protokoll gespeichert: {result.LogFile}");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Abgebrochen.");
            _lblStatus.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            AppendLog("FEHLER: " + ex.Message);
            _lblStatus.Text = "Fehler.";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _btnStart.Enabled = !running;
        _btnCancel.Enabled = running;
        _txtSource.Enabled = _txtTarget.Enabled = _btnSource.Enabled = _btnTarget.Enabled = !running;
        _rbCopy.Enabled = _rbMove.Enabled = _chkSubfolders.Enabled = _chkDryRun.Enabled = !running;
        _chkDeleteEmpty.Enabled = !running && _rbMove.Checked;
    }

    private void FlushLog()
    {
        string text;
        lock (_logBuffer)
        {
            if (_logBuffer.Length == 0) return;
            text = _logBuffer.ToString();
            _logBuffer.Clear();
        }
        _txtLog.AppendText(text);
    }

    // Über den Puffer, damit die Reihenfolge zu noch ausstehenden Progress-Meldungen stimmt
    private void AppendLog(string line)
    {
        lock (_logBuffer) _logBuffer.AppendLine(line);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cts == null) return;
        if (MessageBox.Show(this, "Der Vorgang läuft noch. Abbrechen und beenden?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _cts.Cancel();
        e.Cancel = true; // Fenster schließt, sobald der Vorgang sauber beendet ist
        if (_cts.IsCancellationRequested)
            _ = WaitAndCloseAsync();
    }

    private async Task WaitAndCloseAsync()
    {
        while (_cts != null) await Task.Delay(100);
        Close();
    }
}
