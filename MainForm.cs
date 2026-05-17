using System.Drawing.Drawing2D;

namespace MediaPreviewSan;

public class MainForm : Form
{
    private const string BaseTitle = "MediaPreviewSan";
    private const int SignalWaitPollMs = 250;

    private readonly AppSettings _settings;
    private ICaptureService _capture = new MediaFoundationCaptureService();
    private readonly Panel _videoPanel;
    private readonly StatusOverlay _overlay;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _signalTimer;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly System.Windows.Forms.Timer _saveDebounce;

    private bool _restoringBounds = true;
    private bool _suppressSaveBounds = false;

    public MainForm(AppSettings settings)
    {
        _settings = settings;

        Text = BaseTitle;
        Icon = IconLoader.AppIcon;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(320, 240);
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;

        _videoPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };
        Controls.Add(_videoPanel);

        _overlay = new StatusOverlay { Dock = DockStyle.Fill };
        _videoPanel.Controls.Add(_overlay);

        _contextMenu = new ContextMenuStrip();
        var menuSettings = new ToolStripMenuItem("設定...", BuildSettingsIcon(), (_, _) => OpenSettings());
        var menuReconnect = new ToolStripMenuItem("再接続", null, (_, _) => Reconnect());
        var menuDisconnect = new ToolStripMenuItem("接続解除", null, (_, _) => Disconnect());
        var menuExit = new ToolStripMenuItem("終了", null, (_, _) => Close());
        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            menuSettings,
            menuReconnect,
            menuDisconnect,
            new ToolStripSeparator(),
            menuExit,
        });
        ContextMenuStrip = _contextMenu;
        _videoPanel.ContextMenuStrip = _contextMenu;
        _overlay.ContextMenuStrip = _contextMenu;

        _signalTimer = new System.Windows.Forms.Timer { Interval = SignalWaitPollMs };
        _signalTimer.Tick += OnSignalTick;

        // 描画は Nv12Renderer 内の専用スレッドで行う（UI スレッドからは独立）。
        // このタイマーは実測 FPS をタイトルバーへ反映するために使う（1 秒毎）。
        _renderTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _renderTimer.Tick += (_, _) =>
        {
            if (_capture.IsRunning && _capture.HasFrame) UpdateTitle();
        };
        _renderTimer.Start();

        // ウィンドウ位置/サイズの保存は 500ms デバウンス。
        // 初期化中や移動中の SizeChanged/LocationChanged 連発で書き込みが頻発するのを防ぐ。
        _saveDebounce = new System.Windows.Forms.Timer { Interval = 500 };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveBoundsToSettings();
        };

        _videoPanel.Resize += (_, _) => OnVideoPanelResize();
        Shown += OnFormShown;
        FormClosing += OnFormClosing;
        ResizeEnd += (_, _) => RequestSaveBounds();
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) =>
        {
            if (!_restoringBounds && WindowState == FormWindowState.Normal) RequestSaveBounds();
        };

        RestoreWindowBounds();
        UpdateTitle();
    }

    private void RestoreWindowBounds()
    {
        _restoringBounds = true;
        try
        {
            int w = Math.Max(MinimumSize.Width, _settings.WindowWidth);
            int h = Math.Max(MinimumSize.Height, _settings.WindowHeight);
            Size = new Size(w, h);

            if (_settings.WindowX != -1 && _settings.WindowY != -1)
            {
                var rect = new Rectangle(_settings.WindowX, _settings.WindowY, w, h);
                if (IsRectVisible(rect)) Location = rect.Location;
                else CenterOnExecutionMonitor(w, h);
            }
            else
            {
                // 初回起動: 実行したモニタ（カーソルのある画面）の中央に配置
                CenterOnExecutionMonitor(w, h);
            }

            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }
        finally
        {
            _restoringBounds = false;
        }
    }

    private void CenterOnExecutionMonitor(int w, int h)
    {
        StartPosition = FormStartPosition.Manual;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = area.Left + (area.Width - w) / 2;
        int y = area.Top + (area.Height - h) / 2;
        Location = new Point(x, y);
    }

    private static bool IsRectVisible(Rectangle rect)
    {
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(rect)) return true;
        }
        return false;
    }

    private bool HasSavedSettings =>
        !_settings.IsFirstRun && !string.IsNullOrEmpty(_settings.DeviceMonikerId);

    private void PositionStartupDialog()
    {
        if (_startupDialog == null) return;
        Rectangle area;
        if (HasSavedSettings)
        {
            // 設定あり: メインウィンドウの中心
            area = Bounds;
        }
        else
        {
            // 設定なし: 実行したモニタ（カーソルのある画面）の中央
            area = Screen.FromPoint(Cursor.Position).WorkingArea;
        }
        int x = area.Left + (area.Width - _startupDialog.Width) / 2;
        int y = area.Top + (area.Height - _startupDialog.Height) / 2;
        _startupDialog.Location = new Point(x, y);
    }

    private void UpdateTitle()
    {
        string title = BaseTitle;
        if (!string.IsNullOrEmpty(_settings.DeviceName))
        {
            title = $"{BaseTitle} - {_settings.DeviceName}";

            var actual = _capture.ActualVideoSize;
            int w = actual.Width > 0 ? actual.Width : _settings.CaptureWidth;
            int h = actual.Height > 0 ? actual.Height : _settings.CaptureHeight;

            if (w > 0 && h > 0)
            {
                double setFps = _settings.CaptureFps;
                double measured = _capture.ActualFps;
                // 括弧内: 解像度プリセットの FPS（設定値があれば設定値、
                // 自動＝未指定なら実際に適用された公称 FPS）
                double shownFps = setFps > 0 ? setFps : _capture.NominalFps;
                string fpsPart = shownFps > 0 ? $" @ {shownFps:0.##}fps" : "";
                title = $"{BaseTitle} - {_settings.DeviceName} [{w}x{h}{fpsPart}]";

                // 括弧の後に実測 FPS（数字のみ、空白1つ）
                if (measured > 0) title += $" {measured:0.#}fps";
            }
        }
        if (Text != title) Text = title;
    }

    private BusyDialog? _startupDialog;

    private void OnFormShown(object? sender, EventArgs e)
    {
        // 起動処理が 0.5s 以内に終わればダイアログは出さない。
        // 0.5s 経過してもまだ起動中なら検出中ダイアログを表示。
        bool startupDone = false;
        var delay = new System.Windows.Forms.Timer { Interval = 500 };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            delay.Dispose();
            if (startupDone || IsDisposed) return;
            _startupDialog = new BusyDialog("MediaPreviewSan起動中…") { MinimumStageMs = 0 };
            _startupDialog.StartPosition = FormStartPosition.Manual;
            PositionStartupDialog();      // Show 前に位置確定（左上チラ見防止）
            _startupDialog.Show(this);
        };
        delay.Start();

        BeginInvoke(new Action(async () =>
        {
            try
            {
                // 起動時に1回だけデバイス検出してキャッシュを温める
                if (!DeviceCache.HasCache)
                {
                    if (_startupDialog != null)
                        await _startupDialog.SetMessageAfterMinimumAsync("デバイスを検出中...");
                    await Task.Run(() => DeviceCache.Get(forceRefresh: true));
                }

                if (_settings.IsFirstRun || string.IsNullOrEmpty(_settings.DeviceMonikerId))
                {
                    startupDone = true;
                    await CloseStartupDialogAsync();
                    OpenSettings();
                }
                else
                {
                    StartCapture();
                    startupDone = true;
                    await CloseStartupDialogAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"startup failed: {ex.Message}");
                startupDone = true;
                await CloseStartupDialogAsync();
            }
        }));
    }

    private async Task CloseStartupDialogAsync()
    {
        if (_startupDialog != null && !_startupDialog.IsDisposed)
        {
            await _startupDialog.CloseAfterMinimumAsync();
        }
        _startupDialog = null;
    }

    private void CloseStartupDialogSafe()
    {
        if (_startupDialog != null && !_startupDialog.IsDisposed)
        {
            try { _startupDialog.Close(); _startupDialog.Dispose(); } catch { }
        }
        _startupDialog = null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        try
        {
            _signalTimer.Stop();
            _renderTimer.Stop();
            _saveDebounce.Stop();
            _capture.Stop();
        }
        catch (Exception ex)
        {
            Logger.Log($"stop on close failed: {ex.Message}");
        }
        SaveBoundsToSettings(force: true);
        _settings.Save();
        Logger.Log("application closing");
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (_restoringBounds) return;
        if (WindowState is FormWindowState.Maximized or FormWindowState.Normal)
        {
            RequestSaveBounds();
        }
    }

    private void RequestSaveBounds()
    {
        if (_restoringBounds) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveBoundsToSettings(bool force = false)
    {
        if (_suppressSaveBounds && !force) return;
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.WindowWidth = Size.Width;
            _settings.WindowHeight = Size.Height;
        }
        _settings.WindowMaximized = (WindowState == FormWindowState.Maximized);
        _settings.Save();
    }

    private void ShowIdle()
    {
        _overlay.ShowStatic("デバイス未選択\n右クリック →「設定...」");
    }

    /// <summary>
    /// 現在の設定デバイスへ再接続する。排他使用していた別アプリを閉じた後、
    /// 設定を開き直さずワンクリックで復帰させるための機能。
    /// </summary>
    private void Reconnect()
    {
        _signalTimer.Stop();
        if (string.IsNullOrEmpty(_settings.DeviceMonikerId))
        {
            ShowIdle();
            return;
        }
        // StartCapture が旧 _capture を Dispose し、デバイス種別に応じた
        // service を作り直すので、ここでは呼び直すだけでよい。
        Logger.Log("manual reconnect requested");
        StartCapture();
    }

    /// <summary>
    /// デバイスを解放して接続を解除する。これにより排他を解き、
    /// 他アプリ（OBS / Teams 等）にデバイスの制御を渡せる。
    /// 再開は右クリック →「再接続」。
    /// </summary>
    private void Disconnect()
    {
        _signalTimer.Stop();
        try { _capture.Dispose(); } catch { }
        // 空インスタンスに差し替え（IsRunning=false）。デバイスは解放済み。
        _capture = new MediaFoundationCaptureService();
        Logger.Log("manual disconnect requested");
        _overlay.ShowStatic(
            "接続を解除しました（デバイスは他アプリで利用可能です）。\n\n"
            + "右クリック →「再接続」で再開できます。");
    }

    private void StartCapture()
    {
        _signalTimer.Stop();

        if (string.IsNullOrEmpty(_settings.DeviceMonikerId))
        {
            ShowIdle();
            return;
        }

        try
        {
            // デバイス一覧はキャッシュから取得（再検出はしない）
            var devices = DeviceCache.Get(forceRefresh: false);
            DeviceInfo? device = devices.FirstOrDefault(d =>
                string.Equals(d.PersistentId, _settings.DeviceMonikerId, StringComparison.OrdinalIgnoreCase));
            if (device == null && !string.IsNullOrEmpty(_settings.DeviceName))
                device = devices.FirstOrDefault(d => d.Name == _settings.DeviceName);

            if (device == null)
            {
                _overlay.ShowStatic($"「{_settings.DeviceName}」が見つかりません。\n右クリック →「設定...」で再選択してください。");
                return;
            }

            _overlay.ShowMarquee($"「{device.Name}」からの信号を待っています...");

            // 前回の capture を破棄して、デバイスに応じた service を作り直す
            try { _capture.Dispose(); } catch { }

            if (device.IsDirectShowOnly)
            {
                CaptureFormat? dsFmt = null;
                if (_settings.CaptureWidth > 0 && _settings.CaptureHeight > 0)
                {
                    dsFmt = new CaptureFormat
                    {
                        Width = _settings.CaptureWidth,
                        Height = _settings.CaptureHeight,
                        Fps = _settings.CaptureFps,
                    };
                }
                var ds = new DirectShowCaptureService();
                ds.Start(device.DirectShowDevicePath, dsFmt,
                    ParseSampler(_settings.ScalingMode),
                    _videoPanel.Handle, _videoPanel.ClientRectangle);
                _capture = ds;
            }
            else
            {
                CaptureFormat? fmt = null;
                if (_settings.CaptureWidth > 0 && _settings.CaptureHeight > 0)
                {
                    var formats = DeviceCache.GetFormats(device.SymbolicLink, forceRefresh: false);
                    var match = formats.FirstOrDefault(f =>
                        f.Width == _settings.CaptureWidth && f.Height == _settings.CaptureHeight
                        && Math.Abs(f.Fps - _settings.CaptureFps) < 0.5)
                        ?? formats.FirstOrDefault(f => f.Width == _settings.CaptureWidth && f.Height == _settings.CaptureHeight);
                    if (match != null) fmt = match;
                }
                var mf = new MediaFoundationCaptureService();
                mf.FatalError += OnCaptureFatalError;
                mf.Start(device.SymbolicLink, fmt, ParseSampler(_settings.ScalingMode),
                    _videoPanel.Handle, _videoPanel.ClientRectangle);
                _capture = mf;
            }
            _capture.SetDrawRect(GetDrawRect());
            UpdateTitle(); // 信号到着前でも設定値ベースで反映、後で OnSignalTick が実値で再更新
            _signalTimer.Start();
        }
        catch (Exception ex)
        {
            Logger.Log($"capture start failed: {ex}");
            uint hr = ex is SharpGen.Runtime.SharpGenException sge
                ? unchecked((uint)sge.HResult) : 0;
            if (hr == 0x80070005  /* E_ACCESSDENIED */
                || hr == 0xC00D3704 /* MF_E_INVALIDREQUEST */
                || hr == 0x80070020 /* ERROR_SHARING_VIOLATION */
                || hr == 0x800700AA /* ERROR_BUSY */)
            {
                ShowExclusiveError();
            }
            else
            {
                _overlay.ShowStatic($"開始失敗:\n{ex.Message}");
                MessageBox.Show(this, $"キャプチャ開始に失敗しました:\n{ex.Message}",
                    BaseTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void ShowExclusiveError()
    {
        _signalTimer.Stop();
        _overlay.ShowStatic(
            "デバイスを開けませんでした。\n"
            + "他のアプリケーション（OBS / Teams / ブラウザ等）がこのデバイスを\n"
            + "排他的に使用している可能性があります。\n"
            + "使用中のアプリを終了してから再度お試しください。\n\n"
            + "右クリック →「設定...」で別デバイスを選択できます。");
    }

    private void OnSignalTick(object? sender, EventArgs e)
    {
        if (!_capture.IsRunning)
        {
            _signalTimer.Stop();
            return;
        }
        if (_capture.HasFrame)
        {
            var sz = _capture.ActualVideoSize;
            Logger.Log($"video signal acquired: {sz.Width}x{sz.Height}");
            _overlay.Hide();
            _capture.SetDrawRect(GetDrawRect());
            UpdateTitle();
            _signalTimer.Stop();
        }
    }

    private void OnVideoPanelResize()
    {
        if (_capture.IsRunning)
        {
            var bounds = _videoPanel.ClientRectangle;
            _capture.Resize(bounds);
            _capture.SetDrawRect(GetDrawRect());
        }
    }

    private Rectangle GetDrawRect()
    {
        var client = _videoPanel.ClientRectangle;
        if (!_settings.MaintainAspectRatio) return client;

        var actual = _capture.ActualVideoSize;
        int srcW = actual.Width > 0 ? actual.Width : _settings.CaptureWidth;
        int srcH = actual.Height > 0 ? actual.Height : _settings.CaptureHeight;
        if (srcW <= 0 || srcH <= 0) return client;

        double srcRatio = (double)srcW / srcH;
        double dstRatio = client.Height == 0 ? srcRatio : (double)client.Width / client.Height;
        int w, h;
        if (dstRatio > srcRatio)
        {
            h = client.Height;
            w = (int)Math.Round(h * srcRatio);
        }
        else
        {
            w = client.Width;
            h = (int)Math.Round(w / srcRatio);
        }
        int x = (client.Width - w) / 2;
        int y = (client.Height - h) / 2;
        return new Rectangle(x, y, w, h);
    }

    private void OpenSettings()
    {
        // キャプチャは止めずに継続表示。OK 時に差分があれば再接続する。
        string prevDevice = _settings.DeviceMonikerId;
        int prevW = _settings.CaptureWidth;
        int prevH = _settings.CaptureHeight;
        double prevFps = _settings.CaptureFps;
        string prevSampler = _settings.ScalingMode;
        bool prevAspect = _settings.MaintainAspectRatio;

        using var dlg = new SettingsForm(_settings)
        {
            StartPosition = FormStartPosition.CenterParent,
            Icon = IconLoader.AppIcon,
        };
        var result = dlg.ShowDialog(this);

        // Windows のタスク終了要求で変更なし → アプリごと終了
        if (dlg.ShutdownRequested)
        {
            Close();
            return;
        }

        _settings.IsFirstRun = false;
        _settings.Save();
        UpdateTitle();

        if (result != DialogResult.OK)
        {
            // キャンセル → 何もしない（映像も継続）
            if (!_capture.IsRunning && string.IsNullOrEmpty(_settings.DeviceMonikerId))
                ShowIdle();
            return;
        }

        bool deviceChanged =
            !string.Equals(_settings.DeviceMonikerId, prevDevice, StringComparison.OrdinalIgnoreCase)
            || _settings.CaptureWidth != prevW
            || _settings.CaptureHeight != prevH
            || Math.Abs(_settings.CaptureFps - prevFps) > 0.5;
        bool samplerChanged = !string.Equals(_settings.ScalingMode, prevSampler, StringComparison.OrdinalIgnoreCase);
        bool aspectChanged = _settings.MaintainAspectRatio != prevAspect;

        if (deviceChanged)
        {
            _signalTimer.Stop();
            try { _capture.Stop(); } catch { }
            if (string.IsNullOrEmpty(_settings.DeviceMonikerId))
                ShowIdle();
            else
                StartCapture();
            return;
        }

        if (samplerChanged && _capture.IsRunning)
        {
            _capture.SetSampler(ParseSampler(_settings.ScalingMode));
        }
        if (aspectChanged && _capture.IsRunning)
        {
            _capture.SetDrawRect(GetDrawRect());
        }

        if (!_capture.IsRunning && !string.IsNullOrEmpty(_settings.DeviceMonikerId))
        {
            // 何かの理由で停止していたなら（初回起動・前回エラー）開始
            StartCapture();
        }
    }

    private void OnCaptureFatalError(string message)
    {
        // ReadSample スレッドから呼ばれるため UI スレッドへマーシャル
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _signalTimer.Stop();
                _overlay.ShowStatic(message + "\n\n右クリック →「設定...」で別デバイスを選択できます。");
                CloseStartupDialogSafe();
            }));
        }
        catch { }
    }

    private static SamplerKind ParseSampler(string value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "point" => SamplerKind.Point,
            "anisotropic" => SamplerKind.Anisotropic,
            _ => SamplerKind.Bilinear,
        };
    }

    private static Bitmap BuildSettingsIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.DimGray, 1.5f);
        g.DrawEllipse(pen, 3, 3, 10, 10);
        g.DrawLine(pen, 8, 1, 8, 4);
        g.DrawLine(pen, 8, 12, 8, 15);
        g.DrawLine(pen, 1, 8, 4, 8);
        g.DrawLine(pen, 12, 8, 15, 8);
        using var brush = new SolidBrush(Color.DimGray);
        g.FillEllipse(brush, 6, 6, 4, 4);
        return bmp;
    }
}
