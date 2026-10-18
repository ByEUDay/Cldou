namespace Doorstop
{
    /// <summary>
    /// Doorstop 4.x 约定入口。
    ///
    /// run_cldou.sh 会把 DOORSTOP_TARGET_ASSEMBLY 指向本程序集；
    /// Doorstop 在 Mono 初始化后查找并调用 static void Doorstop.Entrypoint.Start()。
    ///
    /// 注意：本方法运行在“游戏主代码之前”（Unity 尚未初始化），
    /// 因此只做最轻量的初始化（日志 / 目录 / 程序集解析 / 后台等待线程），
    /// 真正的接管由等待线程 + GameManager.Update 帧回调完成。
    /// </summary>
    public static class Entrypoint
    {
        public static void Start()
        {
            Cldou.Bootstrap.Boot.Run();
        }
    }
}
