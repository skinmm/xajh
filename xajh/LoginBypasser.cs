using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Automatic login handler for Cloud_xajhfuzhu.exe.
    ///
    ///  The login window uses a custom-drawn class "MAIN_WIN" with no
    /// standard Win32 child controls — username, password, and button
    /// are all owner-drawn on the window canvas.
    /// Approach: simulate mouse clicks at field positions + keyboard
    /// input via WM_CHAR to type credentials, then click Login or
    /// press Enter.
    /// </summary>
    public static class LoginBypasser
    {

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        static extern void SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const uint WM_CHAR = 0x0102;
        const uint WM_CLOSE = 0x0010;
        const uint BM_CLICK = 0x00F5;
        const int VK_TAB = 0x09;
        const int VK_RETURN = 0x0D;
        const int SW_SHOW = 5;
        const int SW_RESTORE = 9;

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_UNICODE = 0x0004;

        const int MAX_LOGIN_ATTEMPTS = 3;
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
                Console.WriteLine("[!] Cannot open process — run this tool as Administrator.");
                return (IntPtr.Zero, IntPtr.Zero);
            }

            Console.WriteLine($"[+] Attached  PID={proc.Id}  Base=0x{moduleBase.ToInt64():X}");

            // Try filling and submitting the login form (with retries)
            for (int attempt = 1; attempt <= MAX_LOGIN_ATTEMPTS; attempt++)
            {
                if (proc.HasExited)
                {
                    Console.WriteLine("[!] Cloud_xajhfuzhu.exe has exited.");
                    return (IntPtr.Zero, IntPtr.Zero);
                }

                Console.WriteLine($"[*] Login attempt {attempt}/{MAX_LOGIN_ATTEMPTS} ...");
                bool submitted = HandleLoginWindow(proc.Id);
                if (!submitted)
                {
                    Console.WriteLine("[*] No login window detected — may already be past login.");
                    break;
                }
                Thread.Sleep(2000);
                if (proc.HasExited)
                {
                    Console.WriteLine("[!] Cloud_xajhfuzhu.exe exited after login attempt.");
                    return (IntPtr.Zero, IntPtr.Zero);
                }
                bool hadError = DismissErrorDialogs(proc.Id);
                if (!hadError)
                {
                    Console.WriteLine("[+] Login submitted successfully.");
                    break;
                }

                Console.WriteLine("[*] Error dialog dismissed, retrying ...");
                Thread.Sleep(1000);
            }

            Console.WriteLine("[+] Login bypass complete.");
            

            return (hProcess, moduleBase);
        }

        /// <summary>
        /// Finds the MAIN_WIN login window and interacts with it:
        /// click on the username area, type credentials, Tab to password,
        /// type again, then click Login or press Enter.
        /// </summary>
        static bool HandleLoginWindow(int pid)
        {
            Console.WriteLine("[*] Looking for login window (MAIN_WIN) ...");

            IntPtr mainWin = IntPtr.Zero;

            for (int wait = 0; wait < 30; wait++)
            {
                Thread.Sleep(500);
                mainWin = FindMainWindow(pid);
                if (mainWin != IntPtr.Zero) break;
            }
            if (mainWin == IntPtr.Zero)
            {
                // Fall back: look for any visible top-level window
                var windows = GetProcessWindows(pid);
                Console.WriteLine($"[*] Found {windows.Count} window(s):");
                foreach (var (hwnd, title, cls, isChild) in windows)
                {
                    bool vis = IsWindowVisible(hwnd);
                    string prefix = isChild ? "    " : "  ";
                    Console.WriteLine($"{prefix}HWND=0x{hwnd.ToInt64():X}  cls={cls,-24} title=\"{title}\"  vis={vis}");
                }
                // Try standard Edit/Button controls as fallback
                return FallbackStandardControls(windows);
            }
            // Get window dimensions for click position calculation
            GetWindowRect(mainWin, out RECT winRect);
            GetClientRect(mainWin, out RECT clientRect);
            int winW = clientRect.Right - clientRect.Left;
            int winH = clientRect.Bottom - clientRect.Top;
            Console.WriteLine($"[+] Found MAIN_WIN  HWND=0x{mainWin.ToInt64():X}  size={winW}x{winH}");
            // Bring window to front
            ShowWindow(mainWin, SW_RESTORE);
            SetForegroundWindow(mainWin);
            Thread.Sleep(300);
            // --- Strategy 1: Tab + keyboard typing ---
            // Many custom UIs support Tab navigation and direct keyboard input.
            // Click center of window first to give it focus, then use Tab to
            // navigate between username → password → login button.
            // Click near upper area where username field typically is
            // (roughly center-x, 40% from top)
            ClickOnWindow(mainWin, winW / 2, (int)(winH * 0.35));
            Thread.Sleep(200);
            // Type username
            TypeString(mainWin, "admin");
            Thread.Sleep(200);
            // Tab to password field
            SendKey(mainWin, VK_TAB);
            Thread.Sleep(200);
            // Press Enter to submit (or Tab to button + Enter)
            SendKey(mainWin, VK_RETURN);
            Thread.Sleep(500);
            Console.WriteLine("  [+] Credentials typed and Enter pressed.");

            return true;
        }

        /// <summary>
        /// Simulates a left mouse click at a position relative to
        /// the window's client area using PostMessage.
        /// </summary>
        static void ClickOnWindow(IntPtr hWnd, int clientX, int clientY)
        {
            IntPtr lParam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
            PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)0x0001, lParam);
            Thread.Sleep(50);
            PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
        }

        /// <summary>
        /// Types a string into the focused control of the window
        /// by sending WM_CHAR for each character.
        /// </summary>
        static void TypeString(IntPtr hWnd, string text)
        {
            foreach (char c in text)
            {
                PostMessage(hWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(30);
            }
        }

        /// <summary>
        /// Sends a single key press (down + up) to the window.
        /// </summary>
        static void SendKey(IntPtr hWnd, int vk)
        {
            PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            Thread.Sleep(30);
            PostMessage(hWnd, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        }

        /// <summary>
        /// Finds the MAIN_WIN class window for the given process.
        /// </summary>
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
                    return false; // stop enumeration
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Fallback: if standard Edit/Button child controls are found,
        /// use the old WM_SETTEXT + BM_CLICK approach.
        /// </summary>
        static bool FallbackStandardControls(
            List<(IntPtr hwnd, string title, string cls, bool isChild)> windows)
        {
            var editBoxes = windows.Where(w => w.isChild && IsEditControl(w.cls)).ToList();
            var buttons = windows.Where(w => w.isChild && IsButtonControl(w.cls)).ToList();

            if (editBoxes.Count == 0 && buttons.Count == 0)
            {
                // No standard controls — try typing into the first visible top-level window
                var topWin = windows.FirstOrDefault(w =>
                    !w.isChild && IsWindowVisible(w.hwnd) &&
                    w.cls != "MSCTFIME UI" && !w.cls.Contains("IME") &&
                    w.cls != "GDI+ Hook Window Class" && w.cls != "PerryShadowWnd");

                if (topWin.hwnd != IntPtr.Zero)
                {
                    Console.WriteLine($"[*] No standard controls — typing into {topWin.cls} window.");
                    SetForegroundWindow(topWin.hwnd);
                    Thread.Sleep(300);

                    TypeString(topWin.hwnd, "admin");
                    Thread.Sleep(100);
                    SendKey(topWin.hwnd, VK_TAB);
                    Thread.Sleep(100);
                    TypeString(topWin.hwnd, "admin");
                    Thread.Sleep(100);
                    SendKey(topWin.hwnd, VK_RETURN);

                    Console.WriteLine("  [+] Credentials typed via keyboard.");
                    return true;
                }
                Console.WriteLine("[*] No login controls found.");
                return false;
            }
            // Standard control path
            bool acted = false;

            if (editBoxes.Count >= 2)
            {
                SetEditText(editBoxes[0].hwnd, "admin");
                SetEditText(editBoxes[1].hwnd, "admin");
                Console.WriteLine("  [+] Filled username/password.");
                acted = true;
            }
            else if (editBoxes.Count == 1)
            {
                SetEditText(editBoxes[0].hwnd, "admin");
                Console.WriteLine("  [+] Filled field.");
                acted = true;
            }

            if (buttons.Count > 0)
            {
                var btn = buttons.First();
                Console.WriteLine($"  [*] Clicking: \"{btn.title}\"");
                SendMessage(btn.hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                acted = true;
            }

            return acted;
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


        /// <summary>
        /// Enumerates all windows belonging to a process, returning both
        /// top-level and child windows with their titles and class names.
        /// </summary>
        static List<(IntPtr hwnd, string title, string cls, bool isChild)> GetProcessWindows(int pid)
        {
            uint targetPid = (uint)pid;
            var windows = new List<(IntPtr hwnd, string title, string cls, bool isChild)>();

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint wndPid);
                if (wndPid != targetPid) return true;

                var titleBuf = new StringBuilder(512);
                GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
                var clsBuf = new StringBuilder(256);
                GetClassName(hWnd, clsBuf, clsBuf.Capacity);
                windows.Add((hWnd, titleBuf.ToString(), clsBuf.ToString(), false));

                EnumChildWindows(hWnd, (child, __) =>
                {
                    var ct = new StringBuilder(256);
                    var cc = new StringBuilder(256);
                    GetWindowText(child, ct, ct.Capacity);
                    GetClassName(child, cc, cc.Capacity);
                    windows.Add((child, ct.ToString(), cc.ToString(), true));
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            return windows;
        }

        static bool DismissErrorDialogs(int pid)
        {
            var windows = GetProcessWindows(pid);
            var dialogs = windows.Where(w =>
                            !w.isChild && (w.cls == "#32770" || w.cls.Contains("Dialog"))).ToList();
            if (dialogs.Count == 0) return false;
            foreach (var (hwnd, title, cls, _) in dialogs)
            {
                Console.WriteLine($"  [*] Dismissing: \"{title}\" (cls={cls})");

                var children = new List<(IntPtr h, string t, string c)>();
                EnumChildWindows(hwnd, (child, __) =>
                {
                    var ct = new StringBuilder(128);
                    var cc = new StringBuilder(128);
                    GetWindowText(child, ct, ct.Capacity);
                    GetClassName(child, cc, cc.Capacity);
                    children.Add((child, ct.ToString(), cc.ToString()));
                    return true;
                }, IntPtr.Zero);
                var okBtn = children.FirstOrDefault(c =>
                    IsButtonControl(c.c) && (
                        c.t.ToLower().Contains("ok") || c.t == "确定" ||
                        c.t.ToLower() == "yes" || c.t == "是"));
                if (okBtn.h != IntPtr.Zero)
                    SendMessage(okBtn.h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                else
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                Thread.Sleep(300);
            }
            return true;
        }

       
        

        /// <summary>
        /// After login is bypassed, the main window might be hidden.
        /// Find it and show it.
        /// </summary>
        static void RevealMainWindow(int pid)
        {
            var windows = GetProcessWindows(pid);

            var hiddenTopLevel = windows.Where(w =>
                !w.isChild && !IsWindowVisible(w.hwnd) &&
                !string.IsNullOrEmpty(w.cls) &&
                !w.cls.Contains("IME") && w.cls != "MSCTFIME UI" &&
                w.cls != "tooltips_class32").ToList();

            foreach (var (hwnd, title, cls, _) in hiddenTopLevel)
            {
                Console.WriteLine($"  [*] Revealing hidden window: cls={cls} title=\"{title}\"");
                ShowWindow(hwnd, SW_SHOW);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }

        static bool IsEditControl(string className)
        {
            string cl = className.ToLower();
            return cl.Contains("edit") || cl.Contains("tedit") ||
                   cl == "combobox" || cl.Contains("richedit") ||
                   cl.Contains("scintilla") || cl.Contains("textbox");
        }

        static bool IsButtonControl(string className)
        {
            string cl = className.ToLower();
            return cl.Contains("button") || cl.Contains("tbutton") ||
                   cl.Contains("tbitbtn") || cl.Contains("tspeedbutton");
        }

        static bool IsLoginButton(string titleLower)
        {
            return titleLower.Contains("login") || titleLower.Contains("ok") ||
                   titleLower.Contains("确定") || titleLower.Contains("登") ||
                   titleLower.Contains("enter") || titleLower.Contains("start") ||
                   titleLower.Contains("go") || titleLower.Contains("submit") ||
                   titleLower.Contains("验证") || titleLower.Contains("connect") ||
                   titleLower.Contains("进入") || titleLower.Contains("开始");
        }

        static void SetEditText(IntPtr hWnd, string text)
        {

            SendMessage(hWnd, 0x000C, IntPtr.Zero, text); // WM_SETTEXT
        }

        
    }
}