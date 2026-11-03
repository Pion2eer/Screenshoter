using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Screenshoter
{
    // Обновление из GitHub Releases: берём последний релиз, сравниваем номер версии,
    // скачиваем Screenshoter.exe, подменяем файл и перезапускаемся.
    static class Updater
    {
        const string Repo = "Pion2eer/Screenshoter";
        const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        public const string AfterUpdateArg = "--after-update";

        static bool busy;
        public static Action Exit;   // корректно закрыть программу (иконка в трее, перехват клавиш)

        public static Version Current
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string CurrentText
        {
            get { Version v = Current; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        class Release
        {
            public Version Version;
            public string Tag;
            public string ExeUrl;
        }

        static WebClient Client()
        {
            // .NET 4.8 по умолчанию может не включать TLS 1.2, а GitHub без него не пускает
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            WebClient c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = "Screenshoter-updater";
            c.Encoding = System.Text.Encoding.UTF8;
            return c;
        }

        static Release Fetch()
        {
            string json;
            using (WebClient c = Client())
            {
                c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = c.DownloadString(ApiUrl);
            }
            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tag.Success) throw new Exception("в ответе GitHub нет номера версии");
            Match ver = Regex.Match(tag.Groups[1].Value, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!ver.Success) throw new Exception("непонятный номер версии «" + tag.Groups[1].Value + "»");
            Release r = new Release();
            r.Tag = tag.Groups[1].Value;
            r.Version = new Version(int.Parse(ver.Groups[1].Value), int.Parse(ver.Groups[2].Value),
                                    ver.Groups[3].Success ? int.Parse(ver.Groups[3].Value) : 0);
            Match exe = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+?\\.exe)\"", RegexOptions.IgnoreCase);
            if (exe.Success) r.ExeUrl = exe.Groups[1].Value;
            return r;
        }

        static bool IsNewer(Version v)
        {
            Version c = Current;
            return v > new Version(c.Major, c.Minor, Math.Max(c.Build, 0));
        }

        static string Describe(Exception e)
        {
            WebException we = e as WebException;
            if (we != null)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound)
                    return "на GitHub пока нет ни одного релиза (или репозиторий закрыт).";
                if (resp != null && (int)resp.StatusCode == 403)
                    return "GitHub временно ограничил запросы, попробуй позже.";
                return "нет связи с GitHub (" + we.Message + ").";
            }
            return e.Message;
        }

        // Проверка в фоне. silent = true — при старте программы: молчим, если обновлений нет или нет сети.
        public static void Check(Control ui, bool silent)
        {
            if (busy) return;
            busy = true;
            Thread t = new Thread(delegate()
            {
                Release r = null; Exception err = null;
                try { r = Fetch(); } catch (Exception e) { err = e; }
                try { ui.BeginInvoke(new Action(delegate { busy = false; OnChecked(ui, r, err, silent); })); }
                catch { busy = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void OnChecked(Control ui, Release r, Exception err, bool silent)
        {
            if (err != null)
            {
                if (!silent) Toast.ShowText("Не удалось проверить обновления: " + Describe(err));
                return;
            }
            if (!IsNewer(r.Version))
            {
                if (!silent) Toast.ShowText("У тебя последняя версия (" + CurrentText + ").");
                return;
            }
            if (silent)
            {
                Toast.ShowText("Вышла версия " + r.Tag + ". Обновить: меню в трее → «Проверить обновления».");
                return;
            }
            if (r.ExeUrl == null)
            {
                Toast.ShowText("В релизе " + r.Tag + " нет exe. Скачай вручную: " + ReleasesPage);
                return;
            }
            DialogResult a = MessageBox.Show(
                "Вышла версия " + r.Tag + ", у тебя " + CurrentText + ".\n\nСкачать и перезапустить Скриншотер? Набранная пачка скринов начнётся заново.",
                "Скриншотер — обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
            if (a == DialogResult.Yes) Download(ui, r);
        }

        static void Download(Control ui, Release r)
        {
            busy = true;
            Toast.ShowText("Скачиваю версию " + r.Tag + "…");
            string exe = ExePath;
            string tmp = exe + ".new";
            Thread t = new Thread(delegate()
            {
                Exception err = null;
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    using (WebClient c = Client()) c.DownloadFile(r.ExeUrl, tmp);
                    if (new FileInfo(tmp).Length < 20 * 1024) throw new Exception("скачался битый файл");
                }
                catch (Exception e) { err = e; }
                try { ui.BeginInvoke(new Action(delegate { busy = false; Install(exe, tmp, err); })); }
                catch { busy = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void Install(string exe, string tmp, Exception err)
        {
            if (err != null)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                Toast.ShowText("Не удалось скачать обновление: " + Describe(err));
                return;
            }
            string old = exe + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);      // запущенный exe нельзя перезаписать, но можно переименовать
                File.Move(tmp, exe);
            }
            catch (Exception e)
            {
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                Toast.ShowText("Не удалось заменить файл программы (" + e.Message + "). Скачай вручную: " + ReleasesPage);
                return;
            }
            try
            {
                Process.Start(exe, AfterUpdateArg + " " + Process.GetCurrentProcess().Id);
            }
            catch (Exception e)
            {
                Toast.ShowText("Обновление скачано, но перезапуск не вышел (" + e.Message + "). Запусти Скриншотер заново.");
            }
            if (Exit != null) Exit(); else Application.ExitThread();
        }

        // Вызывается при старте: дождаться, пока старая копия закроется, и убрать её файл.
        public static bool HandleStartup(string[] args)
        {
            bool updated = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != AfterUpdateArg) continue;
                updated = true;
                int pid;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out pid))
                {
                    try { Process.GetProcessById(pid).WaitForExit(15000); } catch { }
                }
            }
            string old = ExePath + ".old";
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { Thread.Sleep(300); }
            }
            return updated;
        }
    }
}
