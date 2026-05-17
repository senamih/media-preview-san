namespace MediaPreviewSan;

internal sealed class BusyDialog : Form
{
    private readonly Label _label;
    private readonly ProgressBar _bar;
    private DateTime _stageShownAt = DateTime.UtcNow;

    /// <summary>各ステータス（メッセージ）の最低表示時間。</summary>
    public int MinimumStageMs { get; set; } = 1000;

    public BusyDialog(string message)
    {
        Text = "MediaPreviewSan";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 96);
        BackColor = Color.White;
        DoubleBuffered = true;
        Font = SystemFonts.MessageBoxFont ?? Font;

        _label = new Label
        {
            Text = message,
            Location = new Point(16, 16),
            Size = new Size(288, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
        };
        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Location = new Point(24, 52),
            Size = new Size(272, 18),
        };
        Controls.AddRange(new Control[] { _label, _bar });
    }

    public void SetMessage(string message)
    {
        if (_label.Text != message) _label.Text = message;
        _stageShownAt = DateTime.UtcNow;
    }

    /// <summary>現ステータスを最低 MinimumStageMs 表示してから次メッセージに切り替える。</summary>
    public async Task SetMessageAfterMinimumAsync(string message)
    {
        await WaitMinimumAsync();
        if (!IsDisposed && _label.Text != message) _label.Text = message;
        _stageShownAt = DateTime.UtcNow;
    }

    /// <summary>現ステータスを最低 MinimumStageMs 表示してから閉じる。</summary>
    public async Task CloseAfterMinimumAsync()
    {
        await WaitMinimumAsync();
        if (!IsDisposed)
        {
            try { Close(); Dispose(); } catch { }
        }
    }

    private async Task WaitMinimumAsync()
    {
        var elapsed = (DateTime.UtcNow - _stageShownAt).TotalMilliseconds;
        if (elapsed < MinimumStageMs)
        {
            await Task.Delay(MinimumStageMs - (int)elapsed);
        }
    }

    protected override bool ShowWithoutActivation => false;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            const int CS_DROPSHADOW = 0x00020000;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }
}
