namespace MediaPreviewSan;

public class StatusOverlay : Panel
{
    private readonly Label _label;
    private readonly ProgressBar _bar;

    public StatusOverlay()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        ForeColor = Color.Gainsboro;
        Visible = false;

        _label = new Label
        {
            AutoSize = false,
            ForeColor = Color.Gainsboro,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Regular),
            Text = "",
        };
        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
        };

        Controls.Add(_bar);
        Controls.Add(_label);

        Resize += (_, _) => LayoutChildren();
        LayoutChildren();
    }

    public void ShowMarquee(string message)
    {
        _label.Text = message;
        _bar.Visible = true;
        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 25;
        Visible = true;
        BringToFront();
        LayoutChildren();
    }

    public void ShowStatic(string message)
    {
        _label.Text = message;
        _bar.Visible = false;
        _bar.MarqueeAnimationSpeed = 0;
        Visible = true;
        BringToFront();
        LayoutChildren();
    }

    public new void Hide()
    {
        Visible = false;
        _bar.MarqueeAnimationSpeed = 0;
    }

    public void SetMessage(string message)
    {
        if (_label.Text != message) _label.Text = message;
    }

    private void LayoutChildren()
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        int hPadding = 24;
        int barW = Math.Min(260, Math.Max(120, w - 80));
        int barH = 12;

        int labelW = Math.Max(80, w - hPadding * 2);
        int labelH = Math.Min(Math.Max(60, h - 80), Math.Max(60, h * 2 / 3));

        bool showBar = _bar.Visible;
        int totalH = labelH + (showBar ? (8 + barH) : 0);
        int top = Math.Max(8, (h - totalH) / 2);

        _label.Bounds = new Rectangle((w - labelW) / 2, top, labelW, labelH);
        if (showBar)
        {
            _bar.Bounds = new Rectangle((w - barW) / 2, top + labelH + 8, barW, barH);
        }
    }
}
