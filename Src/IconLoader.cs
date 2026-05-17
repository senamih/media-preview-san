using System.Reflection;

namespace MediaPreviewSan;

public static class IconLoader
{
    private static Icon? _cached;

    public static Icon AppIcon
    {
        get
        {
            if (_cached != null) return _cached;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        _cached = new Icon(stream);
                        return _cached;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"icon load failed: {ex.Message}");
            }
            _cached = SystemIcons.Application;
            return _cached;
        }
    }
}
