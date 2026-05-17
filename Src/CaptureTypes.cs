namespace MediaPreviewSan;

public enum SamplerKind
{
    Point,
    Bilinear,
    Anisotropic,
}

public class DeviceInfo
{
    public string Name { get; init; } = "";

    /// <summary>MF で開く際の SymbolicLink。MF 非対応デバイスでは空。</summary>
    public string SymbolicLink { get; init; } = "";

    /// <summary>DirectShow の moniker display name。DS 経由で開けるデバイス（仮想カメラ含む）。</summary>
    public string DirectShowDevicePath { get; init; } = "";

    /// <summary>MF で開けないが DS でのみ開けるデバイス（仮想カメラ等）。</summary>
    public bool IsDirectShowOnly =>
        string.IsNullOrEmpty(SymbolicLink) && !string.IsNullOrEmpty(DirectShowDevicePath);

    /// <summary>設定保存用の一意 ID。MF 対応なら SymbolicLink、そうでなければ DS device path。</summary>
    public string PersistentId =>
        !string.IsNullOrEmpty(SymbolicLink) ? SymbolicLink : DirectShowDevicePath;
}

public class CaptureFormat : IEquatable<CaptureFormat>
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string SubTypeName { get; init; } = "";
    public int NativeTypeIndex { get; init; } = -1;

    public override string ToString()
    {
        string fpsStr = Fps > 0 ? $" @ {Fps:0.##}fps" : "";
        string sub = string.IsNullOrEmpty(SubTypeName) ? "" : $" [{SubTypeName}]";
        return $"{Width}x{Height}{fpsStr}{sub}";
    }

    public bool Equals(CaptureFormat? other)
    {
        if (other is null) return false;
        return Width == other.Width && Height == other.Height
            && Math.Abs(Fps - other.Fps) < 0.01
            && SubTypeName == other.SubTypeName;
    }

    public override bool Equals(object? obj) => Equals(obj as CaptureFormat);
    public override int GetHashCode() => HashCode.Combine(Width, Height, Fps, SubTypeName);
}

public interface ICaptureService : IDisposable
{
    bool IsRunning { get; }
    bool HasFrame { get; }
    System.Drawing.Size ActualVideoSize { get; }
    /// <summary>実測フレームレート。</summary>
    double ActualFps { get; }
    /// <summary>適用された解像度プリセットの公称フレームレート。</summary>
    double NominalFps { get; }
    void Stop();
    void Resize(Rectangle bounds);
    void SetDrawRect(Rectangle rect);
    void SetSampler(SamplerKind kind);
}
