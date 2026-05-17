namespace MediaPreviewSan;

/// <summary>
/// デバイス一覧のプロセス内キャッシュ。
/// 起動時と「再検出」ボタン押下時のみ実列挙し、設定画面の通常オープンでは再列挙しない。
/// </summary>
public static class DeviceCache
{
    private static readonly object _lock = new();
    private static List<DeviceInfo>? _cached;
    private static readonly Dictionary<string, List<CaptureFormat>> _formatCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<DeviceInfo> Get(bool forceRefresh)
    {
        lock (_lock)
        {
            if (forceRefresh || _cached == null)
            {
                _cached = MediaFoundationCaptureService.EnumerateDevices();
                _formatCache.Clear(); // 再検出時は解像度キャッシュも無効化
            }
            return new List<DeviceInfo>(_cached);
        }
    }

    public static List<CaptureFormat> GetFormats(string id, bool forceRefresh, bool directShow = false)
    {
        if (string.IsNullOrEmpty(id)) return new List<CaptureFormat>();
        string key = (directShow ? "DS|" : "MF|") + id;
        lock (_lock)
        {
            if (!forceRefresh && _formatCache.TryGetValue(key, out var cached))
            {
                return new List<CaptureFormat>(cached);
            }
        }
        // 列挙はロック外で（時間がかかるため）
        var formats = directShow
            ? DirectShowCaptureService.EnumerateFormats(id)
            : MediaFoundationCaptureService.EnumerateFormats(id);
        lock (_lock)
        {
            _formatCache[key] = formats;
            return new List<CaptureFormat>(formats);
        }
    }

    public static bool HasCache
    {
        get { lock (_lock) return _cached != null; }
    }
}
