using System;
using System.Threading;
using System.Windows.Forms;

namespace Screenshoter
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.EnableDpiAwareness();
            bool updated = Updater.HandleStartup(args);   // после обновления ждём, пока закроется старая копия
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
                Application.Run(new TrayApp(updated));
            }
        }
    }
}
