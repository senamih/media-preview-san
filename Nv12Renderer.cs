using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MediaPreviewSan;

/// <summary>
/// NV12 を D3D11 シェーダで RGB 合成し SwapChain に描画するレンダラ。
/// 専用描画スレッドを内部に持ち、SwapChain/Context は全てそのスレッドが所有する。
/// UI スレッド・ReadSample スレッドからの操作はコマンドキュー経由で渡される。
/// Waitable SwapChain により描画はディスプレイの V-sync に同期する。
/// </summary>
public sealed class Nv12Renderer : IDisposable
{
    private const string ShaderSource = """
        Texture2D<float>  yTex   : register(t0);
        Texture2D<float2> uvTex  : register(t1);
        Texture2D         bgraTex : register(t2);
        SamplerState samp        : register(s0);

        struct VS_OUT
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        VS_OUT vs_main(uint id : SV_VertexID)
        {
            VS_OUT o;
            float2 quad = float2((id << 1) & 2, id & 2);
            o.pos = float4(quad * 2.0 - 1.0, 0.0, 1.0);
            o.uv  = float2(quad.x, 1.0 - quad.y);
            return o;
        }

        float4 ps_main(VS_OUT input) : SV_TARGET
        {
            float  yRaw  = yTex.Sample(samp, input.uv);
            float2 uvRaw = uvTex.Sample(samp, input.uv);

            float y = (yRaw  * 255.0 - 16.0)  / 219.0;
            float u = (uvRaw.x * 255.0 - 128.0) / 224.0;
            float v = (uvRaw.y * 255.0 - 128.0) / 224.0;

            float r = y + 1.5748 * v;
            float g = y - 0.1873 * u - 0.4681 * v;
            float b = y + 1.8556 * u;

            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }

        float4 ps_bgra(VS_OUT input) : SV_TARGET
        {
            return float4(bgraTex.Sample(samp, input.uv).rgb, 1.0);
        }
        """;

    private readonly IntPtr _hwnd;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ManualResetEventSlim _stopEvent = new(false);
    private readonly object _pendingLock = new();

    private Thread? _renderThread;

    // 描画スレッド専有
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain2? _swapChain2;
    private IntPtr _frameWaitHandle = IntPtr.Zero;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11PixelShader? _psBgra;
    private ID3D11RasterizerState? _rasterState;
    private ID3D11SamplerState? _samPoint;
    private ID3D11SamplerState? _samLinear;
    private ID3D11SamplerState? _samAniso;
    private ID3D11Texture2D? _nv12Tex;
    private ID3D11ShaderResourceView? _srvY;
    private ID3D11ShaderResourceView? _srvUV;
    private ID3D11Texture2D? _bgraTex;
    private ID3D11ShaderResourceView? _srvBgra;
    private int _bgraW;
    private int _bgraH;

    private int _videoW;
    private int _videoH;
    private int _surfaceW;
    private int _surfaceH;
    private Rectangle _drawRect;
    private SamplerKind _sampler = SamplerKind.Bilinear;
    private bool _allowTearing;
    private bool _waitable;
    private bool _hasFrame;

    // ReadSample スレッドから書き込まれる pending フレーム
    private byte[]? _pendingY;
    private byte[]? _pendingUV;
    private byte[]? _pendingBgra;
    private int _pendingW;
    private int _pendingH;
    private int _pendingYStride;
    private int _pendingUVStride;
    private int _pendingBgraStride;
    private bool _pendingFrame;
    private int _pendingMode; // 0=none, 1=NV12, 2=BGRA
    private int _renderMode;  // 直近に適用したモード

