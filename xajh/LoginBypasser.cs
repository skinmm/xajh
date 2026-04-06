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
    /// Fully automatic login bypass for Cloud_xajhfuzhu.exe.
    ///
    /// The executable is packed with a custom ZDXXAJZB packer, so all
    /// meaningful code and strings are encrypted on disk and only appear
    /// in writable memory after the stub has finished unpacking.
    ///
    /// You do NOT need to enter any username or password manually.
    /// The bypasser:
    ///   1. Attaches to Cloud_xajhfuzhu.exe and waits for the packer
    ///      to decompress the real code.
    ///   2. Patches the authentication check in memory so that ANY
    ///      credentials (including empty) pass validation.
    ///   3. Automatically fills the username and password fields with
    ///      dummy values and clicks the Login/OK button for you.
    ///   4. Detects and dismisses any "wrong password" error dialogs.
    ///   5. Shows the main application window if it was hidden behind
    ///      the login gate.
    /// </summary>
    public static class LoginBypasser
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowA(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

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
        static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const uint WM_SETTEXT  = 0x000C;
        const uint WM_GETTEXT  = 0x000D;
        const uint WM_COMMAND  = 0x0111;
        const uint BN_CLICKED  = 0;
        const uint BM_CLICK    = 0x00F5;
        const uint WM_CLOSE    = 0x0010;
        const uint WM_KEYDOWN  = 0x0100;
        const uint WM_KEYUP    = 0x0101;
        const uint WM_CHAR     = 0x0102;
        const uint EM_SETSEL   = 0x00B1;
        const uint EM_REPLACESEL = 0x00C2;
        const int  VK_RETURN   = 0x0D;
        const int  VK_TAB      = 0x09;
        const int  SW_SHOW     = 5;
        const int  SW_HIDE     = 0;
        const int  SW_RESTORE  = 9;
        const uint PAGE_EXECUTE_READWRITE = 0x40;

        // x86 opcodes for branch patching
        static readonly byte JE_SHORT  = 0x74;
        static readonly byte JNE_SHORT = 0x75;
        static readonly byte JMP_SHORT = 0xEB;
        static readonly byte NOP       = 0x90;

        /// <summary>
        /// Main entry point. Fully automatic — no manual input needed.
        /// Attaches to Cloud_xajhfuzhu.exe, patches auth checks, fills
        /// the login form, and clicks through. Returns the process handle
        /// and module base for downstream use.
        /// </summary>
        public static (IntPtr hProcess, IntPtr moduleBase) Bypass()
        {
            Console.WriteLine("[*] Login Bypasser — Cloud_xajhfuzhu.exe");
            Console.WriteLine("[*] No manual login required — credentials will be filled automatically.");
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

            // Step 1: Wait for the ZDXXAJZB packer to finish unpacking
            WaitForUnpack(hProcess, moduleBase);

            // Step 2: Patch auth-check branches in the unpacked code
            bool memPatched = false;
            memPatched |= PatchAuthBranches(hProcess, moduleBase);
            if (!memPatched)
                memPatched |= GenericDialogPatch(hProcess, moduleBase);

            // Step 3: Wait for the login window to appear, then fill + click
            bool uiPatched = FillAndSubmitLoginForm(proc.Id);

            // Step 4: Handle any error popups and retry
            if (uiPatched)
            {
                Thread.Sleep(1000);
                DismissErrorDialogs(proc.Id);
                RevealMainWindow(proc.Id);
            }

            if (memPatched || uiPatched)
                Console.WriteLine("[+] Login bypass applied successfully.");
            else
                Console.WriteLine("[*] No login gate detected (may already be bypassed or not present).");

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

        /// <summary>
        /// The ZDXXAJZB packer writes the real code into the .text section
        /// (VA 0x401000). We poll until the first 0x100 bytes of that region
        /// are no longer all-zero, which signals unpacking is done.
        /// </summary>
        static void WaitForUnpack(IntPtr hProcess, IntPtr moduleBase)
        {
            Console.Write("[*] Waiting for unpacker to finish");
            IntPtr textStart = IntPtr.Add(moduleBase, 0x1000);
            byte[] probe = new byte[0x100];

            for (int i = 0; i < 120; i++) // up to ~30 s
            {
                MemoryHelper.ReadProcessMemory(hProcess, textStart, probe, probe.Length, out int read);
                if (read == probe.Length && !AllZero(probe))
                {
                    Console.WriteLine(" done.");
                    Thread.Sleep(800); // extra settle time
                    return;
                }
                Console.Write(".");
                Thread.Sleep(250);
            }
            Console.WriteLine(" (timeout — continuing anyway)");
        }

        static bool AllZero(byte[] buf)
        {
            for (int i = 0; i < buf.Length; i++)
                if (buf[i] != 0) return false;
            return true;
        }

        /// <summary>
        /// Scans unpacked .text for common auth-check instruction sequences
        /// and patches conditional jumps so the "success" path is always taken.
        ///
        /// Targeted patterns (x86):
        ///
        /// Pattern A — CMP/TEST + JE/JNE near a CALL (login validator returns
        ///   0/1 in EAX, then the caller branches):
        ///     85 C0        test eax,eax
        ///     74 xx        je   <fail>       → patch to  EB xx  (jmp always)
        ///   or
        ///     85 C0        test eax,eax
        ///     75 xx        jne  <success>    → patch to  EB xx  (jmp always)
        ///
        /// Pattern B — CMP EAX, imm + JE/JNE:
        ///     3D 01 00 00 00   cmp eax,1
        ///     75 xx            jne  <fail>   → NOP + JMP
        ///
        /// Pattern C — Near-jump variants (0F 84 / 0F 85):
        ///     85 C0              test eax,eax
        ///     0F 84 xx xx xx xx  je   <fail> → 0F 85 (invert) or NOP
        /// </summary>
        static bool PatchAuthBranches(IntPtr hProcess, IntPtr moduleBase)
        {
            bool anyPatched = false;

            // Pattern A: TEST EAX,EAX / JE short  →  TEST EAX,EAX / JMP short
            string patternA_JE = "85 C0 74 ??";
            anyPatched |= ScanAndPatchBranch(hProcess, patternA_JE, 2, PatchKind.ShortJeToJmp,
                "TEST EAX,EAX + JE → JMP");

            // Pattern A2: TEST EAX,EAX / JNE short  →  JMP always (keep going)
            string patternA_JNE = "85 C0 75 ??";
            anyPatched |= ScanAndPatchBranch(hProcess, patternA_JNE, 2, PatchKind.ShortJneToJmp,
                "TEST EAX,EAX + JNE → JMP");

            // Pattern B: CMP EAX,1 / JNE short → NOP JMP
            string patternB = "3D 01 00 00 00 75 ??";
            anyPatched |= ScanAndPatchBranch(hProcess, patternB, 5, PatchKind.ShortJneToJmp,
                "CMP EAX,1 + JNE → JMP");

            // Pattern C: CMP EAX,1 / JE short
            string patternC = "3D 01 00 00 00 74 ??";
            anyPatched |= ScanAndPatchBranch(hProcess, patternC, 5, PatchKind.ShortJeToJmp,
                "CMP EAX,1 + JE → JMP");

            // Pattern D: TEST EAX,EAX / JE near (0F 84)
            string patternD = "85 C0 0F 84 ?? ?? ?? ??";
            anyPatched |= ScanAndPatchBranch(hProcess, patternD, 2, PatchKind.NearJeToNop,
                "TEST EAX,EAX + JE near → NOP");

            // Pattern E: TEST EAX,EAX / JNE near (0F 85)
            string patternE = "85 C0 0F 85 ?? ?? ?? ??";
            anyPatched |= ScanAndPatchBranch(hProcess, patternE, 2, PatchKind.NearJneToNop,
                "TEST EAX,EAX + JNE near → NOP");

            return anyPatched;
        }

        enum PatchKind { ShortJeToJmp, ShortJneToJmp, NearJeToNop, NearJneToNop }

        static bool ScanAndPatchBranch(IntPtr hProcess, string pattern, int branchOffset,
            PatchKind kind, string label)
        {
            var hits = MemoryHelper.AobScan(hProcess, pattern);

            // Filter to code section range (image base + 0x1000 .. + 0x200000 typical)
            var codeHits = hits.Where(a =>
            {
                long rva = a.ToInt64();
                return rva > 0x400000 && rva < 0x1800000;
            }).ToList();

            if (codeHits.Count == 0) return false;

            Console.WriteLine($"[*] {label}: {codeHits.Count} candidate(s)");

            bool patched = false;
            foreach (var addr in codeHits)
            {
                IntPtr patchAddr = IntPtr.Add(addr, branchOffset);
                byte[] origBytes = new byte[6];
                MemoryHelper.ReadProcessMemory(hProcess, patchAddr, origBytes, origBytes.Length, out _);

                byte[] patch;
                switch (kind)
                {
                    case PatchKind.ShortJeToJmp:
                        if (origBytes[0] != JE_SHORT) continue;
                        patch = new byte[] { JMP_SHORT, origBytes[1] };
                        break;
                    case PatchKind.ShortJneToJmp:
                        if (origBytes[0] != JNE_SHORT) continue;
                        patch = new byte[] { JMP_SHORT, origBytes[1] };
                        break;
                    case PatchKind.NearJeToNop:
                        if (origBytes[0] != 0x0F || origBytes[1] != 0x84) continue;
                        patch = new byte[] { NOP, NOP, NOP, NOP, NOP, NOP };
                        break;
                    case PatchKind.NearJneToNop:
                        if (origBytes[0] != 0x0F || origBytes[1] != 0x85) continue;
                        patch = new byte[] { 0x0F, 0x84, origBytes[2], origBytes[3], origBytes[4], origBytes[5] };
                        break;
                    default:
                        continue;
                }

                VirtualProtectEx(hProcess, patchAddr, (uint)patch.Length,
                    PAGE_EXECUTE_READWRITE, out uint oldProtect);

                if (MemoryHelper.WriteProcessMemory(hProcess, patchAddr, patch, patch.Length, out int written)
                    && written == patch.Length)
                {
                    Console.WriteLine($"  [+] Patched @ 0x{patchAddr.ToInt64():X}");
                    patched = true;
                }

                VirtualProtectEx(hProcess, patchAddr, (uint)patch.Length,
                    oldProtect, out _);
            }
            return patched;
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

        /// <summary>
        /// Waits for the login window to appear, auto-fills the username
        /// and password fields with dummy values, then clicks the Login button.
        /// You do NOT need to type anything yourself.
        /// </summary>
        static bool FillAndSubmitLoginForm(int pid)
        {
            Console.WriteLine("[*] Waiting for login window to appear ...");

            List<(IntPtr hwnd, string title, string cls, bool isChild)> windows = null;

            // Wait until we see edit boxes or buttons — the login form is ready
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(500);
                windows = GetProcessWindows(pid);
                bool hasEdits = windows.Any(w => w.isChild && IsEditControl(w.cls));
                bool hasButtons = windows.Any(w => w.isChild && IsButtonControl(w.cls));
                if (hasEdits || hasButtons) break;
            }

            if (windows == null || windows.Count == 0)
            {
                Console.WriteLine("[*] No windows found for process.");
                return false;
            }

            Console.WriteLine($"[*] Found {windows.Count} window element(s):");
            foreach (var (hwnd, title, cls, isChild) in windows)
            {
                bool vis = IsWindowVisible(hwnd);
                string prefix = isChild ? "    " : "  ";
                Console.WriteLine($"{prefix}HWND=0x{hwnd.ToInt64():X}  cls={cls,-24} title=\"{title}\"  vis={vis}");
            }

            // Find text input fields (Edit controls)
            var editBoxes = windows.Where(w =>
                w.isChild && IsEditControl(w.cls)).ToList();

            // Find buttons
            var buttons = windows.Where(w =>
                w.isChild && IsButtonControl(w.cls)).ToList();

            bool acted = false;

            if (editBoxes.Count >= 2)
            {
                // Two or more edit boxes: first is username, second is password
                Console.WriteLine($"[*] Found {editBoxes.Count} input field(s) — auto-filling credentials.");

                SetEditText(editBoxes[0].hwnd, "admin");
                Console.WriteLine("  [+] Username field filled: \"admin\"");

                SetEditText(editBoxes[1].hwnd, "admin");
                Console.WriteLine("  [+] Password field filled: \"admin\"");

                acted = true;
            }
            else if (editBoxes.Count == 1)
            {
                // Single edit box: might be key/serial/cardkey input
                Console.WriteLine("[*] Found 1 input field — filling with bypass key.");
                SetEditText(editBoxes[0].hwnd, "admin");
                Console.WriteLine("  [+] Field filled: \"admin\"");
                acted = true;
            }

            // Brief pause to let the UI update before clicking
            if (acted)
                Thread.Sleep(300);

            // Enable any disabled buttons first
            foreach (var (hwnd, title, cls, _) in buttons)
            {
                if (!IsWindowEnabled(hwnd))
                {
                    EnableWindow(hwnd, true);
                    Console.WriteLine($"  [*] Enabled disabled button: \"{title}\"");
                }
            }

            // Click the login/OK/submit button
            bool clicked = false;
            foreach (var (hwnd, title, cls, _) in buttons)
            {
                string tl = title.ToLower();
                if (IsLoginButton(tl))
                {
                    Console.WriteLine($"  [*] Clicking login button: \"{title}\"");
                    SendMessage(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    clicked = true;
                    acted = true;
                    Thread.Sleep(500);
                    break;
                }
            }

            // If no button text matched, click the first visible button
            if (!clicked && buttons.Count > 0)
            {
                var btn = buttons.First();
                Console.WriteLine($"  [*] Clicking button: \"{btn.title}\" (first available)");
                SendMessage(btn.hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                acted = true;
                Thread.Sleep(500);
            }

            // If no buttons found, try pressing Enter on the last edit box
            if (!clicked && editBoxes.Count > 0)
            {
                var lastEdit = editBoxes.Last();
                Console.WriteLine("  [*] Pressing Enter on input field to submit.");
                PostMessage(lastEdit.hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                Thread.Sleep(50);
                PostMessage(lastEdit.hwnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                acted = true;
            }

            return acted;
        }

        /// <summary>
        /// After the login attempt, dismiss any error/warning message boxes
        /// that might pop up (e.g. "wrong password", "auth failed").
        /// </summary>
        static void DismissErrorDialogs(int pid)
        {
            Thread.Sleep(500);
            var windows = GetProcessWindows(pid);

            // Look for MessageBox-style dialogs (#32770 is the dialog class)
            var dialogs = windows.Where(w =>
                !w.isChild && (w.cls == "#32770" || w.cls.Contains("Dialog"))).ToList();

            foreach (var (hwnd, title, cls, _) in dialogs)
            {
                Console.WriteLine($"  [*] Dismissing dialog: \"{title}\" (cls={cls})");
                // Click OK button inside the dialog
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
            EnableWindow(hWnd, true);
            SendMessage(hWnd, WM_SETTEXT, IntPtr.Zero, text);
        }

        /// <summary>
        /// Generic fallback: scan for common dialog-check patterns in the
        /// unpacked .text section and NOP them out. Targets sequences where
        /// a CALL is followed by TEST EAX,EAX and a conditional jump —
        /// typical of  if (!Authenticate()) { exit; }  patterns.
        /// </summary>
        static bool GenericDialogPatch(IntPtr hProcess, IntPtr moduleBase)
        {
            // CALL rel32 / TEST EAX,EAX / JE short
            string pat = "E8 ?? ?? ?? ?? 85 C0 74 ??";
            var hits = MemoryHelper.AobScan(hProcess, pat);

            var codeHits = hits.Where(a =>
            {
                long addr = a.ToInt64();
                return addr > 0x400000 && addr < 0x1800000;
            }).ToList();

            if (codeHits.Count == 0) return false;

            Console.WriteLine($"[*] Generic CALL+TEST+JE: {codeHits.Count} site(s)");

            int patchCount = 0;
            foreach (var addr in codeHits.Take(32))
            {
                IntPtr jeAddr = IntPtr.Add(addr, 7); // offset of JE within the pattern
                byte[] orig = new byte[2];
                MemoryHelper.ReadProcessMemory(hProcess, jeAddr, orig, 2, out _);
                if (orig[0] != JE_SHORT) continue;

                byte[] patch = { JMP_SHORT, orig[1] };

                VirtualProtectEx(hProcess, jeAddr, 2, PAGE_EXECUTE_READWRITE, out uint oldProt);
                if (MemoryHelper.WriteProcessMemory(hProcess, jeAddr, patch, 2, out int w) && w == 2)
                    patchCount++;
                VirtualProtectEx(hProcess, jeAddr, 2, oldProt, out _);
            }

            if (patchCount > 0)
                Console.WriteLine($"  [+] Patched {patchCount} generic auth-check site(s).");
            return patchCount > 0;
        }
    }
}
