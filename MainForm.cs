using System.Diagnostics;

namespace MacroRecorder;

public sealed class MainForm : Form
{
    private const double MinMoveIntervalMs = 4; // thin out mouse-move spam from high-polling-rate mice
    private const double HotkeyModifierWindowMs = 2000; // modifier presses this close to the stop hotkey belong to it

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly GlobalHooks _hooks = new();
    private readonly Player _player = new();
    private readonly List<MacroEvent> _events = new();
    private readonly Stopwatch _recordClock = new();
    private readonly HashSet<Keys> _hotkeysHeld = new();
    private readonly HashSet<int> _keysDownInRecording = new();
    private double _lastEventMs;
    private bool _recording;
    private bool _recordMoves = true;
    private bool _hotkeysSuspended;
    private bool _dirty;
    private string? _currentFile;

    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1000 };
    private CountdownOverlay? _overlay;
    private int _countdownRemaining;
    private bool CountingDown => _overlay != null;

    private readonly Button _btnRecord = new() { AutoSize = true };
    private readonly Button _btnPlay = new() { AutoSize = true };
    private readonly Button _btnLoad = new() { Text = "Open…", AutoSize = true };
    private readonly Button _btnSave = new() { Text = "Save…", AutoSize = true };
    private readonly Button _btnDelete = new() { Text = "Delete selected", AutoSize = true };
    private readonly Button _btnClear = new() { Text = "Clear", AutoSize = true };
    private readonly Button _btnSettings = new() { Text = "Settings…", AutoSize = true };
    private readonly NumericUpDown _speed = new()
    {
        Minimum = 0.1m, Maximum = 20m, Increment = 0.25m, DecimalPlaces = 2, Value = 1m, Width = 70,
    };
    private readonly NumericUpDown _repeat = new() { Minimum = 0, Maximum = 1_000_000, Value = 1, Width = 80 };
    private readonly CheckBox _chkMoves = new() { Text = "Record mouse movement", Checked = true, AutoSize = true };
    private readonly CheckBox _chkMinimize = new() { Text = "Minimize while recording/playing", AutoSize = true };
    private readonly CheckBox _chkTopMost = new() { Text = "Always on top", AutoSize = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        VirtualMode = true,
        HideSelection = false,
        GridLines = true,
    };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 150 };

    public MainForm()
    {
        Text = "Macro Recorder";
        ClientSize = new Size(720, 520);
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _list.Columns.Add("#", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Delay (ms)", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Event", 110);
        _list.Columns.Add("Details", 380);
        _list.RetrieveVirtualItem += (_, e) => e.Item = BuildItem(e.ItemIndex);
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
        buttons.Controls.AddRange(new Control[] { _btnRecord, _btnPlay, _btnLoad, _btnSave, _btnDelete, _btnClear, _btnSettings });

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 2, 6, 4) };
        options.Controls.AddRange(new Control[]
        {
            Label("Speed ×"), _speed,
            Label("Repeat (0 = forever)"), _repeat,
            _chkMoves, _chkMinimize, _chkTopMost,
        });

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);

        // Fill control first so the docked Top/Bottom controls claim their space before it.
        Controls.Add(_list);
        Controls.Add(options);
        Controls.Add(buttons);
        Controls.Add(statusStrip);

        _btnRecord.Click += (_, _) => ToggleRecord();
        _btnPlay.Click += (_, _) => TogglePlay();
        _btnLoad.Click += (_, _) => LoadMacro();
        _btnSave.Click += (_, _) => SaveMacro();
        _btnDelete.Click += (_, _) => DeleteSelected();
        _btnClear.Click += (_, _) => ClearMacro();
        _btnSettings.Click += (_, _) => OpenSettings();
        _chkMoves.CheckedChanged += (_, _) => _recordMoves = _chkMoves.Checked;
        _chkTopMost.CheckedChanged += (_, _) => TopMost = _chkTopMost.Checked;

        _hooks.KeyboardInput = OnKeyboard;
        _hooks.MouseInput = OnMouse;
        _player.Finished += () =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(OnPlaybackFinished);
        };

        _refreshTimer.Tick += (_, _) => RefreshView();
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        UpdateUi();
    }

    private static Label Label(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 2, 0) };

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            _hooks.Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not install input hooks:\n" + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        _refreshTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscard("exit")) { e.Cancel = true; return; }
        _player.Stop();
        EndCountdown();
        _refreshTimer.Stop();
        _hooks.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- Hook handlers

    private bool OnKeyboard(KeyInput k)
    {
        if (k.FromPlayback) return false;

        var key = (Keys)k.Vk;
        if (!k.Down && _hotkeysHeld.Remove(key)) return true; // release of a hotkey we swallowed

        if (k.Down && !_hotkeysSuspended)
        {
            if (_hotkeysHeld.Contains(key)) return true; // auto-repeat while the hotkey is held

            Keys pressed = key | CurrentModifiers();
            Action? action = pressed == _settings.RecordHotkey ? ToggleRecord
                : pressed == _settings.PlayHotkey ? TogglePlay
                : null;
            if (action != null)
            {
                _hotkeysHeld.Add(key);
                if ((pressed & Keys.Alt) != 0) Player.SendMenuMask();
                BeginInvoke(action);
                return true; // swallow hotkeys so they never reach other apps or the recording
            }
        }

        if (_recording && !IsOwnWindow(Native.GetForegroundWindow()))
        {
            // Skip releases of keys that were already down when recording started (e.g. the hotkey's Ctrl).
            if (k.Down) _keysDownInRecording.Add(k.Vk);
            else if (!_keysDownInRecording.Remove(k.Vk)) return false;

            AddEvent(new MacroEvent
            {
                Kind = k.Down ? EventKind.KeyDown : EventKind.KeyUp,
                VirtualKey = k.Vk,
                ScanCode = k.Scan,
                Extended = k.Extended,
            });
        }
        return false;
    }

    private void OnMouse(MouseInput m)
    {
        if (!_recording || m.FromPlayback) return;
        if (m.Message == Native.WM_MOUSEMOVE &&
            (!_recordMoves || _recordClock.Elapsed.TotalMilliseconds - _lastEventMs < MinMoveIntervalMs))
            return;
        // Don't record interaction with this window (e.g. clicking the Stop button).
        if (IsOwnWindow(Native.WindowFromPoint(new Native.POINT { X = m.X, Y = m.Y }))) return;

        var xButton = (m.MouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2;
        short wheel = unchecked((short)(m.MouseData >> 16));
        MacroEvent? e = m.Message switch
        {
            Native.WM_MOUSEMOVE => new() { Kind = EventKind.MouseMove },
            Native.WM_LBUTTONDOWN => new() { Kind = EventKind.MouseDown, Button = MouseButton.Left },
            Native.WM_LBUTTONUP => new() { Kind = EventKind.MouseUp, Button = MouseButton.Left },
            Native.WM_RBUTTONDOWN => new() { Kind = EventKind.MouseDown, Button = MouseButton.Right },
            Native.WM_RBUTTONUP => new() { Kind = EventKind.MouseUp, Button = MouseButton.Right },
            Native.WM_MBUTTONDOWN => new() { Kind = EventKind.MouseDown, Button = MouseButton.Middle },
            Native.WM_MBUTTONUP => new() { Kind = EventKind.MouseUp, Button = MouseButton.Middle },
            Native.WM_XBUTTONDOWN => new() { Kind = EventKind.MouseDown, Button = xButton },
            Native.WM_XBUTTONUP => new() { Kind = EventKind.MouseUp, Button = xButton },
            Native.WM_MOUSEWHEEL => new() { Kind = EventKind.MouseWheel, WheelDelta = wheel },
            Native.WM_MOUSEHWHEEL => new() { Kind = EventKind.MouseHWheel, WheelDelta = wheel },
            _ => null,
        };
        if (e == null) return;
        e.X = m.X;
        e.Y = m.Y;
        AddEvent(e);
    }

    private void AddEvent(MacroEvent e)
    {
        double now = _recordClock.Elapsed.TotalMilliseconds;
        e.DelayMs = Math.Round(now - _lastEventMs, 3);
        _lastEventMs = now;
        _events.Add(e);
        _dirty = true;
    }

    private static Keys CurrentModifiers()
    {
        static bool Down(Keys k) => (Native.GetAsyncKeyState((int)k) & 0x8000) != 0;
        var mods = Keys.None;
        if (Down(Keys.ControlKey)) mods |= Keys.Control;
        if (Down(Keys.ShiftKey)) mods |= Keys.Shift;
        if (Down(Keys.Menu)) mods |= Keys.Alt;
        return mods;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    // ---------------------------------------------------------------- Record / play

    private void ToggleRecord()
    {
        if (_player.IsPlaying) return;
        if (CountingDown)
        {
            EndCountdown();
            if (_chkMinimize.Checked) WindowState = FormWindowState.Normal;
        }
        else if (_recording)
        {
            _recording = false;
            _recordClock.Stop();
            TrimHotkeyModifiers();
            if (_chkMinimize.Checked) WindowState = FormWindowState.Normal;
        }
        else
        {
            if (_events.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    "Append to the current macro?\n\nYes = append, No = replace it with a new recording.",
                    Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.No) _events.Clear();
            }
            if (_chkMinimize.Checked) WindowState = FormWindowState.Minimized;
            if (_settings.CountdownSeconds > 0) StartCountdown();
            else BeginRecording();
        }
        UpdateUi();
    }

    private void StartCountdown()
    {
        _countdownRemaining = _settings.CountdownSeconds;
        _overlay = new CountdownOverlay($"Recording starts…  {AppSettings.HotkeyText(_settings.RecordHotkey)} to cancel")
        {
            Seconds = _countdownRemaining,
        };
        _overlay.Show();
        _countdownTimer.Start();
    }

    private void OnCountdownTick()
    {
        if (!CountingDown) return;
        _countdownRemaining--;
        if (_countdownRemaining > 0)
        {
            _overlay!.Seconds = _countdownRemaining;
            RefreshView();
            return;
        }
        EndCountdown();
        BeginRecording();
        UpdateUi();
    }

    private void EndCountdown()
    {
        _countdownTimer.Stop();
        _overlay?.Close();
        _overlay?.Dispose();
        _overlay = null;
    }

    private void BeginRecording()
    {
        _keysDownInRecording.Clear();
        _recordClock.Restart();
        _lastEventMs = 0;
        _recording = true;
    }

    /// <summary>
    /// Removes key-downs for modifiers still held when recording stopped — they were pressed as part of
    /// a modifier hotkey (e.g. the Ctrl in Ctrl+F9), not as part of the macro.
    /// </summary>
    private void TrimHotkeyModifiers()
    {
        double endMs = _recordClock.Elapsed.TotalMilliseconds;
        foreach (int vk in _keysDownInRecording.Where(vk => AppSettings.IsModifierKey((Keys)vk)).ToList())
        {
            // Walk back from the end, collecting this key's (auto-repeated) downs since its last release.
            var downs = new List<int>();
            double elapsed = endMs - _lastEventMs;
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                if (e.VirtualKey == vk && e.Kind == EventKind.KeyUp) break;
                if (e.VirtualKey == vk && e.Kind == EventKind.KeyDown) downs.Add(i);
                elapsed += e.DelayMs;
                if (elapsed > HotkeyModifierWindowMs) { downs.Clear(); break; } // held deliberately; keep it
            }
            foreach (int i in downs) RemoveEventAt(i); // collected in descending order, so indices stay valid
        }
        _keysDownInRecording.Clear();
    }

    private void TogglePlay()
    {
        if (_recording || CountingDown) return;
        if (_player.IsPlaying)
        {
            _player.Stop();
            return;
        }
        if (_events.Count == 0) return;
        if (_chkMinimize.Checked) WindowState = FormWindowState.Minimized;
        _player.Start(_events, (double)_speed.Value, (int)_repeat.Value);
        UpdateUi();
    }

    private void OnPlaybackFinished()
    {
        if (_chkMinimize.Checked) WindowState = FormWindowState.Normal;
        UpdateUi();
    }

    private void OpenSettings()
    {
        // Hotkeys must reach the dialog's capture boxes instead of triggering.
        _hotkeysSuspended = true;
        try
        {
            using var dialog = new SettingsForm(_settings);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _hotkeysSuspended = false;
            UpdateUi();
        }
    }

    // ---------------------------------------------------------------- Editing / files

    private void RemoveEventAt(int i)
    {
        // Fold the removed event's delay into the next one so later events keep their timing.
        if (i + 1 < _events.Count) _events[i + 1].DelayMs += _events[i].DelayMs;
        _events.RemoveAt(i);
    }

    private void DeleteSelected()
    {
        if (_recording || _player.IsPlaying || _list.SelectedIndices.Count == 0) return;
        foreach (int i in _list.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList())
            RemoveEventAt(i);
        _list.SelectedIndices.Clear();
        _dirty = true;
        UpdateUi();
    }

    private void ClearMacro()
    {
        if (!ConfirmDiscard("clear it")) return;
        _events.Clear();
        _currentFile = null;
        _dirty = false;
        UpdateUi();
    }

    private void SaveMacro()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Macro files (*.macro.json)|*.macro.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = _currentFile != null ? Path.GetFileName(_currentFile) : "macro.macro.json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            new Macro { Events = _events.ToList() }.Save(dialog.FileName);
            _currentFile = dialog.FileName;
            _dirty = false;
            UpdateUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadMacro()
    {
        if (!ConfirmDiscard("open another")) return;
        using var dialog = new OpenFileDialog
        {
            Filter = "Macro files (*.macro.json;*.json)|*.macro.json;*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var macro = Macro.Load(dialog.FileName);
            _events.Clear();
            _events.AddRange(macro.Events);
            _currentFile = dialog.FileName;
            _dirty = false;
            UpdateUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ConfirmDiscard(string action)
    {
        if (!_dirty || _events.Count == 0) return true;
        return MessageBox.Show(this, $"The current macro has unsaved changes. Discard them and {action}?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    // ---------------------------------------------------------------- View

    private ListViewItem BuildItem(int index)
    {
        if (index >= _events.Count) return new ListViewItem(new[] { "", "", "", "" });
        var e = _events[index];
        return new ListViewItem(new[]
        {
            (index + 1).ToString(),
            e.DelayMs.ToString("0.0"),
            e.Kind.ToString(),
            e.Describe(),
        });
    }

    private void UpdateUi()
    {
        bool busy = _recording || _player.IsPlaying || CountingDown;
        string rec = AppSettings.HotkeyText(_settings.RecordHotkey);
        string play = AppSettings.HotkeyText(_settings.PlayHotkey);
        _btnRecord.Text = CountingDown ? $"✕ Cancel countdown ({rec})"
            : _recording ? $"■ Stop recording ({rec})"
            : $"● Record ({rec})";
        _btnRecord.Enabled = !_player.IsPlaying;
        _btnPlay.Text = _player.IsPlaying ? $"■ Stop ({play})" : $"▶ Play ({play})";
        _btnPlay.Enabled = !_recording && !CountingDown && (_player.IsPlaying || _events.Count > 0);
        _btnLoad.Enabled = _btnSave.Enabled = _btnDelete.Enabled = _btnClear.Enabled = _btnSettings.Enabled = !busy;
        _btnSave.Enabled &= _events.Count > 0;
        _chkMoves.Enabled = !_recording;
        Text = "Macro Recorder" + (_currentFile != null ? " — " + Path.GetFileName(_currentFile) : "") + (_dirty ? " *" : "");
        RefreshView();
    }

    private void RefreshView()
    {
        if (_list.VirtualListSize != _events.Count)
        {
            _list.VirtualListSize = _events.Count;
            if (_recording && _events.Count > 0) _list.EnsureVisible(_events.Count - 1);
        }

        string rec = AppSettings.HotkeyText(_settings.RecordHotkey);
        string play = AppSettings.HotkeyText(_settings.PlayHotkey);
        double totalSec = _events.Sum(e => e.DelayMs) / 1000.0;
        if (CountingDown)
        {
            _status.Text = $"Recording starts in {_countdownRemaining}…  —  press {rec} to cancel";
        }
        else if (_recording)
        {
            _status.Text = $"● Recording… {_events.Count} events, {_recordClock.Elapsed.TotalSeconds:0.0}s  —  press {rec} to stop";
        }
        else if (_player.IsPlaying)
        {
            string loops = _repeat.Value == 0 ? "∞" : _repeat.Value.ToString();
            _status.Text = $"▶ Playing loop {_player.CurrentLoop}/{loops}, event {_player.CurrentIndex}/{_events.Count}  —  press {play} to stop";
        }
        else
        {
            _status.Text = $"Ready. {_events.Count} events, {totalSec:0.0}s long.  {rec} = record, {play} = play/stop.";
        }
    }
}
