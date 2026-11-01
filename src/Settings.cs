using System;
using System.Collections.Generic;
using System.IO;

namespace Screenshoter
{
    class Settings
    {
        public bool WinShiftS = true;
        public bool PrintScreen = false;
        public bool Stitch = false;          // склеивать пачку в одну картинку вместо файлов
        public bool NumberScreens = true;    // номер скрина в углу
        public bool ResetAfterPaste = true;  // после Ctrl+V следующий скрин начинает новую пачку

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenshoter"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                s.WinShiftS = Get(d, "WinShiftS", s.WinShiftS);
                s.PrintScreen = Get(d, "PrintScreen", s.PrintScreen);
                s.Stitch = Get(d, "Stitch", s.Stitch);
                s.NumberScreens = Get(d, "NumberScreens", s.NumberScreens);
                s.ResetAfterPaste = Get(d, "ResetAfterPaste", s.ResetAfterPaste);
            }
            catch { }
            return s;
        }

        static bool Get(Dictionary<string, string> d, string key, bool def)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new string[] {
                    "WinShiftS=" + (WinShiftS ? 1 : 0),
                    "PrintScreen=" + (PrintScreen ? 1 : 0),
                    "Stitch=" + (Stitch ? 1 : 0),
                    "NumberScreens=" + (NumberScreens ? 1 : 0),
                    "ResetAfterPaste=" + (ResetAfterPaste ? 1 : 0),
                });
            }
            catch { }
        }
    }
}
