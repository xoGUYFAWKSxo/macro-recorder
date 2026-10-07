namespace MacroRecorder;

/// <summary>
/// Large always-on-top countdown number. Click-through and never takes focus, so the user can
/// keep working in the target app while it counts down.
/// </summary>
internal sealed class CountdownOverlay : Form
{
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Label _label = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 72, FontStyle.Bold),
    };
    private readonly Label _caption = new()
    {
        Dock = DockStyle.Bottom,
        Height = 34,
        TextAlign = ContentAlignment.TopCenter,
        ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 10),
    };

    public CountdownOverlay(string caption)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.85;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(240, 220);
        _caption.Text = caption;
        Controls.Add(_label);
        Controls.Add(_caption);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Centre (after DPI scaling) on the monitor the mouse is on — where the user is most likely looking.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    public int Seconds
    {
        set => _label.Text = value.ToString();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}
