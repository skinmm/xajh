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
    /// Login bypass for Cloud_xajhfuzhu.exe (易语言, ZDXXAJZB packed).
    ///
    /// Login window (MAIN_WIN) layout:
    ///   Tabs: 用户登录 | 用户注册 | 用户充值 | 用户改密
    ///   Fields: 用户账号, 用户密码
    ///   Buttons: 绑定 | 试用 | 登录
    ///
    /// Strategy:
    ///   1. Click 试用 (Trial) to get past the login window.
    ///   2. Patch the trial timer / expiry check in memory so the
    ///      3-minute limit never triggers.
    ///   3. Also NOP the once-per-day restriction check.
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

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP   = 0x0202;
        const uint WM_CLOSE       = 0x0010;
        const uint BM_CLICK       = 0x00F5;
        const uint MK_LBUTTON     = 0x0001;
        const int  SW_RESTORE     = 9;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
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

            // Step 1: Click 试用 (Trial) to get past login
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

            // Step 2: Patch the trial timer so 3-minute limit never triggers
            Thread.Sleep(1000);
            bool timerPatched = PatchTrialTimer(hProcess, moduleBase);
            if (timerPatched)
                Console.WriteLine("[+] Trial timer patched — no time limit.");
            else
                Console.WriteLine("[*] Could not find timer to patch.");

            return (hProcess, moduleBase);
        }

        /// <summary>
        /// Patches the trial timer / expiry mechanism in the unpacked code.
        ///
        /// 易语言 trial timers typically use one of these patterns:
        ///
        /// 1. SetTimer API — creates a WM_TIMER that fires after N ms.
        ///    We find the CALL to SetTimer and change the interval to a
        ///    huge value, or NOP the call entirely.
        ///
        /// 2. GetTickCount / timeGetTime comparison — the timer callback
        ///    reads the current tick, compares with start tick + 180000 (3 min),
        ///    and calls ExitProcess or DestroyWindow if expired.
        ///    We patch the conditional jump so it never triggers.
        ///
        /// 3. KillTimer + ExitProcess in the timer callback.
        ///    We NOP the ExitProcess call.
        ///
        /// We search for all of these in the unpacked .text section.
        /// </summary>
        static bool PatchTrialTimer(IntPtr hProcess, IntPtr moduleBase)
        {
            IntPtr textBase = IntPtr.Add(moduleBase, 0x1000);
            int textSize = 0x1C8000;
            byte[] text = new byte[textSize];
            MemoryHelper.ReadProcessMemory(hProcess, textBase, text, textSize, out int textRead);

            if (textRead < 0x1000)
            {
                Console.WriteLine("[!] Cannot read .text section.");
                return false;
            }

            bool anyPatched = false;

            // --- Strategy A: Patch SetTimer interval ---
            // SetTimer(hWnd, nIDEvent, uElapse, lpTimerFunc)
            // The 3-min timer pushes 180000 (0x0002BF20) as uElapse.
            // We look for PUSH 0x0002BF20 (180000 ms = 3 minutes)
            // Also check 180 (seconds) = 0xB4, and other common trial durations.
            int[] trialMs = { 180000, 120000, 60000, 300000 }; // 3m, 2m, 1m, 5m
            foreach (int ms in trialMs)
            {
                byte[] msBytes = BitConverter.GetBytes(ms);
                anyPatched |= PatchTimerInterval(hProcess, textBase, text, textRead, msBytes, ms);
            }

            // --- Strategy B: Patch ExitProcess calls that follow timer checks ---
            // Find CALL to ExitProcess — the IAT entry for ExitProcess is known.
            // In EPL, ExitProcess is often called as: PUSH 0 / CALL [IAT_ExitProcess]
            // We NOP these calls so the app never exits.
            anyPatched |= PatchTimerExitCalls(hProcess, textBase, text, textRead, moduleBase);

            // --- Strategy C: Patch the timer callback's comparison ---
            // Look for CMP with 180000 (0x0002BF20) or 180 (0xB4)
            // and patch the conditional jump after it.
            anyPatched |= PatchTimerComparison(hProcess, textBase, text, textRead);

            return anyPatched;
        }

        /// <summary>
        /// Find PUSH <timerMs> near a CALL (likely SetTimer) and change
        /// the interval to 0x7FFFFFFF (~24 days).
        /// </summary>
        static bool PatchTimerInterval(IntPtr hProcess, IntPtr textBase,
            byte[] text, int textRead, byte[] msBytes, int msValue)
        {
            bool patched = false;

            for (int i = 0; i < textRead - 20; i++)
            {
                // PUSH imm32 matching the timer interval
                if (text[i] != 0x68) continue;
                if (text[i+1] != msBytes[0] || text[i+2] != msBytes[1] ||
                    text[i+3] != msBytes[2] || text[i+4] != msBytes[3]) continue;

                // Verify there's a CALL within ~20 bytes after (SetTimer call)
                bool hasCall = false;
                for (int j = i + 5; j < Math.Min(i + 25, textRead - 5); j++)
                {
                    if (text[j] == 0xE8 || text[j] == 0xFF) { hasCall = true; break; }
                }
                if (!hasCall) continue;

                IntPtr patchAddr = IntPtr.Add(textBase, i + 1);
                Console.WriteLine($"  [*] Found PUSH {msValue} (timer interval) at 0x{IntPtr.Add(textBase, i).ToInt64():X}");

                // Replace interval with 0x7FFFFFFF (never fires)
                byte[] newInterval = BitConverter.GetBytes(0x7FFFFFFF);

                VirtualProtectEx(hProcess, patchAddr, 4, PAGE_EXECUTE_READWRITE, out uint oldProt);
                if (MemoryHelper.WriteProcessMemory(hProcess, patchAddr, newInterval, 4, out int w) && w == 4)
                {
                    Console.WriteLine($"  [+] Timer interval changed to MAX at 0x{patchAddr.ToInt64():X}");
                    patched = true;
                }
                VirtualProtectEx(hProcess, patchAddr, 4, oldProt, out _);
            }

            return patched;
        }

        /// <summary>
        /// Find PUSH 0 / CALL [ExitProcess] sequences and NOP them.
        /// This prevents the timer callback from killing the process.
        /// </summary>
        static bool PatchTimerExitCalls(IntPtr hProcess, IntPtr textBase,
            byte[] text, int textRead, IntPtr moduleBase)
        {
            // ExitProcess is imported. Find the IAT slot by scanning for
            // FF 15 xx xx xx xx (CALL [addr]) where addr points to ExitProcess.
            // In a simpler approach: look for PUSH 0 / CALL pattern pairs
            // and verify the CALL target resolves to ExitProcess.
            //
            // Even simpler: search for the specific pattern of
            //   6A 00         PUSH 0
            //   E8 xx xx xx xx CALL <ExitProcess_wrapper>
            // and also
            //   6A 00         PUSH 0
            //   FF 15 xx xx xx xx CALL [IAT_ExitProcess]

            bool patched = false;

            for (int i = 0; i < textRead - 10; i++)
            {
                if (text[i] != 0x6A || text[i+1] != 0x00) continue; // PUSH 0

                // Check for FF 15 (CALL [imm32]) right after
                if (text[i+2] == 0xFF && text[i+3] == 0x15)
                {
                    // Read the IAT address
                    int iatAddr = BitConverter.ToInt32(text, i+4);
                    // Verify it's in the IAT range (module base + import section)
                    long iatRva = iatAddr - moduleBase.ToInt64();
                    // ExitProcess IAT is typically in .rdata or the import area
                    if (iatRva > 0 && iatRva < 0x300000)
                    {
                        // Read what the IAT slot points to — should be ExitProcess
                        // We can't easily verify the target, but PUSH 0 + CALL [IAT]
                        // is almost always ExitProcess(0).
                        IntPtr nopAddr = IntPtr.Add(textBase, i);
                        Console.WriteLine($"  [*] Found PUSH 0 / CALL [0x{iatAddr:X}] at 0x{nopAddr.ToInt64():X}");

                        // NOP the entire sequence (2 + 6 = 8 bytes)
                        byte[] nops = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };

                        VirtualProtectEx(hProcess, nopAddr, 8, PAGE_EXECUTE_READWRITE, out uint oldProt);
                        if (MemoryHelper.WriteProcessMemory(hProcess, nopAddr, nops, 8, out int w) && w == 8)
                        {
                            Console.WriteLine($"  [+] NOPed ExitProcess call at 0x{nopAddr.ToInt64():X}");
                            patched = true;
                        }
                        VirtualProtectEx(hProcess, nopAddr, 8, oldProt, out _);
                    }
                }
            }

            return patched;
        }

        /// <summary>
        /// Find CMP with 180000 or 180 (timer duration comparison)
        /// and patch the conditional jump after it.
        /// </summary>
        static bool PatchTimerComparison(IntPtr hProcess, IntPtr textBase,
            byte[] text, int textRead)
        {
            bool patched = false;

            // CMP reg, 0x0002BF20 (180000 ms)
            // Encoding: 81 F8 20 BF 02 00 (CMP EAX, 180000)
            //       or: 3D 20 BF 02 00    (CMP EAX, 180000 — short form)
            byte[] cmp180k = { 0x20, 0xBF, 0x02, 0x00 };

            for (int i = 0; i < textRead - 12; i++)
            {
                bool isCmp = false;
                int jccOffset = -1;

                // 3D xx xx xx xx (CMP EAX, imm32)
                if (text[i] == 0x3D &&
                    text[i+1] == cmp180k[0] && text[i+2] == cmp180k[1] &&
                    text[i+3] == cmp180k[2] && text[i+4] == cmp180k[3])
                {
                    isCmp = true;
                    jccOffset = i + 5;
                }

                // 81 F8 xx xx xx xx (CMP EAX, imm32) — alternate encoding
                if (text[i] == 0x81 && text[i+1] == 0xF8 &&
                    text[i+2] == cmp180k[0] && text[i+3] == cmp180k[1] &&
                    text[i+4] == cmp180k[2] && text[i+5] == cmp180k[3])
                {
                    isCmp = true;
                    jccOffset = i + 6;
                }

                // 81 F9..FF (CMP ECX/EDX/etc, imm32)
                if (text[i] == 0x81 && text[i+1] >= 0xF8 && text[i+1] <= 0xFF &&
                    text[i+2] == cmp180k[0] && text[i+3] == cmp180k[1] &&
                    text[i+4] == cmp180k[2] && text[i+5] == cmp180k[3])
                {
                    isCmp = true;
                    jccOffset = i + 6;
                }

                if (!isCmp || jccOffset < 0 || jccOffset >= textRead - 2) continue;

                byte jcc = text[jccOffset];
                // JA (0x77), JAE (0x73), JB (0x72), JBE (0x76),
                // JG (0x7F), JGE (0x7D), JL (0x7C), JLE (0x7E),
                // JE (0x74), JNE (0x75)
                bool isJcc = (jcc >= 0x70 && jcc <= 0x7F);

                if (!isJcc) continue;

                IntPtr patchAddr = IntPtr.Add(textBase, jccOffset);
                Console.WriteLine($"  [*] Found CMP with 180000 + Jcc at 0x{patchAddr.ToInt64():X}");

                // NOP the conditional jump (2 bytes: opcode + offset)
                byte[] nops = { 0x90, 0x90 };

                VirtualProtectEx(hProcess, patchAddr, 2, PAGE_EXECUTE_READWRITE, out uint oldProt);
                if (MemoryHelper.WriteProcessMemory(hProcess, patchAddr, nops, 2, out int w) && w == 2)
                {
                    Console.WriteLine($"  [+] NOPed timer comparison jump at 0x{patchAddr.ToInt64():X}");
                    patched = true;
                }
                VirtualProtectEx(hProcess, patchAddr, 2, oldProt, out _);
            }

            return patched;
        }

        // --- UI interaction methods ---

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

            ShowWindow(mainWin, SW_RESTORE);
            SetForegroundWindow(mainWin);
            Thread.Sleep(500);

            // 试用 button: center button at bottom row (~50% x, ~88% y)
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
