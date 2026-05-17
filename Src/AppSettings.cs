using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaPreviewSan;

public class AppSettings
{
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 640;
    public int WindowHeight { get; set; } = 480;
    public bool WindowMaximized { get; set; } = false;

    public string DeviceMonikerId { get; set; } = "";
    public string DeviceName { get; set; } = "";

    public int CaptureWidth { get; set; } = 0;
    public int CaptureHeight { get; set; } = 0;
    public double CaptureFps { get; set; } = 0;

    public bool MaintainAspectRatio { get; set; } = true;

    public string ScalingMode { get; set; } = "Bilinear";

    [JsonIgnore]
    public bool IsFirstRun { get; set; } = false;

    private static string SettingsPath
    {
        get
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "settings.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private string _lastSavedJson = "";

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                Logger.Log("settings.json not found, using defaults (first run)");
                var s = new AppSettings { IsFirstRun = true };
                return s;
            }
            string json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings { IsFirstRun = true };
            // 比較用は再シリアライズして正規化（インデント/順序差で誤検知しないように）
            loaded._lastSavedJson = JsonSerializer.Serialize(loaded, JsonOptions);
            Logger.Log($"settings loaded: device='{loaded.DeviceName}' size={loaded.CaptureWidth}x{loaded.CaptureHeight}");
            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Log($"settings load failed: {ex.Message}");
            return new AppSettings { IsFirstRun = true };
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, JsonOptions);
            if (json == _lastSavedJson) return;
            File.WriteAllText(SettingsPath, json);
            _lastSavedJson = json;
            Logger.Log("settings saved");
        }
        catch (Exception ex)
        {
            Logger.Log($"settings save failed: {ex.Message}");
        }
    }
}