    public Nv12Renderer(IntPtr hwnd, int surfaceW, int surfaceH)
    {
        _hwnd = hwnd;
        _surfaceW = Math.Max(1, surfaceW);
        _surfaceH = Math.Max(1, surfaceH);
        _drawRect = new Rectangle(0, 0, _surfaceW, _surfaceH);

        var startedEvent = new ManualResetEventSlim(false);
        Exception? initError = null;
        _renderThread = new Thread(() =>
        {
            try
            {
                InitializeDevice();
                CreateSwapChain();
                CreateShaders();
                CreateRasterizerAndSamplers();
            }
            catch (Exception ex)
            {
                initError = ex;
                startedEvent.Set();
                return;
            }
            startedEvent.Set();
            try { RenderLoop(); }
            catch (Exception ex) { Logger.Log($"RenderLoop crashed: {ex.Message}"); }
            finally { ReleaseResources(); }
        })
        {
            IsBackground = true,
            Name = "Nv12-Render",
        };
        _renderThread.Start();
        startedEvent.Wait(5000);
        if (initError != null) throw initError;
    }

    public bool HasFrame
    {
        get { lock (_pendingLock) return _hasFrame; }
    }

    public void SetSampler(SamplerKind kind)
    {
        _commands.Enqueue(() => _sampler = kind);
    }

    public void SetDrawRect(Rectangle rect)
    {
        _commands.Enqueue(() => _drawRect = rect);
    }

