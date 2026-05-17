namespace MediaPreviewSan;

public class SettingsForm : Form
{
    private record ScalingOption(string Label, string Value);

    private static readonly ScalingOption[] ScalingOptions =
    {
        new("バイリニア：双線形：既定・滑らか", "Bilinear"),
        new("ニアレストネイバー：最近傍：ドット感を維持", "Point"),
        new("アニソトロピック：異方性：高品質・対応GPUのみ", "Anisotropic"),
    };

    private readonly AppSettings _settings;
    private readonly ComboBox _deviceCombo = new();
    private readonly ComboBox _formatCombo = new();
    private readonly ComboBox _scalingCombo = new();
    private readonly CheckBox _aspectCheck = new();
    private readonly Button _refreshBtn = new();
    private readonly Button _okBtn = new();
    private readonly Button _cancelBtn = new();
    private readonly Label _statusLabel = new();
    private readonly Label _deviceCountLabel = new();
    private readonly LinkLabel _versionLabel = new();

    private const string RepositoryUrl = "https://github.com/senamih/media-preview-san";
    private readonly StatusOverlay _overlay = new();

    // 復元基準値（初回ロードは保存設定、再検出時は再検出前の UI 選択）
    private string _restoreDevId = "";
    private CaptureFormat? _restoreFormat;
    private string _restoreScaling = "Bilinear";
    private bool _restoreAspect = true;

    private List<DeviceInfo> _devices = new();
    private List<CaptureFormat> _formats = new();
    private bool _suppressDeviceChange;
    private BusyDialog? _busyDialog;
    // 確認用最小表示時間。機能は維持しつつ現在は 0（待機なし）。
    private const int BusyMinimumDisplayMs = 0;

    private readonly string _origDeviceId;
    private readonly int _origW, _origH;
    private readonly double _origFps;
    private readonly string _origScaling;
    private readonly bool _origAspect;

