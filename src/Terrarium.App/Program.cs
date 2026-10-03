namespace Terrarium.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 视觉样式由 app.manifest 里的 Common-Controls v6 依赖声明保证，
        // 这里再调一次只是兜底，而且**必须**包在 try 里。
        //
        // Application.EnableVisualStyles() 会在 %TEMP% 下写一个激活上下文清单文件。
        // 临时目录不可写时（受限沙箱 / 只读用户配置 / 企业终端管控）它会抛
        // UnauthorizedAccessException，把程序在启动那一瞬间直接干掉，
        // 而且异常栈指向 FileStream，完全看不出真正的原因。
        // 这个坑是在发布 exe 后实测才发现的 —— 用 dotnet xxx.dll 跑不会暴露。
        try { Application.EnableVisualStyles(); }
        catch (Exception) { /* 清单已经生效，拿不到样式也照样能跑 */ }

        try { Application.SetCompatibleTextRenderingDefault(false); }
        catch (InvalidOperationException) { /* 已经有窗口创建过了，忽略 */ }

        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
        catch (Exception) { /* 清单里已声明 DPI 感知，失败无所谓 */ }

        Application.Run(new MainForm());
    }
}
