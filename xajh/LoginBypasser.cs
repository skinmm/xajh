using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Login bypass for Cloud_xajhfuzhu.exe (易语言 program).
    ///
    /// The login window (class MAIN_WIN) has custom-drawn controls:
    ///   - Tabs: 用户登录 | 用户注册 | 用户充值 | 用户改密
    ///   - Fields: 用户账号, 用户密码
    ///   - Buttons: 绑定 (left), 试用 (center), 登录 (right)
    ///
    /// We click the 试用 (Trial) button to enter without credentials.
    /// </summary>
    public static class LoginBypasser
    {
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP   = 0x0202;
        const uint WM_CLOSE       = 0x0010;
        const uint BM_CLICK       = 0x00F5;
        const uint MK_LBUTTON     = 0x0001;
        const int  SW_RESTORE     = 9;
        const string MAIN_WIN_CLASS = "MAIN_WIN";

        public static (IntPtr hProcess, IntPtr moduleBase) Bypass()
        {
            Console.WriteLine("[*] Login Bypasser — Cloud_xajhfuzhu.exe");
            Console.WriteLine("[*] Waiting for Cloud_xajhfuzhu.exe ...");

            Process proc = WaitForProcess("Cloud_xajhfuzhu", timeoutSeconds: 60);
            if (proc == null)
            {
                Console.WriteLine("[!] Cloud_xajhfuzhu.exe not found. Please start it first.");
                return (IntPtr.Zero, IntPtr.Zero);
            }

            IntPtr moduleBase = proc.MainModule.BaseAddress;
            IntPtr hProcess = MemoryHelper.OpenProcess(
                MemoryHelper.PROCESS_ALL_ACCESS, false, proc.Id);

            if (hProcess == IntPtr.Zero)
            {
                Console.WriteLine("[!] Cannot open process — run as Administrator.");
                return (IntPtr.Zero, IntPtr.Zero);
            }

            Console.WriteLine($"[+] Attached  PID={proc.Id}  Base=0x{moduleBase.ToInt64():X}");

            // Find and click the 试用 (Trial) button
            bool clicked = ClickTrialButton(proc.Id);

            if (clicked)
            {
                Console.WriteLine("[+] 试用 (Trial) button clicked.");
                Thread.Sleep(2000);
                DismissErrorDialogs(proc.Id);
            }
            else
            {
                Console.WriteLine("[!] Could not find login window.");
            }

            return (hProcess, moduleBase);
        }

        /// <summary>
        /// Finds the MAIN_WIN login window and clicks the 试用 (Trial) button.
        ///
        /// Button layout (bottom row of the login form):
        ///   [ 绑定 ]   [ 试用 ]   [ 登录 ]
        ///    ~25%        ~50%       ~75%    (horizontal)
        ///    ~88%        ~88%       ~88%    (vertical)
        /// </summary>
        static bool ClickTrialButton(int pid)
        {
            Console.WriteLine("[*] Looking for MAIN_WIN login window ...");

            IntPtr mainWin = IntPtr.Zero;

            for (int wait = 0; wait < 30; wait++)
            {
                Thread.Sleep(500);
                mainWin = FindMainWindow(pid);
                if (mainWin != IntPtr.Zero) break;
            }

            if (mainWin == IntPtr.Zero)
            {
                Console.WriteLine("[*] MAIN_WIN not found.");
                return false;
            }

            GetClientRect(mainWin, out RECT clientRect);
            int winW = clientRect.Right - clientRect.Left;
            int winH = clientRect.Bottom - clientRect.Top;

            Console.WriteLine($"[+] Found MAIN_WIN  HWND=0x{mainWin.ToInt64():X}  size={winW}x{winH}");

            // Bring window to front
            ShowWindow(mainWin, SW_RESTORE);
            SetForegroundWindow(mainWin);
            Thread.Sleep(500);

            // Click 试用 (Trial) button — center button, bottom row
            // Position: approximately 50% from left, 88% from top
            int trialX = winW / 2;
            int trialY = (int)(winH * 0.88);

            Console.WriteLine($"[*] Clicking 试用 (Trial) at ({trialX}, {trialY}) ...");
            ClickAt(mainWin, trialX, trialY);

            return true;
        }

        static void ClickAt(IntPtr hWnd, int x, int y)
        {
            IntPtr lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
            PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
            Thread.Sleep(80);
            PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
        }

        static IntPtr FindMainWindow(int pid)
        {
            uint targetPid = (uint)pid;
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint wndPid);
                if (wndPid != targetPid) return true;

                var clsBuf = new StringBuilder(256);
                GetClassName(hWnd, clsBuf, clsBuf.Capacity);
                if (clsBuf.ToString() == MAIN_WIN_CLASS && IsWindowVisible(hWnd))
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        static Process WaitForProcess(string name, int timeoutSeconds)
        {
            for (int i = 0; i < timeoutSeconds * 2; i++)
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) return procs[0];
                Thread.Sleep(500);
            }
            return null;
        }

        static void DismissErrorDialogs(int pid)
        {
            uint targetPid = (uint)pid;
            var dialogs = new List<IntPtr>();

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint wndPid);
                if (wndPid != targetPid) return true;

                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "#32770")
                    dialogs.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            foreach (var dlg in dialogs)
            {
                Console.WriteLine($"  [*] Dismissing dialog 0x{dlg.ToInt64():X}");

                EnumChildWindows(dlg, (child, __) =>
                {
                    var cc = new StringBuilder(128);
                    GetClassName(child, cc, cc.Capacity);
                    if (cc.ToString().ToLower().Contains("button"))
                    {
                        SendMessage(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                Thread.Sleep(200);
                PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
