using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Screenshoter
{
    // Низкоуровневый хук клавиатуры: перехватывает Win+Shift+S (и по желанию PrtSc),
    // чтобы вместо системных «Ножниц» открывался наш скриншотер.
    //
    // Почему хук мог «отваливаться» и что с этим сделано:
    //  * Windows молча снимает низкоуровневый хук, если обработчик хоть раз ответил дольше
    //    LowLevelHooksTimeout (бывает при паузе сборщика мусора, просыпании из сна, блокировке).
    //    Уведомления об этом нет, поэтому хук переустанавливается раз в минуту, после сна,
    //    разблокировки и переподключения сеанса.
    //  * Пока активно окно, запущенное от администратора, обычному процессу хук нажатий не
    //    приходит. На этот случай сочетание ещё и регистрируется через RegisterHotKey (если
    //    его не держат системные «Ножницы») — такие сочетания работают в любом окне.
    //  * Нажатия от программ-переназначателей (PowerToys, AutoHotkey, утилиты клавиатур,
    //    удалённый рабочий стол) помечены как «внедрённые» — раньше они игнорировались.
    //  * Флаг «S уже проглочена» мог залипнуть, если отпускание S не дошло (экран блокировки,
    //    UAC), и тогда следующее нажатие терялось. Теперь залипший флаг сбрасывается.
    class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const int WM_TIMER = 0x113, WM_HOTKEY = 0x312, WM_QUIT = 0x12;
        const uint WM_REINSTALL = 0x8001, WM_REREGISTER = 0x8002; // WM_APP + n
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_S = 0x53, VK_V = 0x56, VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D, VK_DUMMY = 0xE8;
        const uint MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        const int HK_WINSHIFTS = 1, HK_PRTSC = 2;
        const int ReinstallMs = 60000;

        readonly Native.LowLevelKeyboardProc proc; // держим ссылку, чтобы GC не собрал делегат
        IntPtr hook;
        readonly Control invoker;
        bool sSwallowed, prtSwallowed;
        int sSwallowedAt;
        bool hkWinShiftS, hkPrtSc;
        int lastCapture = Environment.TickCount - 10000;

        public volatile bool CatchWinShiftS = true;
        public volatile bool CatchPrintScreen = false;
        public event Action CaptureRequested;
        public event Action PasteDetected;

        public bool Installed { get { return hook != IntPtr.Zero; } }

        readonly Thread thread;
        uint threadId;

        public KeyboardHook(Control invoker)
        {
            this.invoker = invoker;
            proc = Callback;
            // Хук живёт в отдельном потоке: пока основной поток занят (снимок экрана, отрисовка),
            // Windows не должна решить, что хук «завис», и пропустить нажатие системе.
            ManualResetEvent ready = new ManualResetEvent(false);
            thread = new Thread(delegate ()
            {
                threadId = Native.GetCurrentThreadId();
                Install();
                RegisterHotkeys();
                ready.Set();
                Native.SetTimer(IntPtr.Zero, IntPtr.Zero, ReinstallMs, IntPtr.Zero);
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero)
                    {
                        if (msg.message == WM_TIMER || msg.message == WM_REINSTALL) { Install(); RegisterHotkeys(); continue; }
                        if (msg.message == WM_REREGISTER) { RegisterHotkeys(); continue; }
                        if (msg.message == WM_HOTKEY) { OnHotkey(msg.wParam.ToInt32()); continue; }
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                UnregisterHotkeys();
                if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            });
            thread.IsBackground = true;
            thread.Name = "KeyboardHook";
            thread.Start();
            ready.WaitOne(3000);
        }

        // Ставит хук заново. Старый снимается только после того, как новый встал,
        // чтобы не было окна, когда нажатия уходят мимо.
        void Install()
        {
            IntPtr fresh = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero) return; // оставляем прежний, попробуем в следующий раз
            IntPtr old = hook;
            hook = fresh;
            if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
        }

        // Запасной путь через RegisterHotKey. Получится, только если сочетание никто не занял:
        // Win+Shift+S обычно держат «Ножницы», пока их не отключили в меню трея.
        void RegisterHotkeys()
        {
            if (CatchWinShiftS && !hkWinShiftS)
                hkWinShiftS = Native.RegisterHotKey(IntPtr.Zero, HK_WINSHIFTS, MOD_WIN | MOD_SHIFT | MOD_NOREPEAT, VK_S);
            else if (!CatchWinShiftS && hkWinShiftS)
            { Native.UnregisterHotKey(IntPtr.Zero, HK_WINSHIFTS); hkWinShiftS = false; }

            if (CatchPrintScreen && !hkPrtSc)
                hkPrtSc = Native.RegisterHotKey(IntPtr.Zero, HK_PRTSC, MOD_NOREPEAT, VK_SNAPSHOT);
            else if (!CatchPrintScreen && hkPrtSc)
            { Native.UnregisterHotKey(IntPtr.Zero, HK_PRTSC); hkPrtSc = false; }
        }

        void UnregisterHotkeys()
        {
            if (hkWinShiftS) { Native.UnregisterHotKey(IntPtr.Zero, HK_WINSHIFTS); hkWinShiftS = false; }
            if (hkPrtSc) { Native.UnregisterHotKey(IntPtr.Zero, HK_PRTSC); hkPrtSc = false; }
        }

        void OnHotkey(int id)
        {
            if (id == HK_WINSHIFTS && CatchWinShiftS) FireCapture();
            else if (id == HK_PRTSC && CatchPrintScreen) FireCapture();
        }

        // Просьба переустановить хук (после сна, разблокировки и т.п.). Можно звать из любого потока.
        public void Reinstall()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        }

        // Настройки поменялись — обновить зарегистрированные сочетания.
        public void SettingsChanged()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, WM_REREGISTER, IntPtr.Zero, IntPtr.Zero);
        }

        void FireCapture()
        {
            // защита от двойного срабатывания (хук и RegisterHotKey на одно нажатие)
            int now = Environment.TickCount;
            if (unchecked(now - lastCapture) < 400) return;
            lastCapture = now;
            Raise(CaptureRequested);
        }

        void Raise(Action a)
        {
            if (a == null || !invoker.IsHandleCreated) return;
            try { invoker.BeginInvoke(a); } catch { }
        }

        static void SendDummyKey()
        {
            // Неназначенная клавиша 0xE8: не даёт открыться меню «Пуск» после отпускания Win.
            Native.keybd_event(VK_DUMMY, 0, 0, UIntPtr.Zero);
            Native.keybd_event(VK_DUMMY, 0, 2, UIntPtr.Zero);
        }

        static bool WinShiftHeld()
        {
            bool win = Native.IsDown(VK_LWIN) || Native.IsDown(VK_RWIN);
            // Ctrl не мешает: Ctrl+Win+Shift+S тоже открывает скриншотер. Alt — мешает.
            return win && Native.IsDown(VK_SHIFT) && !Native.IsDown(VK_MENU);
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    // Читаем поля напрямую, без Marshal.PtrToStructure: меньше работы и мусора в хуке.
                    int vk = Marshal.ReadInt32(lParam, 0);
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                    if (vk == VK_S)
                    {
                        if (down)
                        {
                            if (sSwallowed)
                            {
                                // автоповтор, пока сочетание зажато; иначе флаг залип — сбрасываем
                                if (WinShiftHeld() && unchecked(Environment.TickCount - sSwallowedAt) < 30000)
                                {
                                    sSwallowedAt = Environment.TickCount;
                                    return new IntPtr(1);
                                }
                                sSwallowed = false;
                            }
                            if (CatchWinShiftS && WinShiftHeld())
                            {
                                sSwallowed = true;
                                sSwallowedAt = Environment.TickCount;
                                SendDummyKey();
                                FireCapture();
                                return new IntPtr(1);
                            }
                        }
                        else if (sSwallowed)
                        {
                            sSwallowed = false;
                            return new IntPtr(1);
                        }
                    }
                    else if (vk == VK_SNAPSHOT)
                    {
                        if (down)
                        {
                            if (prtSwallowed) return new IntPtr(1);
                            if (CatchPrintScreen && !Native.IsDown(VK_MENU) && !Native.IsDown(VK_CONTROL))
                            {
                                prtSwallowed = true;
                                FireCapture();
                                return new IntPtr(1);
                            }
                        }
                        else if (prtSwallowed)
                        {
                            prtSwallowed = false;
                            return new IntPtr(1);
                        }
                    }
                    else if (down && ((vk == VK_V && Native.IsDown(VK_CONTROL) && !Native.IsDown(VK_MENU)) ||
                                      (vk == VK_INSERT && Native.IsDown(VK_SHIFT))))
                    {
                        Raise(PasteDetected);
                    }
                }
            }
            catch { } // исключение в хуке не должно ронять процесс или ломать ввод
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
