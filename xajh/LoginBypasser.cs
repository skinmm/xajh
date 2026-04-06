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
    /// Bypasses the login/auth gate on Cloud_xajhfuzhu.exe.
    ///
    /// The executable is packed with a custom ZDXXAJZB packer, so all
    /// meaningful code and strings are encrypted on disk and only appear
    /// in writable memory after the stub has finished unpacking.
    ///
    /// Strategy:
    ///   1. Attach to (or launch) Cloud_xajhfuzhu.exe.
    ///   2. Wait for the unpacker stub to finish (poll until the
    ///      unpacked .text section becomes populated).
    ///   3. Scan runtime memory for auth-check patterns:
    ///      a) AOB scan for conditional-jump sequences around login
    ///         validation (JE/JNE after TEST/CMP near message-box or
    ///         EnableWindow calls).
    ///      b) Window-based bypass: enumerate child windows of the
    ///         login dialog and simulate a successful login by posting
    ///         the right messages or toggling visibility.
    ///   4. Patch the branch so validation always succeeds.
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

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const uint WM_COMMAND = 0x0111;
        const uint BN_CLICKED = 0;
        const uint BM_CLICK = 0x00F5;
        const uint WM_CLOSE = 0x0010;
        const int SW_SHOW = 5;
        const int SW_HIDE = 0;
        const uint PAGE_EXECUTE_READWRITE = 0x40;

        // JE  (opcode 0x74 xx  or  0x0F 0x84 xx xx xx xx)
        // JNE (opcode 0x75 xx  or  0x0F 0x85 xx xx xx xx)
        static readonly byte JE_SHORT = 0x74;
        static readonly byte JNE_SHORT = 0x75;
        static readonly byte JMP_SHORT = 0xEB;  // unconditional short jump
        static readonly byte NOP = 0x90;

        /// <summary>
        /// Main entry point. Attaches to the running Cloud_xajhfuzhu process,
        /// waits for unpacking to finish, then applies login bypass patches.
        /// Returns the process handle and module base on success.
        /// </summary>
        public static (IntPtr hProcess, IntPtr moduleBase) Bypass()
        {
            Console.WriteLine("[*] Login Bypasser — Cloud_xajhfuzhu.exe");
            Console.WriteLine("[*] Waiting for Cloud_xajhfuzhu.exe ...");

            Process proc = WaitForProcess("xajhfuzhu", timeoutSeconds: 60);
            if (proc == null)
            {
                Console.WriteLine("[!] Cloud_xajhfuzhu.exe not found. Make sure it is running.");
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

            WaitForUnpack(hProcess, moduleBase);

            bool patched = false;

            patched |= PatchAuthBranches(hProcess, moduleBase);

            patched |= PatchAuthViaWindowEnum(proc.Id);

            if (!patched)
            {
                Console.WriteLine("[*] No specific auth pattern found — attempting generic NOP-sled on dialog validators.");
                patched |= GenericDialogPatch(hProcess, moduleBase);
            }

            if (patched)
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
        /// Window-based bypass: find the login dialog by enumerating all
        /// windows owned by the process, then click the OK/Login button
        /// or directly show the hidden main window.
        /// </summary>
        static bool PatchAuthViaWindowEnum(int pid)
        {
            Thread.Sleep(1500);

            uint targetPid = (uint)pid;
            var windows = new List<(IntPtr hwnd, string title, string cls)>();

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint wndPid);
                if (wndPid != targetPid) return true;

                var titleBuf = new StringBuilder(512);
                GetWindowText(hWnd, titleBuf, titleBuf.Capacity);
                var clsBuf = new StringBuilder(256);
                GetClassName(hWnd, clsBuf, clsBuf.Capacity);
                windows.Add((hWnd, titleBuf.ToString(), clsBuf.ToString()));

                EnumChildWindows(hWnd, (child, __) =>
                {
                    var ct = new StringBuilder(256);
                    var cc = new StringBuilder(256);
                    GetWindowText(child, ct, ct.Capacity);
                    GetClassName(child, cc, cc.Capacity);
                    windows.Add((child, ct.ToString(), cc.ToString()));
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            if (windows.Count == 0)
            {
                Console.WriteLine("[*] No windows found for process yet.");
                return false;
            }

            Console.WriteLine($"[*] Found {windows.Count} window(s) for Cloud_xajhfuzhu:");
            foreach (var (hwnd, title, cls) in windows)
            {
                bool visible = IsWindowVisible(hwnd);
                Console.WriteLine($"  HWND=0x{hwnd.ToInt64():X}  cls={cls,-24} title=\"{title}\"  vis={visible}");
            }

            bool acted = false;

            // Look for button children that might be a Login/OK button and click them
            var buttons = windows.Where(w =>
                w.cls.Contains("Button") || w.cls.Contains("BUTTON")).ToList();

            foreach (var (hwnd, title, cls) in buttons)
            {
                string tl = title.ToLower();
                bool isLoginBtn = tl.Contains("login") || tl.Contains("ok") || tl.Contains("确定")
                    || tl.Contains("登") || tl.Contains("enter") || tl.Contains("start")
                    || tl.Contains("go") || tl.Contains("submit");
                if (isLoginBtn || buttons.Count <= 3)
                {
                    Console.WriteLine($"  [*] Clicking button: \"{title}\" (0x{hwnd.ToInt64():X})");
                    SendMessage(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    acted = true;
                    Thread.Sleep(300);
                }
            }

            // If there are hidden top-level windows, show them (might be main form)
            var hidden = windows.Where(w =>
                !IsWindowVisible(w.hwnd) && string.IsNullOrEmpty(w.cls) == false
                && !w.cls.Contains("Button") && !w.cls.Contains("Edit") && !w.cls.Contains("Static"))
                .ToList();

            foreach (var (hwnd, title, cls) in hidden)
            {
                if (cls.Contains("Window") || cls.Contains("Form") || cls.Contains("Frame")
                    || cls.StartsWith("T") || cls.StartsWith("Afx"))
                {
                    Console.WriteLine($"  [*] Showing hidden window: cls={cls} (0x{hwnd.ToInt64():X})");
                    ShowWindow(hwnd, SW_SHOW);
                    acted = true;
                }
            }

            return acted;
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
