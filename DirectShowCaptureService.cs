using DirectShowLib;
using OpenCvSharp;

namespace MediaPreviewSan;

/// <summary>
/// MF で開けない仮想カメラ（OBS Virtual Camera 等の DS-only デバイス）用。
/// OpenCvSharp の VideoCapture(CAP_DSHOW) でフレームを取得し、
/// Nv12Renderer の BGRA パスで D3D11 GPU 描画する（補間・アス比は MF と共通）。
/// </summary>
public sealed class DirectShowCaptureService : ICaptureService
{
    private readonly object _sync = new();
    private VideoCapture? _cap;
    private Mat? _bgra;
    private Nv12Renderer? _renderer;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _hasFrame;
    private int _videoWidth;
    private int _videoHeight;
    private long _frameCount;
    private readonly System.Diagnostics.Stopwatch _fpsSw = new();
    private long _fpsFrames;
    private double _measuredFps;
    private double _nominalFps;

    public bool IsRunning => _running;
    public bool HasFrame => _hasFrame;

    public System.Drawing.Size ActualVideoSize
    {
        get { lock (_sync) return new System.Drawing.Size(_videoWidth, _videoHeight); }
    }

    /// <summary>直近 1 秒間の実測フレームレート。</summary>
    public double ActualFps
    {
        get { lock (_sync) return _measuredFps; }
    }

    /// <summary>適用した解像度プリセットの公称フレームレート。</summary>
    public double NominalFps
    {
        get { lock (_sync) return _nominalFps; }
    }

    public void Start(string devicePath, CaptureFormat? format, SamplerKind sampler,
        IntPtr ownerHandle, Rectangle bounds)
    {
        Stop();

        if (string.IsNullOrEmpty(devicePath))
            throw new InvalidOperationException("デバイスパスが空です");
        if (ownerHandle == IntPtr.Zero)
            throw new InvalidOperationException("ウィンドウハンドルが無効です");

        int camIndex = ResolveDeviceIndex(devicePath);
        if (camIndex < 0)
            throw new InvalidOperationException("対象デバイスのインデックスを特定できませんでした");

        // 「自動（解像度未指定）」のときは MF 同様に最高画質を選ぶ
        if (format == null || format.Width <= 0 || format.Height <= 0)
        {
            var list = EnumerateFormats(devicePath);
            if (list.Count > 0)
            {
                format = list[0]; // 降順ソート済み → 先頭が最高画質
                Logger.Log($"DS(OpenCV) auto-selected best format: {format}");
            }
        }

        var cap = new VideoCapture(camIndex, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened())
        {
            cap.Dispose();
            throw new InvalidOperationException(
                "OpenCV でデバイスを開けませんでした（他アプリが使用中の可能性）");
        }

        // 解像度/FPS の指定があれば適用
        if (format != null && format.Width > 0 && format.Height > 0)
        {
            cap.Set(VideoCaptureProperties.FrameWidth, format.Width);
            cap.Set(VideoCaptureProperties.FrameHeight, format.Height);
            if (format.Fps > 0) cap.Set(VideoCaptureProperties.Fps, format.Fps);
        }
        lock (_sync) _nominalFps = format?.Fps ?? 0;

        _cap = cap;
        int w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        int h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        lock (_sync) { _videoWidth = w; _videoHeight = h; }
        Logger.Log($"DS(OpenCV) opened index={camIndex} {w}x{h} req={format}");

        _renderer = new Nv12Renderer(ownerHandle, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        _renderer.SetSampler(sampler);
        _renderer.SetDrawRect(new Rectangle(0, 0, bounds.Width, bounds.Height));

        _bgra = new Mat();
        _running = true;
        _frameCount = 0;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "OpenCV-Read" };
        _thread.Start();
    }

