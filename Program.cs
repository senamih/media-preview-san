namespace MediaPreviewSan;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Logger.Log("=== MediaPreviewSan starting ===");
        try
        {
            ApplicationConfiguration.Initialize();
            MediaFoundationCaptureService.GlobalStartup();
            try
            {
                var settings = AppSettings.Load();
                Application.Run(new MainForm(settings));
            }
            finally
            {
                MediaFoundationCaptureService.GlobalShutdown();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"fatal: {ex}");
            try
            {
                MessageBox.Show($"予期しないエラーが発生しました:\n{ex.Message}",
                    "MediaPreviewSan", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
        finally
        {
            Logger.Log("=== MediaPreviewSan exited ===");
        }
    }
}
