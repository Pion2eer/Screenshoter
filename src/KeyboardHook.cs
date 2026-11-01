using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Screenshoter
{
    // Низкоуровневый хук клавиатуры: перехватывает Win+Shift+S (и по желанию PrtSc),
    // чтобы вместо системных «Ножниц» открывался наш скриншотер.
    class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        const uint LLKHF_INJECTED = 0x10;
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_S = 0x53, VK_V = 0x56, VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D;

        readonly Native.LowLevelKeyboardProc proc; // держим ссылку, чтобы GC не собрал делегат
        IntPtr hook;
        readonly Control invoker;
        bool sSwallowed, prtSwallowed;

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
                hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
                ready.Set();
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            });
            thread.IsBackground = true;
            thread.Name = "KeyboardHook";
            thread.Start();
            ready.WaitOne(3000);
        }

        void Raise(Action a)
        {
            if (a == null || !invoker.IsHandleCreated) return;
            invoker.BeginInvoke(a);
        }

        static void SendDummyKey()
        {
            // Неназначенная клавиша 0xE8: не даёт открыться меню «Пуск» после отпускания Win.
            Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if ((k.flags & LLKHF_INJECTED) == 0)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    int vk = (int)k.vkCode;

                    if (vk == VK_S)
                    {
                        if (down)
                        {
                            if (sSwallowed) return new IntPtr(1); // автоповтор
                            bool win = Native.IsDown(VK_LWIN) || Native.IsDown(VK_RWIN);
                            if (CatchWinShiftS && win && Native.IsDown(VK_SHIFT) && !Native.IsDown(VK_CONTROL) && !Native.IsDown(VK_MENU))
                            {
                                sSwallowed = true;
                                SendDummyKey();
                                Raise(CaptureRequested);
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
                                Raise(CaptureRequested);
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
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
