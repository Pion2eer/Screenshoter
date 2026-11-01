using System;
using System.Threading;
using System.Windows.Forms;

namespace Screenshoter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Native.EnableDpiAwareness();
            bool created;
            using (Mutex m = new Mutex(true, "Screenshoter_SingleInstance_5d1e", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Скриншотер уже запущен — ищи жёлтую иконку в трее.", "Скриншотер");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    try { Toast.ShowText("Ошибка: " + e.Exception.Message); } catch { }
                };
                Application.Run(new TrayApp());
            }
        }
    }
}
