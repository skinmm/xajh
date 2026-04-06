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
    /// Automatic login handler for Cloud_xajhfuzhu.exe.
    ///
    /// Uses only UI automation (no memory patching):
    ///   1. Waits for the login window to appear.
    ///   2. Auto-fills username and password fields.
    ///   3. Clicks the Login button.
    ///   4. Dismisses error dialogs and retries if needed.
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
        static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const uint WM_SETTEXT = 0x000C;
        const uint BM_CLICK   = 0x00F5;
        const uint WM_CLOSE   = 0x0010;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP   = 0x0101;
        const int  VK_RETURN  = 0x0D;
        const int  SW_SHOW    = 5;
        const int  SW_RESTORE = 9;

        const int MAX_LOGIN_ATTEMPTS = 3;

        /// <summary>
        /// Main entry point. Waits for Cloud_xajhfuzhu.exe, fills the
        /// login form, and clicks through. No memory patching — safe.
        /// Returns the process handle and module base.
        /// </summary>
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

                bool filled = FillAndSubmitLoginForm(proc.Id);
                if (!filled)
                {
                    Console.WriteLine("[*] No login form detected — may already be past login.");
                    break;
                }

                Thread.Sleep(1500);

                // Check if an error dialog appeared
                if (proc.HasExited)
                {
                    Console.WriteLine("[!] Cloud_xajhfuzhu.exe has exited after login attempt.");
                    return (IntPtr.Zero, IntPtr.Zero);
                }

                bool hadError = DismissErrorDialogs(proc.Id);
                if (!hadError)
                {
                    Console.WriteLine("[+] Login submitted — no error dialog detected.");
                    break;
                }

                Console.WriteLine("[*] Error dialog dismissed, retrying ...");
                Thread.Sleep(1000);
            }

            // Show any hidden main windows
            RevealMainWindow(proc.Id);

            Console.WriteLine("[+] Login bypass complete.");
            return (hProcess, moduleBase);
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

        /// <summary>
        /// Waits for the login window, fills username/password, clicks Login.
        /// </summary>
        static bool FillAndSubmitLoginForm(int pid)
        {
            Console.WriteLine("[*] Waiting for login window ...");

            List<(IntPtr hwnd, string title, string cls, bool isChild)> windows = null;

            for (int wait = 0; wait < 40; wait++)
            {
                Thread.Sleep(500);
                windows = GetProcessWindows(pid);
                bool hasEdits = windows.Any(w => w.isChild && IsEditControl(w.cls));
                bool hasButtons = windows.Any(w => w.isChild && IsButtonControl(w.cls));
                if (hasEdits || hasButtons) break;
            }

            if (windows == null || windows.Count == 0)
            {
                Console.WriteLine("[*] No windows found.");
                return false;
            }

            Console.WriteLine($"[*] Found {windows.Count} window element(s):");
            foreach (var (hwnd, title, cls, isChild) in windows)
            {
                bool vis = IsWindowVisible(hwnd);
                string prefix = isChild ? "    " : "  ";
                Console.WriteLine($"{prefix}HWND=0x{hwnd.ToInt64():X}  cls={cls,-24} title=\"{title}\"  vis={vis}");
            }

            var editBoxes = windows.Where(w => w.isChild && IsEditControl(w.cls)).ToList();
            var buttons = windows.Where(w => w.isChild && IsButtonControl(w.cls)).ToList();

            bool acted = false;

            if (editBoxes.Count >= 2)
            {
                Console.WriteLine($"[*] Found {editBoxes.Count} input field(s) — auto-filling.");
                SetEditText(editBoxes[0].hwnd, "admin");
                Console.WriteLine("  [+] Username filled: \"admin\"");
                SetEditText(editBoxes[1].hwnd, "admin");
                Console.WriteLine("  [+] Password filled: \"admin\"");
                acted = true;
            }
            else if (editBoxes.Count == 1)
            {
                Console.WriteLine("[*] Found 1 input field — filling.");
                SetEditText(editBoxes[0].hwnd, "admin");
                Console.WriteLine("  [+] Field filled: \"admin\"");
                acted = true;
            }

            if (acted)
                Thread.Sleep(300);

            // Enable disabled buttons
            foreach (var (hwnd, title, cls, _) in buttons)
            {
                if (!IsWindowEnabled(hwnd))
                {
                    EnableWindow(hwnd, true);
                    Console.WriteLine($"  [*] Enabled button: \"{title}\"");
                }
            }

            // Click login button
            bool clicked = false;
            foreach (var (hwnd, title, cls, _) in buttons)
            {
                if (IsLoginButton(title.ToLower()))
                {
                    Console.WriteLine($"  [*] Clicking: \"{title}\"");
                    SendMessage(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    clicked = true;
                    acted = true;
                    Thread.Sleep(500);
                    break;
                }
            }

            if (!clicked && buttons.Count > 0)
            {
                var btn = buttons.First();
                Console.WriteLine($"  [*] Clicking: \"{btn.title}\" (first button)");
                SendMessage(btn.hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                acted = true;
                Thread.Sleep(500);
            }

            if (!clicked && editBoxes.Count > 0)
            {
                Console.WriteLine("  [*] Pressing Enter to submit.");
                var lastEdit = editBoxes.Last();
                PostMessage(lastEdit.hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                Thread.Sleep(50);
                PostMessage(lastEdit.hwnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                acted = true;
            }

            return acted;
        }

        /// <summary>
        /// Dismiss error/warning message boxes. Returns true if any were found.
        /// </summary>
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
                Console.WriteLine($"  [*] Revealing: cls={cls} title=\"{title}\"");
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
            EnableWindow(hWnd, true);
            SendMessage(hWnd, WM_SETTEXT, IntPtr.Zero, text);
        }
    }
}