    public void Resize(int width, int height)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        _commands.Enqueue(() => DoResize(w, h));
    }

    public void UpdateNv12(IntPtr yPlane, int yStride, IntPtr uvPlane, int uvStride,
        int width, int height)
    {
        if (yPlane == IntPtr.Zero || uvPlane == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) return;

        int yBytes = yStride * height;
        int uvBytes = uvStride * (height / 2);
        lock (_pendingLock)
        {
            if (_pendingY == null || _pendingY.Length < yBytes) _pendingY = new byte[yBytes];
            if (_pendingUV == null || _pendingUV.Length < uvBytes) _pendingUV = new byte[uvBytes];
            Marshal.Copy(yPlane, _pendingY, 0, yBytes);
            Marshal.Copy(uvPlane, _pendingUV, 0, uvBytes);
            _pendingW = width;
            _pendingH = height;
            _pendingYStride = yStride;
            _pendingUVStride = uvStride;
            _pendingMode = 1;
            _pendingFrame = true;
            _hasFrame = true;
        }
    }

    /// <summary>BGRA32（OpenCV の BGR を BGRA に変換したもの）フレームを供給。</summary>
    public void UpdateBgra(IntPtr data, int stride, int width, int height)
    {
        if (data == IntPtr.Zero || width <= 0 || height <= 0) return;
        int bytes = stride * height;
        lock (_pendingLock)
        {
            if (_pendingBgra == null || _pendingBgra.Length < bytes)
                _pendingBgra = new byte[bytes];
            Marshal.Copy(data, _pendingBgra, 0, bytes);
            _pendingW = width;
            _pendingH = height;
            _pendingBgraStride = stride;
            _pendingMode = 2;
            _pendingFrame = true;
            _hasFrame = true;
        }
    }

    public void Dispose()
    {
        _stopEvent.Set();
        try { _renderThread?.Join(1500); } catch { }
        _renderThread = null;
        try { _stopEvent.Dispose(); } catch { }
    }

    // ===== 以下、描画スレッドのみが実行 =====

    private void RenderLoop()
    {
        // stop シグナルと V-sync ハンドルの両方を WaitAny で待つ。
        // これにより停止要求が来た瞬間にループを抜けられる（ハング防止）。
        WaitHandle[]? waits = null;
        AutoResetEvent? frameEvent = null;
        if (_waitable && _frameWaitHandle != IntPtr.Zero)
        {
            try
            {
                var sh = new Microsoft.Win32.SafeHandles.SafeWaitHandle(_frameWaitHandle, ownsHandle: false);
                frameEvent = new AutoResetEvent(false) { SafeWaitHandle = sh };
                waits = new WaitHandle[] { _stopEvent.WaitHandle, frameEvent };
            }
            catch (Exception ex)
            {
                Logger.Log($"frame wait handle wrap failed: {ex.Message}");
                waits = null;
            }
        }

        while (!_stopEvent.IsSet)
        {
            while (_commands.TryDequeue(out var cmd))
            {
                try { cmd(); } catch (Exception ex) { Logger.Log($"render command failed: {ex.Message}"); }
            }
            if (_stopEvent.IsSet) break;

            if (waits != null)
            {
                int wi = WaitHandle.WaitAny(waits, 100);
                if (wi == 0) break; // stop signaled
            }
            else
            {
                if (_stopEvent.Wait(8)) break;
            }
            if (_stopEvent.IsSet) break;

            ApplyPendingFrame();
            if (_stopEvent.IsSet) break;

            RenderFrame();
        }

        frameEvent?.Dispose();
    }

    private unsafe void ApplyPendingFrame()
    {
        byte[]? y, uv, bgra;
        int w, h, yS, uvS, bgraS, mode;
        lock (_pendingLock)
        {
            if (!_pendingFrame) return;
            y = _pendingY;
            uv = _pendingUV;
            bgra = _pendingBgra;
            w = _pendingW;
            h = _pendingH;
            yS = _pendingYStride;
            uvS = _pendingUVStride;
            bgraS = _pendingBgraStride;
            mode = _pendingMode;
            _pendingFrame = false;
        }
        if (w <= 0 || h <= 0) return;

        if (mode == 2)
        {
            if (bgra == null) return;
            if (_bgraTex == null || _bgraW != w || _bgraH != h)
                RecreateBgraResources(w, h);

            var bx = _context!.Map(_bgraTex!, 0, MapMode.WriteDiscard);
            try
            {
                byte* dst = (byte*)bx.DataPointer;
                int rp = bx.RowPitch;
                int rowBytes = Math.Min(w * 4, rp);
                for (int i = 0; i < h; i++)
                    Marshal.Copy(bgra, i * bgraS, (IntPtr)(dst + i * rp), rowBytes);
            }
            finally
            {
                _context.Unmap(_bgraTex!, 0);
            }
            _renderMode = 2;
            return;
        }

        if (y == null || uv == null) return;
        if (_nv12Tex == null || _videoW != w || _videoH != h)
            RecreateNv12Resources(w, h);

        var box = _context!.Map(_nv12Tex!, 0, MapMode.WriteDiscard);
        try
        {
            byte* dst = (byte*)box.DataPointer;
            int rp = box.RowPitch;
            int copyY = Math.Min(w, rp);
            for (int i = 0; i < h; i++)
            {
                Marshal.Copy(y, i * yS, (IntPtr)(dst + i * rp), copyY);
            }
            byte* uvDst = dst + rp * h;
            int copyUV = Math.Min(w, rp);
            int uvRows = h / 2;
            for (int i = 0; i < uvRows; i++)
            {
                Marshal.Copy(uv, i * uvS, (IntPtr)(uvDst + i * rp), copyUV);
            }
        }
        finally
        {
            _context.Unmap(_nv12Tex!, 0);
        }
        _renderMode = 1;
    }

    private void RenderFrame()
    {
        if (_swapChain == null || _rtv == null || _context == null || _device == null) return;

        _context.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f));

        bool nv12Ready = _renderMode == 1 && _srvY != null && _srvUV != null && _ps != null;
        bool bgraReady = _renderMode == 2 && _srvBgra != null && _psBgra != null;

        bool drawn = false;
        if (_vs != null && _rasterState != null && (nv12Ready || bgraReady))
        {
            _context.OMSetRenderTargets(_rtv);
            var vp = new Viewport(
                _drawRect.Left,
                _drawRect.Top,
                Math.Max(1, _drawRect.Width),
                Math.Max(1, _drawRect.Height),
                0f, 1f);
            _context.RSSetViewport(vp);
            _context.RSSetState(_rasterState);

            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);

            var sampler = _sampler switch
            {
                SamplerKind.Point => _samPoint,
                SamplerKind.Anisotropic => _samAniso,
                _ => _samLinear,
            };
            _context.PSSetSampler(0, sampler);

            if (bgraReady)
            {
                _context.PSSetShader(_psBgra);
                _context.PSSetShaderResources(2, new[] { _srvBgra! });
            }
            else
            {
                _context.PSSetShader(_ps);
                _context.PSSetShaderResources(0, new[] { _srvY!, _srvUV! });
            }
            _context.Draw(3, 0);
            drawn = true;
        }

        try
        {
            // syncInterval=0 + AllowTearing で v-sync ブロックを切る。
            // Waitable SwapChain ならディスプレイ FPS に同期し、それ以外なら tearing OK。
            int sync = _waitable ? 1 : 0;
            var pf = _allowTearing && !_waitable ? PresentFlags.AllowTearing : PresentFlags.None;
            _swapChain.Present(sync, pf);
        }
        catch (SharpGenException ex)
        {
            Logger.Log($"SwapChain.Present failed: {ex.ResultCode}");
        }

        // 初フレームのログ用
        if (drawn && !_loggedFirstDraw)
        {
            Logger.Log($"first frame presented ({_videoW}x{_videoH})");
            _loggedFirstDraw = true;
        }
    }

    private bool _loggedFirstDraw;

    private void DoResize(int width, int height)
    {
        if (width == _surfaceW && height == _surfaceH) return;
        _surfaceW = width;
        _surfaceH = height;

        _rtv?.Dispose(); _rtv = null;
        _backBuffer?.Dispose(); _backBuffer = null;

        var flags = ComputeSwapChainFlags();
        try
        {
            _swapChain!.ResizeBuffers(0, width, height, Format.Unknown, flags);
        }
        catch (SharpGenException ex)
        {
            Logger.Log($"ResizeBuffers failed: {ex.ResultCode}");
            return;
        }

        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(_backBuffer);
    }

    private SwapChainFlags ComputeSwapChainFlags()
    {
        var flags = SwapChainFlags.None;
        if (_allowTearing) flags |= SwapChainFlags.AllowTearing;
        if (_waitable) flags |= SwapChainFlags.FrameLatencyWaitableObject;
        return flags;
    }

    private void InitializeDevice()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };
        var flags = DeviceCreationFlags.BgraSupport;

        var result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, flags, featureLevels,
            out ID3D11Device? device, out FeatureLevel _, out ID3D11DeviceContext? context);

        if (result.Failure || device is null || context is null)
        {
            result = D3D11.D3D11CreateDevice(
                null, DriverType.Warp, flags, featureLevels,
                out device, out _, out context);
            result.CheckError();
        }

        _device = device!;
        _context = context!;

        using var dxgiDev1 = _device.QueryInterfaceOrNull<IDXGIDevice1>();
        if (dxgiDev1 != null)
        {
            try { dxgiDev1.SetMaximumFrameLatency(1); }
            catch (Exception ex) { Logger.Log($"SetMaximumFrameLatency failed: {ex.Message}"); }
        }
    }

    private void CreateSwapChain()
    {
        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
        {
            if (factory5 != null)
            {
                try { _allowTearing = factory5.PresentAllowTearing; }
                catch { _allowTearing = false; }
            }
        }
        Logger.Log($"AllowTearing supported: {_allowTearing}");

        // Waitable SwapChain を優先（ディスプレイ FPS に同期した正確な V-sync 待機）
        _waitable = true;

        var desc = new SwapChainDescription1
        {
            Width = _surfaceW,
            Height = _surfaceH,
            Format = Format.B8G8R8A8_UNorm,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = ComputeSwapChainFlags(),
        };

        try
        {
            _swapChain = factory.CreateSwapChainForHwnd(_device!, _hwnd, desc);
        }
        catch (SharpGenException ex)
        {
            Logger.Log($"CreateSwapChain(Waitable) failed: {ex.ResultCode}, retry without Waitable");
            _waitable = false;
            desc.Flags = ComputeSwapChainFlags();
            _swapChain = factory.CreateSwapChainForHwnd(_device!, _hwnd, desc);
        }
        factory.MakeWindowAssociation(_hwnd,
            WindowAssociationFlags.IgnoreAll | WindowAssociationFlags.IgnoreAltEnter);

        if (_waitable)
        {
            _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (_swapChain2 != null)
            {
                try
                {
                    _swapChain2.MaximumFrameLatency = 1;
                    _frameWaitHandle = _swapChain2.FrameLatencyWaitableObject;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Waitable setup failed: {ex.Message}");
                    _waitable = false;
                    _frameWaitHandle = IntPtr.Zero;
                }
            }
            else
            {
                _waitable = false;
            }
        }
        Logger.Log($"Waitable swap chain: {_waitable}");

        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(_backBuffer);
    }

    private void CreateShaders()
    {
        Compiler.Compile(ShaderSource, "vs_main", "Nv12Renderer", "vs_5_0",
            out Blob? vsBlob, out Blob? vsErr);
        if (vsBlob == null) throw new InvalidOperationException(
            "頂点シェーダのコンパイルに失敗: " + (vsErr?.AsString() ?? "(no message)"));

        Compiler.Compile(ShaderSource, "ps_main", "Nv12Renderer", "ps_5_0",
            out Blob? psBlob, out Blob? psErr);
        if (psBlob == null) throw new InvalidOperationException(
            "ピクセルシェーダのコンパイルに失敗: " + (psErr?.AsString() ?? "(no message)"));

        Compiler.Compile(ShaderSource, "ps_bgra", "Nv12Renderer", "ps_5_0",
            out Blob? psBgraBlob, out Blob? psBgraErr);
        if (psBgraBlob == null) throw new InvalidOperationException(
            "BGRA ピクセルシェーダのコンパイルに失敗: " + (psBgraErr?.AsString() ?? "(no message)"));

        _vs = _device!.CreateVertexShader(vsBlob.AsBytes());
        _ps = _device.CreatePixelShader(psBlob.AsBytes());
        _psBgra = _device.CreatePixelShader(psBgraBlob.AsBytes());

        vsBlob.Dispose();
        psBlob.Dispose();
        psBgraBlob.Dispose();
        vsErr?.Dispose();
        psErr?.Dispose();
        psBgraErr?.Dispose();
    }

    private void CreateRasterizerAndSamplers()
    {
        _rasterState = _device!.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
        });

        var common = new SamplerDescription
        {
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };

        _samPoint = _device.CreateSamplerState(common with { Filter = Filter.MinMagMipPoint, MaxAnisotropy = 1 });
        _samLinear = _device.CreateSamplerState(common with { Filter = Filter.MinMagMipLinear, MaxAnisotropy = 1 });
        _samAniso = _device.CreateSamplerState(common with { Filter = Filter.Anisotropic, MaxAnisotropy = 16 });
    }

    private void RecreateNv12Resources(int width, int height)
    {
        _srvY?.Dispose(); _srvY = null;
        _srvUV?.Dispose(); _srvUV = null;
        _nv12Tex?.Dispose(); _nv12Tex = null;

        width = (width + 1) & ~1;
        height = (height + 1) & ~1;

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _nv12Tex = _device!.CreateTexture2D(desc);

        _srvY = _device.CreateShaderResourceView(_nv12Tex, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });
        _srvUV = _device.CreateShaderResourceView(_nv12Tex, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });

        _videoW = width;
        _videoH = height;
    }

    private void RecreateBgraResources(int width, int height)
    {
        _srvBgra?.Dispose(); _srvBgra = null;
        _bgraTex?.Dispose(); _bgraTex = null;

        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _bgraTex = _device!.CreateTexture2D(desc);
        _srvBgra = _device.CreateShaderResourceView(_bgraTex, new ShaderResourceViewDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });
        _bgraW = width;
        _bgraH = height;
    }

    private void ReleaseResources()
    {
        _srvY?.Dispose(); _srvY = null;
        _srvUV?.Dispose(); _srvUV = null;
        _nv12Tex?.Dispose(); _nv12Tex = null;
        _srvBgra?.Dispose(); _srvBgra = null;
        _bgraTex?.Dispose(); _bgraTex = null;
        _samPoint?.Dispose(); _samPoint = null;
        _samLinear?.Dispose(); _samLinear = null;
        _samAniso?.Dispose(); _samAniso = null;
        _rasterState?.Dispose(); _rasterState = null;
        _vs?.Dispose(); _vs = null;
        _ps?.Dispose(); _ps = null;
        _psBgra?.Dispose(); _psBgra = null;
        _rtv?.Dispose(); _rtv = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        _swapChain2?.Dispose(); _swapChain2 = null;
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    private static class Win32
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }
}