    private void ReadLoop()
    {
        var cap = _cap;
        if (cap == null) return;
        try
        {
            using var frame = new Mat();
            while (_running)
            {
                if (!cap.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(10);
                    continue;
                }

                int w = frame.Width, h = frame.Height;
                lock (_sync) { _videoWidth = w; _videoHeight = h; }

                // OpenCV は BGR3ch。D3D11 用に BGRA4ch へ変換して GPU 転送。
                var bgra = _bgra;
                if (bgra == null) break;
                Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);

                _renderer?.UpdateBgra(bgra.Data, (int)bgra.Step(), w, h);
                _hasFrame = true;

                long n = System.Threading.Interlocked.Increment(ref _frameCount);
                if (n == 1) Logger.Log($"DS(OpenCV) first frame: {w}x{h}");

                if (!_fpsSw.IsRunning) _fpsSw.Restart();
                _fpsFrames++;
                long el = _fpsSw.ElapsedMilliseconds;
                if (el >= 1000)
                {
                    lock (_sync) _measuredFps = _fpsFrames * 1000.0 / el;
                    _fpsFrames = 0;
                    _fpsSw.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenCV ReadLoop failed: {ex.Message}");
        }
    }

    public void Resize(Rectangle bounds)
    {
        var r = _renderer;
        if (r == null) return;
        r.Resize(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        r.SetDrawRect(new Rectangle(0, 0, bounds.Width, bounds.Height));
    }

    public void SetDrawRect(Rectangle rect) => _renderer?.SetDrawRect(rect);
    public void SetSampler(SamplerKind kind) => _renderer?.SetSampler(kind);

    public void Stop()
    {
        _running = false;
        _hasFrame = false;

        var cap = _cap; _cap = null;
        var thread = _thread; _thread = null;
        var bgra = _bgra; _bgra = null;
        var renderer = _renderer; _renderer = null;

        // OpenCV の VideoCapture は ReadLoop の cap.Read と別スレッド Release が
        // 競合するとハングする。さらに別スレッド遅延 Release だと、次の Start が
        // 同一デバイスを二重 Open してデッドロックする（解像度変更時のハング原因）。
        // よって ReadLoop 終了を同期で待ってから cap を確実に解放する。
        try { thread?.Join(3000); } catch { }       // ReadLoop が cap.Read を抜けるまで待つ
        try { renderer?.Dispose(); } catch { }      // SwapChain/HWND を同期解放
        try { cap?.Release(); } catch { }
        try { cap?.Dispose(); } catch { }           // ここでデバイスが解放される
        try { bgra?.Dispose(); } catch { }

        lock (_sync) { _videoWidth = 0; _videoHeight = 0; _measuredFps = 0; _nominalFps = 0; }
    }

    public void Dispose() => Stop();

    /// <summary>DirectShowLib で DS デバイスの対応解像度を列挙する（OpenCV には列挙 API がないため）。</summary>
    public static List<CaptureFormat> EnumerateFormats(string devicePath)
    {
        var result = new List<CaptureFormat>();
        if (string.IsNullOrEmpty(devicePath)) return result;

        IFilterGraph2? graph = null;
        IBaseFilter? src = null;
        ICaptureGraphBuilder2? builder = null;
        try
        {
            graph = (IFilterGraph2)new FilterGraph();
            builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            builder.SetFiltergraph(graph);

            DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            try
            {
                foreach (var d in devices)
                {
                    if (string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var iid = typeof(IBaseFilter).GUID;
                        d.Mon.BindToObject(null!, null!, ref iid, out object o);
                        src = (IBaseFilter)o;
                        graph.AddFilter(src, "src");
                        break;
                    }
                }
            }
            finally
            {
                foreach (var d in devices) d.Dispose();
            }
            if (src == null) return result;

            object? o2;
            int hr = builder.FindInterface(PinCategory.Capture, MediaType.Video, src,
                typeof(IAMStreamConfig).GUID, out o2);
            if (hr != 0 || o2 is not IAMStreamConfig cfg)
            {
                hr = builder.FindInterface(PinCategory.Preview, MediaType.Video, src,
                    typeof(IAMStreamConfig).GUID, out o2);
                if (hr != 0 || o2 is not IAMStreamConfig) return result;
                cfg = (IAMStreamConfig)o2;
            }

            cfg.GetNumberOfCapabilities(out int count, out int size);
            IntPtr caps = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                var seen = new HashSet<string>();
                for (int i = 0; i < count; i++)
                {
                    AMMediaType? mt = null;
                    try
                    {
                        if (cfg.GetStreamCaps(i, out mt, caps) != 0 || mt == null) continue;
                        if (mt.formatPtr == IntPtr.Zero) continue;
                        int wv = 0, hv = 0; double fps = 0;
                        if (mt.formatType == DirectShowLib.FormatType.VideoInfo)
                        {
                            var vih = System.Runtime.InteropServices.Marshal
                                .PtrToStructure<VideoInfoHeader>(mt.formatPtr);
                            if (vih == null) continue;
                            wv = vih.BmiHeader.Width;
                            hv = Math.Abs(vih.BmiHeader.Height);
                            if (vih.AvgTimePerFrame > 0) fps = 10_000_000.0 / vih.AvgTimePerFrame;
                        }
                        else if (mt.formatType == DirectShowLib.FormatType.VideoInfo2)
                        {
                            var vih = System.Runtime.InteropServices.Marshal
                                .PtrToStructure<VideoInfoHeader2>(mt.formatPtr);
                            if (vih == null) continue;
                            wv = vih.BmiHeader.Width;
                            hv = Math.Abs(vih.BmiHeader.Height);
                            if (vih.AvgTimePerFrame > 0) fps = 10_000_000.0 / vih.AvgTimePerFrame;
                        }
                        if (wv <= 0 || hv <= 0) continue;
                        string key = $"{wv}x{hv}@{fps:0.##}";
                        if (!seen.Add(key)) continue;
                        result.Add(new CaptureFormat
                        {
                            Width = wv, Height = hv, Fps = fps,
                            SubTypeName = "DShow", NativeTypeIndex = i,
                        });
                    }
                    finally
                    {
                        if (mt != null) DsUtils.FreeAMMediaType(mt);
                    }
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(caps);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"DS EnumerateFormats failed: {ex.Message}");
        }
        finally
        {
            if (src != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(src);
            if (builder != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(builder);
            if (graph != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(graph);
        }

        // OBS Virtual Camera 等は IAMStreamConfig で 1 つしか公開しないことがある。
        // 仮想カメラはネイティブ解像度「以下」へはソフトリサイズ対応するが、
        // それを超える解像度には拡大しない。よってプリセットは
        // ネイティブ最大解像度以下のみ補完し、FPS はネイティブ値を付与する
        // （解像度と FPS をセットで VideoCapture.Set する）。
        int maxW = result.Count > 0 ? result.Max(f => f.Width) : int.MaxValue;
        int maxH = result.Count > 0 ? result.Max(f => f.Height) : int.MaxValue;
        double nativeFps = result.Count > 0 ? result.Max(f => f.Fps) : 0;
        if (nativeFps <= 0) nativeFps = 30;

        var presentSizes = new HashSet<string>(
            result.Select(f => $"{f.Width}x{f.Height}"));
        var presets = new (int W, int H)[]
        {
            (3840, 2160), (2560, 1440), (1920, 1080), (1600, 900),
            (1280, 720), (1024, 576), (960, 540), (854, 480),
            (800, 600), (640, 480), (640, 360), (424, 240),
        };
        foreach (var (pw, ph) in presets)
        {
            if (pw > maxW || ph > maxH) continue; // ネイティブ超は不可
            if (presentSizes.Add($"{pw}x{ph}"))
            {
                result.Add(new CaptureFormat
                {
                    Width = pw, Height = ph, Fps = nativeFps,
                    SubTypeName = "preset", NativeTypeIndex = -1,
                });
            }
        }

        result.Sort((a, b) =>
        {
            int c = b.Width.CompareTo(a.Width);
            if (c != 0) return c;
            c = b.Height.CompareTo(a.Height);
            if (c != 0) return c;
            return b.Fps.CompareTo(a.Fps);
        });
        return result;
    }

    private static int ResolveDeviceIndex(string devicePath)
    {
        DsDevice[] devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (string.Equals(devices[i].DevicePath, devicePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
        return -1;
    }
}
