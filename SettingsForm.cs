namespace MacroRecorder;

/// <summary>Text box that captures a key combination instead of text.</summary>
internal sealed class HotkeyBox : TextBox
{
    private Keys _hotkey;

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
        ShortcutsEnabled = false;
    }

    public Keys Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; Text = AppSettings.HotkeyText(value); }
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        BeginInvoke(() => SelectionLength = 0); // no highlighted text; it's a capture box, not an editor
    }

    // Let every key except Tab reach OnKeyDown, so Enter, Esc, arrows etc. can be captured.
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) != Keys.Tab || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (AppSettings.IsModifierKey(e.KeyCode))
        {
            // Show the modifiers held so far while waiting for the main key.
            Text = AppSettings.HotkeyText(e.Modifiers) + "+…";
            return;
        }
        Hotkey = e.KeyCode | e.Modifiers;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        if (Text.EndsWith("…")) Text = AppSettings.HotkeyText(_hotkey); // released modifiers without a key
    }
}

internal sealed class SettingsForm : Form
{
    private readonly HotkeyBox _record = new() { Width = 180 };
    private readonly HotkeyBox _play = new() { Width = 180 };
    private readonly NumericUpDown _countdown = new() { Minimum = 0, Maximum = 60, Width = 70 };

    public SettingsForm(AppSettings settings)
    {
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _record.Hotkey = settings.RecordHotkey;
        _play.Hotkey = settings.PlayHotkey;
        _countdown.Value = Math.Clamp(settings.CountdownSeconds, 0, 60);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
            grid.Controls.Add(control);
        }
        Row("Record / stop recording:", _record);
        Row("Play / stop playback:", _play);
        Row("Countdown before recording (s):", _countdown);

        var hint = new Label
        {
            Text = "Click a hotkey box and press the key combination (Ctrl, Alt and Shift are allowed).\nCountdown 0 = start recording immediately.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 8),
        };
        grid.Controls.Add(hint);
        grid.SetColumnSpan(hint, 2);

        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var reset = new Button { Text = "Defaults", AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok, reset });
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);

        // No AcceptButton: Enter must be capturable as a hotkey.
        CancelButton = cancel;
        reset.Click += (_, _) =>
        {
            var defaults = new AppSettings();
            _record.Hotkey = defaults.RecordHotkey;
            _play.Hotkey = defaults.PlayHotkey;
            _countdown.Value = defaults.CountdownSeconds;
        };
        ok.Click += (_, _) =>
        {
            if (_record.Hotkey == _play.Hotkey)
            {
                MessageBox.Show(this, "The two hotkeys must be different.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            settings.RecordHotkey = _record.Hotkey;
            settings.PlayHotkey = _play.Hotkey;
            settings.CountdownSeconds = (int)_countdown.Value;
            DialogResult = DialogResult.OK;
        };
    }
}
