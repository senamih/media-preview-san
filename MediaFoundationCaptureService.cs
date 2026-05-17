using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace MediaPreviewSan;

public sealed class MediaFoundationCaptureService : ICaptureService
{
    private static int _startupCount;

    private readonly object _sync = new();
    private Nv12Renderer? _renderer;
    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private Thread? _readThread;
    private volatile int _generation;
    private volatile bool _running;
    private int _videoWidth;
    private int _videoHeight;
    private double _actualFps;
    private Rectangle _drawBounds;

    /// <summary>復帰不能なキャプチャエラー（排他使用中など）の通知。別スレッドから発火する。</summary>
    public event Action<string>? FatalError;

    public bool IsRunning => _running;

    public System.Drawing.Size ActualVideoSize
    {
        get { lock (_sync) return new System.Drawing.Size(_videoWidth, _videoHeight); }
    }

    private readonly System.Diagnostics.Stopwatch _fpsSw = new();
    private long _fpsFrames;
    private double _measuredFps;

    /// <summary>実測フレームレート。</summary>
    public double ActualFps
    {
        get { lock (_sync) return _measuredFps; }
    }

    /// <summary>適用された MediaType の公称フレームレート。</summary>
    public double NominalFps
    {
        get { lock (_sync) return _actualFps; }
    }

    public bool HasFrame => _renderer?.HasFrame == true;

    public static void GlobalStartup()
    {
        if (Interlocked.Increment(ref _startupCount) == 1)
        {
            MediaFactory.MFStartup(true).CheckError();
        }
    }

