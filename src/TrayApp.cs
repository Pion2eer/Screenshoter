using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Screenshoter
{
    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "Screenshoter";

        readonly Settings settings;
        readonly Batch batch;
        readonly Control invoker;
        readonly KeyboardHook hook;
        readonly NotifyIcon tray;
        bool overlayOpen;

        ToolStripMenuItem miNewBatch, miFiles, miStitch, miNumbers, miResetAfterPaste, miWinShiftS, miPrtSc, miAutostart, miSystemSnip;

        public TrayApp(bool updated)
        {
            settings = Settings.Load();
            batch = new Batch(settings);
            Batch.Cleanup();

            invoker = new Control();
            IntPtr h = invoker.Handle; // создаём окно для BeginInvoke

            hook = new KeyboardHook(invoker);
            ApplyHookSettings();
            hook.CaptureRequested += StartCapture;
            hook.PasteDetected += delegate
            {
                if (overlayOpen) return; // Ctrl+V внутри подписи не считается вставкой пачки
                batch.MarkPasted();
                UpdateTooltip();
            };

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.ContextMenuStrip = BuildMenu();
            tray.DoubleClick += delegate { StartCapture(); };
            tray.Visible = true;
            UpdateTooltip();

            if (!hook.Installed)
                Toast.ShowText("Не удалось перехватить клавиши. Скрин можно сделать двойным кликом по иконке в трее.");
            else if (updated)
                Toast.ShowText("Скриншотер обновлён до версии " + Updater.CurrentText + ".");
            else
                Toast.ShowText("Скриншотер запущен. " + HotkeyText() + " — сделать скрин.");

            Updater.Exit = ExitThread;
            // тихая проверка обновлений через полминуты после запуска: сообщим, только если вышла новая версия
            Timer updTimer = new Timer();
            updTimer.Interval = 30000;
            updTimer.Tick += delegate { updTimer.Stop(); updTimer.Dispose(); Updater.Check(invoker, true); };
            updTimer.Start();
        }

        string HotkeyText()
        {
            if (settings.WinShiftS && settings.PrintScreen) return "Win+Shift+S или PrtSc";
            if (settings.PrintScreen) return "PrtSc";
            if (settings.WinShiftS) return "Win+Shift+S";
            return "Двойной клик по иконке";
        }

        void ApplyHookSettings()
        {
            hook.CatchWinShiftS = settings.WinShiftS;
            hook.CatchPrintScreen = settings.PrintScreen;
        }

        void UpdateTooltip()
        {
            int n = batch.Count;
            string t = "Скриншотер · " + (n == 0 ? "пачка пуста" : ("в пачке: " + n));
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void StartCapture()
        {
            if (overlayOpen) return;
            overlayOpen = true;
            try
            {
                Rectangle virt = SystemInformation.VirtualScreen;
                using (Bitmap shot = OverlayForm.CaptureScreen(virt))
                using (OverlayForm f = new OverlayForm(shot, virt, batch.NextNumber, settings.NumberScreens))
                {
                    f.ResetBatch = delegate { batch.StartNew(); };
                    f.ShowDialog();
                    if (f.DialogResult == DialogResult.OK && f.Result != null)
                    {
                        int n;
                        using (Bitmap res = f.Result) n = batch.Add(res);
                        string mode = settings.Stitch ? " (склеены в одну картинку)" : "";
                        Toast.ShowText(n == 1
                            ? "Скрин 1 в буфере. Ctrl+V — вставить, " + HotkeyText() + " — добавить ещё."
                            : "В пачке " + n + " скрин(а/ов)" + mode + ". Ctrl+V — вставить все.");
                    }
                }
            }
            catch (Exception ex)
            {
                Toast.ShowText("Ошибка: " + ex.Message);
            }
            finally
            {
                overlayOpen = false;
                UpdateTooltip();
            }
        }

        ToolStripMenuItem Item(ContextMenuStrip m, string text, EventHandler click)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            i.Click += click;
            m.Items.Add(i);
            return i;
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem shot = Item(m, "Сделать скриншот", delegate { invoker.BeginInvoke(new Action(StartCapture)); });
            shot.Font = new Font(shot.Font, FontStyle.Bold);
            miNewBatch = Item(m, "Начать новую пачку", delegate { batch.StartNew(); UpdateTooltip(); Toast.ShowText("Пачка очищена."); });
            m.Items.Add(new ToolStripSeparator());

            miFiles = Item(m, "Вставлять отдельными картинками", delegate { SetStitch(false); });
            miStitch = Item(m, "Склеивать в одну картинку", delegate { SetStitch(true); });
            m.Items.Add(new ToolStripSeparator());

            miNumbers = Item(m, "Номер скрина в углу", delegate { settings.NumberScreens = !settings.NumberScreens; settings.Save(); });
            miResetAfterPaste = Item(m, "После Ctrl+V начинать новую пачку", delegate { settings.ResetAfterPaste = !settings.ResetAfterPaste; settings.Save(); });
            miWinShiftS = Item(m, "Перехватывать Win+Shift+S", delegate { settings.WinShiftS = !settings.WinShiftS; settings.Save(); ApplyHookSettings(); });
            miPrtSc = Item(m, "Перехватывать PrtSc", delegate { settings.PrintScreen = !settings.PrintScreen; settings.Save(); ApplyHookSettings(); });
            miAutostart = Item(m, "Запускать вместе с Windows", delegate { SetAutostart(!IsAutostart()); });
            miSystemSnip = Item(m, "Отключить системные «Ножницы» на Win+Shift+S", delegate { ToggleSystemSnip(); });
            m.Items.Add(new ToolStripSeparator());

            Item(m, "Открыть папку со скриншотами", delegate
            {
                try { Directory.CreateDirectory(Batch.Root); Process.Start("explorer.exe", "\"" + Batch.Root + "\""); } catch { }
            });
            Item(m, "Проверить обновления (версия " + Updater.CurrentText + ")", delegate { Updater.Check(invoker, false); });
            Item(m, "Выход", delegate { ExitThread(); });

            m.Opening += delegate
            {
                int n = batch.Count;
                miNewBatch.Text = "Начать новую пачку" + (n > 0 ? " (сейчас скринов: " + n + ")" : "");
                miNewBatch.Enabled = n > 0;
                miFiles.Checked = !settings.Stitch;
                miStitch.Checked = settings.Stitch;
                miNumbers.Checked = settings.NumberScreens;
                miResetAfterPaste.Checked = settings.ResetAfterPaste;
                miWinShiftS.Checked = settings.WinShiftS;
                miPrtSc.Checked = settings.PrintScreen;
                miAutostart.Checked = IsAutostart();
                miSystemSnip.Checked = IsSystemSnipDisabled();
            };
            return m;
        }

        void SetStitch(bool stitch)
        {
            settings.Stitch = stitch;
            settings.Save();
            if (batch.Count > 0)
            {
                try { batch.PutToClipboard(); } catch { }
            }
        }

        static bool IsAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex) { Toast.ShowText("Не получилось изменить автозапуск: " + ex.Message); }
        }

        // ----- отключение системного Win+Shift+S -----
        // Проводник не регистрирует сочетания Win+<буква> для букв из значения DisabledHotkeys.
        // Буква S отключает Win+Shift+S (и заодно Win+S — поиск). Нужен перезапуск Проводника.
        const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        static string GetDisabledHotkeys()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AdvancedKey))
                {
                    object v = k == null ? null : k.GetValue("DisabledHotkeys");
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }

        static bool IsSystemSnipDisabled()
        {
            return GetDisabledHotkeys().ToUpperInvariant().IndexOf('S') >= 0;
        }

        void ToggleSystemSnip()
        {
            bool disable = !IsSystemSnipDisabled();
            string question = disable
                ? "Отключить системные «Ножницы» на Win+Shift+S?\n\n" +
                  "Заодно перестанет работать Win+S (поиск), сам поиск через кнопку «Пуск» останется.\n" +
                  "Проводник перезапустится: панель задач мигнёт, открытые папки закроются."
                : "Вернуть системные «Ножницы» на Win+Shift+S и поиск на Win+S?\n\nПроводник перезапустится.";
            if (MessageBox.Show(question, "Скриншотер", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                string cur = GetDisabledHotkeys();
                string next = disable ? cur + "S" : cur.Replace("S", "").Replace("s", "");
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AdvancedKey))
                {
                    if (next.Length == 0) k.DeleteValue("DisabledHotkeys", false);
                    else k.SetValue("DisabledHotkeys", next, RegistryValueKind.String);
                }
                RestartExplorer();
                Toast.ShowText(disable ? "Системные «Ножницы» отключены." : "Системные «Ножницы» возвращены.");
            }
            catch (Exception ex) { Toast.ShowText("Не получилось: " + ex.Message); }
        }

        static void RestartExplorer()
        {
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
                p.Dispose();
            }
            // Обычно Windows сама поднимает Проводник; если нет — запускаем вручную.
            System.Threading.Thread.Sleep(2500);
            if (Process.GetProcessesByName("explorer").Length == 0) Process.Start("explorer.exe");
        }

        static Icon MakeIcon()
        {
            try
            {
                using (Stream st = typeof(TrayApp).Assembly.GetManifestResourceStream("Screenshoter.app.ico"))
                    if (st != null) return new Icon(st, SystemInformation.SmallIconSize);
            }
            catch { }
            using (Bitmap b = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 221, 0)))
                        g.FillRectangle(bg, 1, 1, 30, 30);
                    using (Pen p = new Pen(Color.FromArgb(25, 25, 25), 3))
                    {
                        // уголки рамки
                        g.DrawLines(p, new Point[] { new Point(6, 13), new Point(6, 6), new Point(13, 6) });
                        g.DrawLines(p, new Point[] { new Point(19, 6), new Point(26, 6), new Point(26, 13) });
                        g.DrawLines(p, new Point[] { new Point(26, 19), new Point(26, 26), new Point(19, 26) });
                        g.DrawLines(p, new Point[] { new Point(13, 26), new Point(6, 26), new Point(6, 19) });
                    }
                    using (SolidBrush d = new SolidBrush(Color.FromArgb(25, 25, 25)))
                        g.FillRectangle(d, 12, 12, 8, 8);
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }

        protected override void ExitThreadCore()
        {
            hook.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
