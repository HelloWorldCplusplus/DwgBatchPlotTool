// DwgBatchPlotTool — DWG/DXF 批量转 PDF 的界面程序
// 作者: 唐明龙 (QQ 979123550)
using System;
using System.Windows.Forms;

namespace DwgBatchPlotTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var host = new HtmlHost();
            string url;
            if (!host.Start(out url))
            {
                MessageBox.Show("无法启动本机服务 (17845-17889 端口都被占用)。\r\n请关闭占用这些端口的程序后重试。",
                    "DWG/DXF 批量转 PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (Array.IndexOf(args, "--no-browser") >= 0)
            {
                // 无界面模式: 只把地址打印出来, 供自检/脚本使用
                Console.WriteLine("URL: " + url);
                Console.Out.Flush();
            }
            else if (!host.OpenBrowser(url))
            {
                MessageBox.Show("无法自动打开浏览器，请手动访问下面的地址:\r\n\r\n" + url,
                    "DWG/DXF 批量转 PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            host.WaitForExit();
            return 0;
        }
    }
}