    public static void GlobalShutdown()
    {
        if (Interlocked.Decrement(ref _startupCount) == 0)
        {
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }

    // Win10+ の標準カメラカテゴリ。OBS v32 等 MF 対応の仮想カメラはここに登録される。
    private static readonly Guid KSCATEGORY_VIDEO_CAMERA =
        new("E5323777-F976-4F5B-9B55-B94699C46E44");

    public static List<DeviceInfo> EnumerateDevices()
    {
        var list = new List<DeviceInfo>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int idx = -1;

        void EnumerateMf(Guid? category, string label)
        {
            int rawCount = 0;
            try
            {
                using var attr = MediaFactory.MFCreateAttributes(category.HasValue ? 2u : 1u);
                attr.SourceType = CaptureDeviceAttributeKeys.SourceTypeVidcap;
                if (category.HasValue)
                    attr.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapCategory, category.Value);

                using IMFActivateCollection col = MediaFactory.MFEnumDeviceSources(attr);

                foreach (IMFActivate act in col)
                {
                    idx++;
                    rawCount++;
                    string name = "";
                    string link = "";

                    try
                    {
                        try { name = act.FriendlyName ?? ""; }
                        catch (Exception ex)
                        {
                            Logger.Log($"  mf[{label}][{idx}] FriendlyName threw: {ex.GetType().Name}: {ex.Message}");
                        }
                        try { link = act.SymbolicLink ?? ""; }
                        catch (Exception ex)
                        {
                            Logger.Log($"  mf[{label}][{idx}] SymbolicLink threw: {ex.GetType().Name}: {ex.Message}");
                        }

                        string linkKey = link.Trim();
                        string nameKey = name.Trim();

                        if (string.IsNullOrEmpty(linkKey) && string.IsNullOrEmpty(nameKey))
                        {
                            Logger.Log($"  mf[{label}][{idx}] skipped: empty name and link");
                            continue;
                        }
                        if (!string.IsNullOrEmpty(linkKey) && !seenLinks.Add(linkKey))
                        {
                            Logger.Log($"  mf[{label}][{idx}] skipped: duplicate link, name='{name}'");
                            continue;
                        }
                        if (!string.IsNullOrEmpty(nameKey) && !seenNames.Add(nameKey))
                        {
                            Logger.Log($"  mf[{label}][{idx}] skipped: duplicate name='{name}'");
                            continue;
                        }

                        if (string.IsNullOrEmpty(name)) name = "(unknown)";
                        list.Add(new DeviceInfo
                        {
                            Name = name,
                            SymbolicLink = link,
                            DirectShowDevicePath = link, // MF/DS で同形式（\\?\...）なので兼用
                        });
                        Logger.Log($"  mf[{label}][{idx}] added: name='{name}' link='{(link.Length > 60 ? link.Substring(0, 60) + "..." : link)}'");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"  mf[{label}][{idx}] processing failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        try { act.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"EnumerateMf[{label}] failed: {ex.GetType().Name}: {ex.Message}");
            }
            Logger.Log($"EnumerateMf[{label}]: raw {rawCount}");
        }

        // 既定の Vidcap 列挙 + Win10+ カメラカテゴリ（OBS v32 等 MF 仮想カメラはこちらに出る）
        EnumerateMf(null, "default");
        EnumerateMf(KSCATEGORY_VIDEO_CAMERA, "VideoCamera");

        Logger.Log($"EnumerateDevices(MF): kept {list.Count} device(s)");

        // DS でも列挙して MF が見落としたものを補完
        AppendDirectShowDevices(list, seenLinks, seenNames);

        Logger.Log($"EnumerateDevices: total {list.Count} device(s)");
        return list;
    }

    private static void AppendDirectShowDevices(List<DeviceInfo> list,
        HashSet<string> seenLinks, HashSet<string> seenNames)
    {
        DirectShowLib.DsDevice[]? devices = null;
        try
        {
            devices = DirectShowLib.DsDevice.GetDevicesOfCat(DirectShowLib.FilterCategory.VideoInputDevice);
        }
        catch (Exception ex)
        {
            Logger.Log($"DS enumerate threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (devices == null) { Logger.Log("DS enumerate returned null"); return; }

        int added = 0;
        int idx = -1;
        foreach (var d in devices)
        {
            idx++;
            string name = "";
            string path = "";
            try
            {
                try { name = d.Name ?? ""; }
                catch (Exception ex) { Logger.Log($"  ds[{idx}] Name threw: {ex.GetType().Name}: {ex.Message}"); }
                try { path = d.DevicePath ?? ""; }
                catch (Exception ex) { Logger.Log($"  ds[{idx}] DevicePath threw: {ex.GetType().Name}: {ex.Message}"); }

                string linkKey = path.Trim();
                string nameKey = name.Trim();

                if (string.IsNullOrEmpty(linkKey))
                {
                    Logger.Log($"  ds[{idx}] skipped (no device path): name='{name}'");
                    continue;
                }
                // @device:sw:... 等の software-only moniker は MF では開けない。
                // ただし DS パイプライン経由で開ける可能性があるので、IsDirectShowOnly として残す。
                bool mfCompatible = linkKey.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase);

                if (mfCompatible)
                {
                    if (!seenLinks.Add(linkKey))
                    {
                        Logger.Log($"  ds[{idx}] skipped: duplicate link (MF-compat), name='{name}'");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(nameKey) && !seenNames.Add(nameKey))
                    {
                        Logger.Log($"  ds[{idx}] skipped: duplicate name='{name}' (MF-compat)");
                        continue;
                    }
                    if (string.IsNullOrEmpty(name)) name = "(unknown)";
                    list.Add(new DeviceInfo
                    {
                        Name = name,
                        SymbolicLink = linkKey,
                        DirectShowDevicePath = linkKey,
                    });
                    Logger.Log($"  ds[{idx}] added (MF-compat): name='{name}' link='{(linkKey.Length > 60 ? linkKey.Substring(0, 60) + "..." : linkKey)}'");
                }
                else
                {
                    // DS only: MF SymbolicLink を持たないが DS device path で開ける
                    if (!string.IsNullOrEmpty(nameKey) && !seenNames.Add(nameKey))
                    {
                        Logger.Log($"  ds[{idx}] skipped: duplicate name='{name}' (DS-only)");
                        continue;
                    }
                    if (string.IsNullOrEmpty(name)) name = "(unknown)";
                    list.Add(new DeviceInfo
                    {
                        Name = name,
                        SymbolicLink = "",
                        DirectShowDevicePath = linkKey,
                    });
                    Logger.Log($"  ds[{idx}] added (DS-only): name='{name}' path='{(linkKey.Length > 60 ? linkKey.Substring(0, 60) + "..." : linkKey)}'");
                }
                added++;
            }
            catch (Exception ex)
            {
                Logger.Log($"  ds[{idx}] processing failed: {ex.GetType().Name}: {ex.Message} (name='{name}')");
            }
            finally
            {
                try { d.Dispose(); }
                catch (Exception ex) { Logger.Log($"  ds[{idx}] dispose threw: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        Logger.Log($"EnumerateDevices(DS): processed {idx + 1} device(s), added {added}");
    }

    public static List<CaptureFormat> EnumerateFormats(string symbolicLink)
    {
        var result = new List<CaptureFormat>();
        if (string.IsNullOrEmpty(symbolicLink)) return result;

        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            source = CreateMediaSourceBySymbolicLink(symbolicLink);
            if (source == null) return result;
            reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, null);
            result = EnumerateFormatsViaReader(reader);
        }
        catch (Exception ex)
        {
            Logger.Log($"EnumerateFormats failed: {ex.Message}");
        }
        finally
        {
            reader?.Dispose();
            if (source != null)
            {
                try { source.Shutdown(); } catch { }
                source.Dispose();
            }
        }
        return result;
    }

    public void Start(string symbolicLink, CaptureFormat? format, SamplerKind sampler,
        IntPtr ownerHandle, Rectangle bounds)
    {
        Stop(); // 自インスタンス（renderer は同期破棄され HWND を解放する）

        if (string.IsNullOrEmpty(symbolicLink))
            throw new InvalidOperationException("デバイスが指定されていません");
        if (ownerHandle == IntPtr.Zero)
            throw new InvalidOperationException("ウィンドウハンドルが無効です");

        _drawBounds = bounds;

        try
        {
            _source = CreateMediaSourceBySymbolicLink(symbolicLink)
                ?? throw new InvalidOperationException("指定デバイスを開けませんでした");

            using var readerAttr = MediaFactory.MFCreateAttributes(1);
            readerAttr.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, false);

            _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, readerAttr);

            if (format == null || format.NativeTypeIndex < 0)
            {
                var list = EnumerateFormatsViaReader(_reader);
                if (list.Count > 0) format = list[0];
            }
            if (format == null) throw new InvalidOperationException("利用可能なメディアタイプがありません");

            const int videoStream = (int)SourceReaderIndex.FirstVideoStream;

            using var desired = MediaFactory.MFCreateMediaType();
            desired.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            desired.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeSize(desired, MediaTypeAttributeKeys.FrameSize, (uint)format.Width, (uint)format.Height);
            if (format.Fps > 0)
            {
                uint num = (uint)Math.Round(format.Fps * 1000);
                MediaFactory.MFSetAttributeRatio(desired, MediaTypeAttributeKeys.FrameRate, num, 1000);
            }
            desired.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            desired.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);

            try
            {
                _reader.SetCurrentMediaType(videoStream, desired);
            }
            catch (SharpGenException ex)
            {
                Logger.Log($"SetCurrentMediaType(NV12) failed: 0x{(uint)ex.HResult:X}, falling back to native subtype");
                using IMFMediaType native = _reader.GetNativeMediaType(videoStream, format.NativeTypeIndex);
                Guid nativeSub = native.GetGUID(MediaTypeAttributeKeys.Subtype);
                desired.Set(MediaTypeAttributeKeys.Subtype, nativeSub);
                _reader.SetCurrentMediaType(videoStream, desired);
            }

            using (IMFMediaType current = _reader.GetCurrentMediaType(videoStream))
            {
                MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out uint aw, out uint ah);
                _videoWidth = (int)aw;
                _videoHeight = (int)ah;
                try
                {
                    MediaFactory.MFGetAttributeRatio(current, MediaTypeAttributeKeys.FrameRate, out uint frNum, out uint frDen);
                    _actualFps = frDen > 0 ? (double)frNum / frDen : 0;
                }
                catch { _actualFps = 0; }
                Guid subActual = current.GetGUID(MediaTypeAttributeKeys.Subtype);
                string subLabel = SubTypeName(subActual);
                if (string.IsNullOrEmpty(subLabel)) subLabel = subActual.ToString("D");
                Logger.Log($"reader current type: {subLabel} {_videoWidth}x{_videoHeight} @ {_actualFps:0.##}fps");
            }

            // ストリーム選択: enum 版を使う（int キャスト経由だと別 overload が呼ばれて挙動が変わる場合あり）
            try
            {
                _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            }
            catch (SharpGenException ex)
            {
                Logger.Log($"SetStreamSelection failed: 0x{(uint)ex.HResult:X}（無視して続行）");
            }

            _renderer = new Nv12Renderer(ownerHandle, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
            _renderer.SetSampler(sampler);
            _renderer.SetDrawRect(new Rectangle(0, 0, bounds.Width, bounds.Height));

            _running = true;
            int gen = ++_generation;
            _readThread = new Thread(() => ReadLoop(gen))
            {
                IsBackground = true,
                Name = "MF-ReadSample",
            };
            _readThread.Start();

            Logger.Log($"MF capture started: format={format} sampler={sampler}");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void SetSampler(SamplerKind kind) => _renderer?.SetSampler(kind);

    public void Resize(Rectangle bounds)
    {
        _drawBounds = bounds;
        var r = _renderer;
        if (r == null) return;
        r.Resize(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        r.SetDrawRect(new Rectangle(0, 0, bounds.Width, bounds.Height));
    }

    public void SetDrawRect(Rectangle rect) => _renderer?.SetDrawRect(rect);

    public void Stop()
    {
        // 世代を進めて ReadLoop に終了を伝える。UI スレッドはここで一切ブロックしない。
        _generation++;
        _running = false;

        var reader = _reader; _reader = null;
        var source = _source; _source = null;
        var thread = _readThread; _readThread = null;
        var renderer = _renderer; _renderer = null;
        _videoWidth = 0;
        _videoHeight = 0;
        _actualFps = 0;
        _measuredFps = 0;
        _fpsFrames = 0;
        _fpsSw.Reset();

        if (source == null && reader == null && thread == null && renderer == null)
            return;

        // renderer(Nv12Renderer) は同期破棄する。これが SwapChain を解放し
        // panel HWND を空ける。新 Start が同じ HWND に SwapChain を作るので、
        // ここで解放しないと CreateSwapChainForHwnd が E_ACCESSDENIED になる
        // （= 解像度変更時の排他エラーの真因）。Nv12Renderer.Dispose は
        // WaitHandle で即抜けるため数 ms で完了しハングしない。
        try { renderer?.Dispose(); } catch { }

        // MF Source / Reader はデバイスによって Shutdown/Join がブロックしうるため
        // 別スレッドで後片付け（UI を一切ブロックしない）。
        System.Threading.Tasks.Task.Run(() =>
        {
            try { source?.Shutdown(); } catch { }
            try { reader?.Flush(SourceReaderIndex.AllStreams); } catch { }
            try { thread?.Join(3000); } catch { }
            try { reader?.Dispose(); } catch { }
            try { source?.Dispose(); } catch { }
        });
    }

    public void Dispose() => Stop();

    private void ReadLoop(int myGen)
    {
        var reader = _reader;
        try
        {
            while (myGen == _generation && reader != null)
            {
                IMFSample? sample;
                SourceReaderFlag flags;
                try
                {
                    sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None, out _, out flags, out _);
                }
                catch (SharpGenException ex)
                {
                    if (myGen != _generation) break;
                    uint hr = (uint)ex.HResult;
                    Logger.Log($"ReadSample threw: 0x{hr:X}");
                    if (hr == 0xC00D3704 || hr == 0xC00D4A45 || hr == 0xC00D36B4)
                    {
                        string msg = hr == 0xC00D3704
                            ? "デバイスを開けませんでした。\n他のアプリケーション（OBS / Teams / ブラウザ等）が"
                              + "このデバイスを排他的に使用している可能性があります。\n"
                              + "使用中のアプリを終了してから再度お試しください。"
                            : $"キャプチャを継続できません（エラーコード 0x{hr:X8}）。";
                        try { FatalError?.Invoke(msg); } catch { }
                        break;
                    }
                    Thread.Sleep(50);
                    continue;
                }

                if (myGen != _generation) { sample?.Dispose(); break; }

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    Logger.Log("Reader: end of stream");
                    sample?.Dispose();
                    break;
                }
                if ((flags & (SourceReaderFlag.NativeMediaTypeChanged | SourceReaderFlag.CurrentMediaTypeChanged)) != 0)
                {
                    try
                    {
                        using IMFMediaType current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                        MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out uint aw, out uint ah);
                        double newFps = 0;
                        try
                        {
                            MediaFactory.MFGetAttributeRatio(current, MediaTypeAttributeKeys.FrameRate, out uint n, out uint d);
                            if (d > 0) newFps = (double)n / d;
                        }
                        catch { }
                        lock (_sync)
                        {
                            _videoWidth = (int)aw;
                            _videoHeight = (int)ah;
                            _actualFps = newFps;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"media type change read failed: {ex.Message}");
                    }
                }

                if (sample != null)
                {
                    try { ProcessSample(sample); }
                    catch (Exception ex) { Logger.Log($"ProcessSample failed: {ex.Message}"); }
                    sample.Dispose();

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
        }
        catch (Exception ex)
        {
            Logger.Log($"ReadLoop failed: {ex.Message}");
        }
    }

    private void ProcessSample(IMFSample sample)
    {
        var renderer = _renderer;
        if (renderer == null) return;
        int w, h;
        lock (_sync) { w = _videoWidth; h = _videoHeight; }
        if (w <= 0 || h <= 0) return;

        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();

        IMF2DBuffer? buf2d = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buf2d != null)
        {
            IntPtr scanline0 = IntPtr.Zero;
            int pitch = 0;
            bool locked = false;
            try
            {
                buf2d.Lock2D(out scanline0, out pitch);
                locked = true;
                IntPtr yPlane = scanline0;
                IntPtr uvPlane = IntPtr.Add(scanline0, pitch * h);
                renderer.UpdateNv12(yPlane, pitch, uvPlane, pitch, w, h);
                return;
            }
            catch (Exception ex)
            {
                Logger.Log($"IMF2DBuffer.Lock2D failed: {ex.Message}");
            }
            finally
            {
                if (locked) { try { buf2d.Unlock2D(); } catch { } }
                buf2d.Dispose();
            }
        }

        IntPtr ptr = IntPtr.Zero;
        bool bufLocked = false;
        try
        {
            buffer.Lock(out ptr, out _, out _);
            bufLocked = true;
            IntPtr yPlane = ptr;
            IntPtr uvPlane = IntPtr.Add(ptr, w * h);
            renderer.UpdateNv12(yPlane, w, uvPlane, w, w, h);
        }
        finally
        {
            if (bufLocked) { try { buffer.Unlock(); } catch { } }
        }
    }

    private static List<CaptureFormat> EnumerateFormatsViaReader(IMFSourceReader reader)
    {
        var list = new List<CaptureFormat>();
        const int videoStream = (int)SourceReaderIndex.FirstVideoStream;
        for (int i = 0; ; i++)
        {
            IMFMediaType? mt;
            try
            {
                mt = reader.GetNativeMediaType(videoStream, i);
            }
            catch (SharpGenException)
            {
                break;
            }
            if (mt == null) break;
            try
            {
                Guid sub = mt.GetGUID(MediaTypeAttributeKeys.Subtype);
                MediaFactory.MFGetAttributeSize(mt, MediaTypeAttributeKeys.FrameSize, out uint aw, out uint ah);
                double fps = 0;
                try
                {
                    MediaFactory.MFGetAttributeRatio(mt, MediaTypeAttributeKeys.FrameRate, out uint num, out uint den);
                    if (den > 0) fps = (double)num / den;
                }
                catch { }
                string subName = SubTypeName(sub);
                if (aw > 0 && ah > 0 && !string.IsNullOrEmpty(subName))
                {
                    list.Add(new CaptureFormat
                    {
                        Width = (int)aw,
                        Height = (int)ah,
                        Fps = fps,
                        SubTypeName = subName,
                        NativeTypeIndex = i,
                    });
                }
            }
            finally { mt.Dispose(); }
        }
        list.Sort((a, b) =>
        {
            int c = b.Width.CompareTo(a.Width);
            if (c != 0) return c;
            c = b.Height.CompareTo(a.Height);
            if (c != 0) return c;
            return b.Fps.CompareTo(a.Fps);
        });
        return list;
    }

    private static IMFMediaSource? CreateMediaSourceBySymbolicLink(string symbolicLink)
    {
        try
        {
            using var attr = MediaFactory.MFCreateAttributes(2);
            attr.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            attr.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, symbolicLink);
            return MediaFactory.MFCreateDeviceSource(attr);
        }
        catch (Exception ex)
        {
            Logger.Log($"CreateMediaSourceBySymbolicLink failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>既知のサブタイプのみ名前を返す。未知は空文字（リストから除外する目印）。</summary>
    private static string SubTypeName(Guid sub)
    {
        if (sub == VideoFormatGuids.NV12) return "NV12";
        if (sub == VideoFormatGuids.YUY2) return "YUY2";
        if (sub == VideoFormatGuids.Rgb32) return "RGB32";
        if (sub == VideoFormatGuids.Rgb24) return "RGB24";
        if (sub == VideoFormatGuids.Mjpg) return "MJPG";
        if (sub == VideoFormatGuids.I420) return "I420";
        if (sub == VideoFormatGuids.Iyuv) return "IYUV";
        if (sub == VideoFormatGuids.Uyvy) return "UYVY";
        if (sub == VideoFormatGuids.H264) return "H264";
        return "";
    }
}
