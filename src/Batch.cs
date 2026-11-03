using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Screenshoter
{
    // Пачка скриншотов. Каждый скрин сохраняется PNG-файлом, а в буфер обмена кладётся
    // список файлов (как при Ctrl+C в Проводнике). Чаты ИИ при Ctrl+V прикрепляют их все.
    class Batch
    {
        const string MarkerFormat = "ScreenshoterBatchId";
        public static readonly string Root = Path.Combine(Path.GetTempPath(), "Screenshoter");

        readonly Settings settings;
        readonly List<string> files = new List<string>();
        string dir;
        string batchId = "";
        bool pasted;
        List<string> previous = new List<string>(); // прошлая пачка, чтобы её можно было вернуть
        public bool StartedAfterPaste;              // последний Add начал новую пачку, потому что старую вставили

        public Batch(Settings settings) { this.settings = settings; }

        // Пачка «живая», пока в ней есть скрины и её не вставили по Ctrl+V.
        // Что лежит в буфере, на пачку НЕ влияет: другие программы (Parsec, менеджеры буфера,
        // обычное копирование текста) перезаписывают буфер, и раньше из-за этого пачка
        // молча начиналась заново. Теперь следующий скрин просто снова кладёт всю пачку в буфер.
        public bool IsAlive
        {
            get { return files.Count > 0 && !pasted; }
        }

        public int Count { get { return IsAlive ? files.Count : 0; } }
        public int NextNumber { get { return Count + 1; } }

        bool ClipboardIsOurs()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    IDataObject d = Clipboard.GetDataObject();
                    if (d == null || !d.GetDataPresent(MarkerFormat)) return false;
                    object v = d.GetData(MarkerFormat);
                    return v != null && v.ToString() == batchId;
                }
                catch { Thread.Sleep(30); }
            }
            return true; // буфер занят кем-то, не рушим пачку из-за этого
        }

        void Reset()
        {
            if (files.Count > 0) previous = new List<string>(files);
            files.Clear();
            pasted = false;
            batchId = Guid.NewGuid().ToString("N");
            dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        }

        public int Add(Bitmap shot)
        {
            StartedAfterPaste = false;
            if (!IsAlive)
            {
                StartedAfterPaste = pasted && files.Count > 0;
                Reset();
            }
            Directory.CreateDirectory(dir);
            int n = files.Count + 1;
            string path = Path.Combine(dir, "screenshot-" + n + ".png");
            for (int k = 2; File.Exists(path); k++)
                path = Path.Combine(dir, "screenshot-" + n + "-" + k + ".png");
            shot.Save(path, ImageFormat.Png);
            files.Add(path);
            PutToClipboard();
            return n;
        }

        public void PutToClipboard()
        {
            if (files.Count == 0) return;
            DataObject d = new DataObject();
            Bitmap stitched = null;
            try
            {
                if (settings.Stitch)
                {
                    stitched = Stitch();
                    d.SetData(DataFormats.Bitmap, true, stitched);
                    MemoryStream png = new MemoryStream();
                    stitched.Save(png, ImageFormat.Png);
                    d.SetData("PNG", false, png);
                }
                else
                {
                    StringCollection list = new StringCollection();
                    list.AddRange(files.ToArray());
                    d.SetFileDropList(list);
                    d.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 1, 0, 0, 0 })); // копирование
                }
                d.SetData(MarkerFormat, batchId);
                Clipboard.SetDataObject(d, true, 10, 100);
            }
            finally
            {
                if (stitched != null) stitched.Dispose();
            }
        }

        Bitmap Stitch()
        {
            List<Bitmap> imgs = new List<Bitmap>();
            try
            {
                foreach (string f in files)
                {
                    using (FileStream fs = File.OpenRead(f))
                    using (Bitmap tmp = new Bitmap(fs))
                        imgs.Add(new Bitmap(tmp));
                }
                const int gap = 16;
                int w = 0, h = 0;
                foreach (Bitmap b in imgs) { w = Math.Max(w, b.Width); h += b.Height; }
                h += gap * (imgs.Count - 1);
                Bitmap res = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(res))
                {
                    g.Clear(Color.FromArgb(60, 60, 60));
                    int y = 0;
                    foreach (Bitmap b in imgs)
                    {
                        g.DrawImage(b, new Rectangle(0, y, b.Width, b.Height));
                        y += b.Height + gap;
                    }
                }
                return res;
            }
            finally
            {
                foreach (Bitmap b in imgs) b.Dispose();
            }
        }

        public void MarkPasted()
        {
            if (settings.ResetAfterPaste && files.Count > 0 && !pasted && ClipboardIsOurs()) pasted = true;
        }

        public void StartNew()
        {
            try { if (files.Count > 0 && ClipboardIsOurs()) Clipboard.Clear(); } catch { }
            if (files.Count > 0) previous = new List<string>(files);
            files.Clear();
            pasted = false;
            batchId = "";
        }

        public int PreviousCount { get { return ExistingPrevious().Count; } }

        List<string> ExistingPrevious()
        {
            List<string> res = new List<string>();
            foreach (string f in previous) if (File.Exists(f)) res.Add(f);
            return res;
        }

        // Вернуть прошлую пачку: она снова становится текущей и кладётся в буфер.
        public int RestorePrevious()
        {
            List<string> prev = ExistingPrevious();
            if (prev.Count == 0) return 0;
            List<string> cur = new List<string>(files);
            files.Clear();
            files.AddRange(prev);
            previous = cur;
            pasted = false;
            batchId = Guid.NewGuid().ToString("N");
            dir = Path.GetDirectoryName(prev[prev.Count - 1]);
            PutToClipboard();
            return files.Count;
        }

        public static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (string d in Directory.GetDirectories(Root))
                {
                    try
                    {
                        if (Directory.GetCreationTime(d) < DateTime.Now.AddDays(-3)) Directory.Delete(d, true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