    /// <summary>Windows のタスク終了要求で、変更なしのため閉じた場合 true。</summary>
    public bool ShutdownRequested { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _origDeviceId = settings.DeviceMonikerId ?? "";
        _origW = settings.CaptureWidth;
        _origH = settings.CaptureHeight;
        _origFps = settings.CaptureFps;
        _origScaling = settings.ScalingMode ?? "Bilinear";
        _origAspect = settings.MaintainAspectRatio;

        SetStyle(ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        InitializeUi();
        FormClosing += OnFormClosingHandler;
    }

    private bool HasChanges()
    {
        int idx = _deviceCombo.SelectedIndex;
        string dev = (idx >= 0 && idx < _devices.Count) ? _devices[idx].PersistentId : "";
        int w = 0, h = 0; double fps = 0;
        int fIdx = _formatCombo.SelectedIndex;
        if (fIdx > 0 && fIdx - 1 < _formats.Count)
        {
            var f = _formats[fIdx - 1];
            w = f.Width; h = f.Height; fps = f.Fps;
        }
        int sIdx = _scalingCombo.SelectedIndex;
        string scaling = (sIdx >= 0 && sIdx < ScalingOptions.Length)
            ? ScalingOptions[sIdx].Value : "Bilinear";

        return !string.Equals(dev, _origDeviceId, StringComparison.OrdinalIgnoreCase)
            || w != _origW || h != _origH || Math.Abs(fps - _origFps) > 0.01
            || !string.Equals(scaling, _origScaling, StringComparison.OrdinalIgnoreCase)
            || _aspectCheck.Checked != _origAspect;
    }

    private void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.TaskManagerClosing
            or CloseReason.WindowsShutDown
            or CloseReason.ApplicationExitCall)
        {
            if (HasChanges())
            {
                // 変更があるので終了をブロック（ユーザの保存判断を待つ）
                e.Cancel = true;
            }
            else
            {
                // 変更なし → キャンセル扱いで設定画面を閉じ、アプリ終了へ
                ShutdownRequested = true;
                DialogResult = DialogResult.Cancel;
            }
        }
    }

    private const int WM_SETREDRAW = 0x000B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);

    private void SuspendRedraw()
    {
        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    private void ResumeRedraw()
    {
        SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        Invalidate(true);
    }

    private void InitializeUi()
    {
        Text = "MediaPreviewSan - 設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 320);
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont ?? Font;

        const int labelLeft = 12;
        const int controlLeft = 122;
        const int labelW = 100;
        const int rowH = 28;
        const int rowGap = 8;
        const int controlH = 24;

        int row1Y = 14;
        int deviceCountY = row1Y + controlH + 2;          // デバイス一覧の直下
        int row2Y = deviceCountY + 18 + rowGap;
        int row3Y = row2Y + rowH + rowGap;
        int row4Y = row3Y + rowH + rowGap;

        var lblDevice = new Label
        {
            Text = "入力デバイス:",
            Location = new Point(labelLeft, row1Y),
            Size = new Size(labelW, controlH),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _deviceCombo.Location = new Point(controlLeft, row1Y);
        _deviceCombo.Size = new Size(300, controlH);
        _deviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceCombo.SelectedIndexChanged += async (_, _) =>
        {
            if (_suppressDeviceChange) return;
            UpdateDeviceNote();
            await LoadFormatsForSelectedDeviceAsync();
        };

        _refreshBtn.Text = "再検出";
        _refreshBtn.Location = new Point(controlLeft + 306, row1Y - 1);
        _refreshBtn.Size = new Size(86, 26);
        _refreshBtn.Click += async (_, _) => await LoadDevicesAsync(forceRefresh: true);

        var lblFormat = new Label
        {
            Text = "解像度/FPS:",
            Location = new Point(labelLeft, row2Y),
            Size = new Size(labelW, controlH),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _formatCombo.Location = new Point(controlLeft, row2Y);
        _formatCombo.Size = new Size(392, controlH);
        _formatCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        var lblScaling = new Label
        {
            Text = "拡大時の補間:",
            Location = new Point(labelLeft, row3Y),
            Size = new Size(labelW, controlH),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _scalingCombo.Location = new Point(controlLeft, row3Y);
        _scalingCombo.Size = new Size(392, controlH);
        _scalingCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var opt in ScalingOptions) _scalingCombo.Items.Add(opt.Label);
        int scalingIdx = Array.FindIndex(ScalingOptions,
            o => string.Equals(o.Value, _settings.ScalingMode, StringComparison.OrdinalIgnoreCase));
        _scalingCombo.SelectedIndex = scalingIdx >= 0 ? scalingIdx : 0;

        _aspectCheck.Text = "ウィンドウ拡縮時にアスペクト比を維持";
        _aspectCheck.Location = new Point(controlLeft, row4Y);
        _aspectCheck.Size = new Size(392, controlH);
        _aspectCheck.Checked = _settings.MaintainAspectRatio;

        // 既定で無効。MF デバイスが確定したときだけ UpdateDeviceNote が有効化する。
        // これにより DS-only 復元時に一瞬でも有効に見えるのを防ぐ。
        _formatCombo.Enabled = false;
        _scalingCombo.Enabled = false;
        _aspectCheck.Enabled = false;

        // デバイス一覧の直下に「N 件のデバイスを検出しました」を配置
        _deviceCountLabel.Location = new Point(controlLeft, deviceCountY);
        _deviceCountLabel.Size = new Size(402, 18);
        _deviceCountLabel.ForeColor = Color.DimGray;
        _deviceCountLabel.AutoEllipsis = true;
        _deviceCountLabel.TextAlign = ContentAlignment.MiddleLeft;

        _statusLabel.Location = new Point(12, row4Y + rowH + rowGap + 4);
        _statusLabel.Size = new Size(516, 36);
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.AutoEllipsis = true;

        int btnY = ClientSize.Height - 30 - 14;

        // 画面下部の左側（ボタンと同じ高さ）にバージョン情報を表示。
        // クリックで GitHub リポジトリを既定ブラウザで開く。
        _versionLabel.AutoSize = true;
        _versionLabel.Text = $"MediaPreviewSan v{GetAppVersion()}";
        _versionLabel.Location = new Point(12, btnY + 8);
        _versionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _versionLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        // 既定の見た目（下線つき青色）で常時表示する。
        _versionLabel.LinkBehavior = LinkBehavior.AlwaysUnderline;
        _versionLabel.LinkArea = new LinkArea(0, _versionLabel.Text.Length);
        _versionLabel.LinkClicked += OnVersionLinkClicked;

        _okBtn.Text = "OK";
        _okBtn.DialogResult = DialogResult.OK;
        _okBtn.Size = new Size(96, 30);
        _okBtn.Location = new Point(332, btnY);
        _okBtn.Click += OnOk;

        _cancelBtn.Text = "キャンセル";
        _cancelBtn.DialogResult = DialogResult.Cancel;
        _cancelBtn.Size = new Size(96, 30);
        _cancelBtn.Location = new Point(432, btnY);

        Controls.AddRange(new Control[]
        {
            lblDevice, _deviceCombo, _refreshBtn,
            _deviceCountLabel,
            lblFormat, _formatCombo,
            lblScaling, _scalingCombo,
            _aspectCheck,
            _statusLabel,
            _versionLabel,
            _okBtn, _cancelBtn,
        });

        _overlay.BackColor = Color.White;
        _overlay.ForeColor = Color.Black;
        _overlay.Dock = DockStyle.Fill;
        Controls.Add(_overlay);

        // Dock.Fill の _overlay より前面に出し、隠れないようにする
        _versionLabel.BringToFront();

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 設定画面を開くだけでは再検出しない（キャッシュ利用）。再検出はボタンのみ。
        await LoadDevicesAsync(forceRefresh: false);
    }

    private System.Windows.Forms.Timer? _busyDelayTimer;
    private string _pendingBusyMessage = "";
    private bool _busy;

    private void ShowBusy(string message)
    {
        _deviceCombo.Enabled = false;
        _formatCombo.Enabled = false;
        _scalingCombo.Enabled = false;
        _refreshBtn.Enabled = false;
        _okBtn.Enabled = false;
        _cancelBtn.Enabled = false;
        _aspectCheck.Enabled = false;

        _busy = true;
        _pendingBusyMessage = message;

        if (_busyDialog != null && !_busyDialog.IsDisposed)
        {
            _busyDialog.SetMessage(message);
            CenterBusyDialog();
            return;
        }

        // 0.5s 経過後もまだ処理中の時だけダイアログを出す（高速時は出さない）
        _busyDelayTimer ??= new System.Windows.Forms.Timer { Interval = 500 };
        _busyDelayTimer.Stop();
        _busyDelayTimer.Tick -= OnBusyDelayTick;
        _busyDelayTimer.Tick += OnBusyDelayTick;
        _busyDelayTimer.Start();
    }

    private void OnBusyDelayTick(object? sender, EventArgs e)
    {
        _busyDelayTimer?.Stop();
        if (!_busy) return;
        if (_busyDialog == null || _busyDialog.IsDisposed)
        {
            _busyDialog = new BusyDialog(_pendingBusyMessage) { MinimumStageMs = BusyMinimumDisplayMs };
            _busyDialog.StartPosition = FormStartPosition.Manual;
            CenterBusyDialog();   // Show 前に位置確定（左上チラ見防止）
            _busyDialog.Show(this);
        }
    }

    /// <summary>現ステータスを最低時間表示してから次メッセージへ（ダイアログは閉じない）。</summary>
    private async Task SetBusyMessageAsync(string message)
    {
        _pendingBusyMessage = message;
        if (_busyDialog != null && !_busyDialog.IsDisposed)
        {
            await _busyDialog.SetMessageAfterMinimumAsync(message);
            CenterBusyDialog();
        }
    }

    private async Task HideBusyAsync()
    {
        _busy = false;
        _busyDelayTimer?.Stop();

        if (_busyDialog != null && !_busyDialog.IsDisposed)
        {
            await _busyDialog.CloseAfterMinimumAsync();
            _busyDialog = null;
        }

        _deviceCombo.Enabled = true;
        _refreshBtn.Enabled = true;
        _okBtn.Enabled = true;
        _cancelBtn.Enabled = true;
        // format/scaling/aspect は UpdateDeviceNote が MF/DS に応じて制御するので
        // ここでは一律 Enable しない。
        UpdateDeviceNote();
    }

    private void CenterBusyDialog()
    {
        if (_busyDialog == null) return;
        // 設定画面の中心に表示（CenterParent では Show 前の位置調整が効かないので手動）
        int x = Bounds.Left + (Bounds.Width - _busyDialog.Width) / 2;
        int y = Bounds.Top + (Bounds.Height - _busyDialog.Height) / 2;
        _busyDialog.Location = new Point(x, y);
    }

    private async Task LoadDevicesAsync(bool forceRefresh)
    {
        // 復元基準を確定する。再検出時は「再検出前の UI 選択」を保持して
        // 再列挙後に戻す（保存設定に巻き戻らないようにする）。初回ロードは保存設定。
        if (forceRefresh && _devices.Count > 0)
        {
            int ci = _deviceCombo.SelectedIndex;
            _restoreDevId = (ci >= 0 && ci < _devices.Count)
                ? _devices[ci].PersistentId : _settings.DeviceMonikerId;
            int fi = _formatCombo.SelectedIndex;
            _restoreFormat = (fi > 0 && fi - 1 < _formats.Count) ? _formats[fi - 1] : null;
            int si = _scalingCombo.SelectedIndex;
            _restoreScaling = (si >= 0 && si < ScalingOptions.Length)
                ? ScalingOptions[si].Value : _settings.ScalingMode;
            _restoreAspect = _aspectCheck.Checked;
        }
        else
        {
            _restoreDevId = _settings.DeviceMonikerId;
            // 保存設定にカラーフォーマットは持たないため SubType は空（後段で
            // 解像度+FPS フォールバック一致になる）。
            _restoreFormat = (_settings.CaptureWidth > 0 && _settings.CaptureHeight > 0)
                ? new CaptureFormat
                {
                    Width = _settings.CaptureWidth,
                    Height = _settings.CaptureHeight,
                    Fps = _settings.CaptureFps,
                    SubTypeName = "",
                }
                : null;
            _restoreScaling = _settings.ScalingMode;
            _restoreAspect = _settings.MaintainAspectRatio;
        }

        ShowBusy(forceRefresh ? "デバイスを再検出中..." : "デバイス一覧を読み込み中...");
        _statusLabel.Text = "";
        try
        {
            var devices = await Task.Run(() => DeviceCache.Get(forceRefresh));
            _devices = devices;

            _suppressDeviceChange = true;
            try
            {
                _deviceCombo.BeginUpdate();
                _deviceCombo.Items.Clear();
                foreach (var d in _devices)
                {
                    string label = d.IsDirectShowOnly ? $"{d.Name} [DirectShow(レガシー)]" : d.Name;
                    _deviceCombo.Items.Add(label);
                }
                _deviceCombo.EndUpdate();

                _formatCombo.Items.Clear();
                _formats.Clear();

                if (_devices.Count == 0)
                {
                    _statusLabel.Text = "入力デバイスが見つかりません。\n"
                        + "Webカメラが見つからない場合は、Windows設定 → プライバシーとセキュリティ → "
                        + "カメラ → 「デスクトップアプリにカメラへのアクセスを許可する」が ON か確認してください。";
                    return;
                }

                // PersistentId（MF=SymbolicLink / DS=DevicePath）で復元。
                // SymbolicLink 比較だと DS-only デバイスが復元できず先頭(MF)に化けて
                // コントロールが有効のままになっていた。
                int idx = -1;
                if (!string.IsNullOrEmpty(_restoreDevId))
                {
                    idx = _devices.FindIndex(d =>
                        string.Equals(d.PersistentId, _restoreDevId, StringComparison.OrdinalIgnoreCase));
                }
                if (idx < 0 && !string.IsNullOrEmpty(_settings.DeviceName))
                {
                    idx = _devices.FindIndex(d => d.Name == _settings.DeviceName);
                }
                _deviceCombo.SelectedIndex = idx >= 0 ? idx : 0;
                _deviceCountLabel.Text = $"{_devices.Count} 件のデバイスを検出しました。";
                UpdateDeviceNote();

                // suppress を維持したまま解像度・補間・アス比を _settings から復元する。
                // 先に suppress=false に戻すと、LoadFormats の await 中に遅延発火した
                // _deviceCombo.SelectedIndexChanged が並行して LoadFormats を起動し、
                // 復元結果を上書きしてしまう（再検出時の復元が効かない原因）。
                await LoadFormatsForSelectedDeviceAsync(showBusy: false, forceRefresh: forceRefresh);
            }
            finally
            {
                _suppressDeviceChange = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"LoadDevices failed: {ex.Message}");
            _statusLabel.Text = $"デバイス列挙失敗: {ex.Message}";
        }
        finally
        {
            await HideBusyAsync();
        }
    }

    private async Task LoadFormatsForSelectedDeviceAsync(bool showBusy = true, bool forceRefresh = false)
    {
        int idx = _deviceCombo.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count)
        {
            _formatCombo.Items.Clear();
            _formats.Clear();
            return;
        }

        var selectedDevice = _devices[idx];

        UpdateDeviceNote();

        bool isDs = selectedDevice.IsDirectShowOnly;
        string id = isDs ? selectedDevice.DirectShowDevicePath : selectedDevice.SymbolicLink;

        if (showBusy)
            ShowBusy("解像度情報を取得中...");
        else
            await SetBusyMessageAsync("解像度情報を取得中..."); // 前段を最低1s表示してから推移

        try
        {
            var formats = await Task.Run(() => DeviceCache.GetFormats(id, forceRefresh, isDs));
            _formats = formats;

            string autoLabel = _formats.Count > 0
                ? $"(自動 - 最高画質: {_formats[0]})"
                : "(デバイス既定)";

            _formatCombo.BeginUpdate();
            _formatCombo.Items.Clear();
            _formatCombo.Items.Add(autoLabel);
            foreach (var f in _formats) _formatCombo.Items.Add(f.ToString());
            _formatCombo.EndUpdate();

            int selIdx = 0;
            if (_restoreFormat is { } rf && rf.Width > 0 && rf.Height > 0)
            {
                // 1) 完全一致（解像度 + FPS(±0.01, 小数も区別) + カラーフォーマット）
                //    CaptureFormat.Equals が W/H一致・Abs(Fps差)<0.01・SubType一致を判定。
                int found = _formats.FindIndex(f => f.Equals(rf));
                // 2) 解像度 + FPS（±0.05）一致（SubType 不問）
                if (found < 0)
                {
                    found = _formats.FindIndex(f =>
                        f.Width == rf.Width && f.Height == rf.Height
                        && Math.Abs(f.Fps - rf.Fps) < 0.05);
                }
                // 3) 解像度のみ一致
                if (found < 0)
                {
                    found = _formats.FindIndex(f =>
                        f.Width == rf.Width && f.Height == rf.Height);
                }
                if (found >= 0) selIdx = found + 1;
            }
            _formatCombo.SelectedIndex = selIdx;

            // 復元基準（初回=保存設定／再検出=再検出前の選択）に一致する項目が
            // あれば、補間・アス比もその値へ戻す。
            int sIdx = Array.FindIndex(ScalingOptions,
                o => string.Equals(o.Value, _restoreScaling, StringComparison.OrdinalIgnoreCase));
            if (sIdx >= 0) _scalingCombo.SelectedIndex = sIdx;
            _aspectCheck.Checked = _restoreAspect;
        }
        catch (Exception ex)
        {
            Logger.Log($"LoadFormats failed: {ex.Message}");
            _statusLabel.Text = $"解像度列挙失敗: {ex.Message}";
        }
        finally
        {
            if (showBusy) await HideBusyAsync();
        }
    }

    private void UpdateDeviceNote()
    {
        int idx = _deviceCombo.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count) return;
        var device = _devices[idx];
        _deviceCountLabel.Text = $"{_devices.Count} 件のデバイスを検出しました。";
        if (device.IsDirectShowOnly)
        {
            _statusLabel.Text = "取得: DirectShow（レガシー）+ OpenCV ／ 描画: Direct3D11 (GPU)";
            _statusLabel.ForeColor = Color.SaddleBrown;
        }
        else
        {
            _statusLabel.Text = "取得: Media Foundation ／ 描画: Direct3D11 (GPU)";
            _statusLabel.ForeColor = Color.DimGray;
        }
        // DS/MF いずれも解像度・補間・アス比を利用可能
        _formatCombo.Enabled = true;
        _scalingCombo.Enabled = true;
        _aspectCheck.Enabled = true;
    }

    /// <summary>csproj の InformationalVersion（無ければ FileVersion）を取得。+commit 等の付随情報は除去。</summary>
    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string? v = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?
            .InformationalVersion;
        if (!string.IsNullOrEmpty(v))
        {
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
        return asm.GetName().Version?.ToString() ?? "?";
    }

    /// <summary>バージョン表記クリックで GitHub リポジトリを既定ブラウザで開く。</summary>
    private void OnVersionLinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepositoryUrl)
            {
                UseShellExecute = true,
            });
            _versionLabel.LinkVisited = true;
        }
        catch (Exception ex)
        {
            Logger.Log($"リポジトリURLを開けませんでした: {ex.Message}");
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        int idx = _deviceCombo.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count)
        {
            _settings.DeviceMonikerId = "";
            _settings.DeviceName = "";
        }
        else
        {
            // MF 用は SymbolicLink、DS-only は DirectShowDevicePath。PersistentId が両者を抽象化
            _settings.DeviceMonikerId = _devices[idx].PersistentId;
            _settings.DeviceName = _devices[idx].Name;
        }

        int fIdx = _formatCombo.SelectedIndex;
        if (fIdx <= 0 || fIdx - 1 >= _formats.Count)
        {
            // 自動: 0 にしておいて Start で最高画質を選ばせる
            _settings.CaptureWidth = 0;
            _settings.CaptureHeight = 0;
            _settings.CaptureFps = 0;
        }
        else
        {
            var f = _formats[fIdx - 1];
            _settings.CaptureWidth = f.Width;
            _settings.CaptureHeight = f.Height;
            _settings.CaptureFps = f.Fps;
        }

        int sIdx = _scalingCombo.SelectedIndex;
        _settings.ScalingMode = (sIdx >= 0 && sIdx < ScalingOptions.Length)
            ? ScalingOptions[sIdx].Value
            : "Bilinear";

        _settings.MaintainAspectRatio = _aspectCheck.Checked;
        _settings.Save();
        Logger.Log($"settings applied via dialog: device='{_settings.DeviceName}' scaling={_settings.ScalingMode}");
    }
}
