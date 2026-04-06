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
    /// Runtime login bypass for Cloud_xajhfuzhu.exe (易语言 program).
    ///
    /// The exe is packed with ZDXXAJZB — all code and strings are encrypted
    /// on disk and decrypted at runtime. Cannot patch on disk.
    ///
    /// After the unpacker finishes:
    ///   1. Search runtime memory for the login window title string.
    ///   2. Find the code that references it (the login form setup).
    ///   3. Find the login validation CALL near it.
    ///   4. Patch only that specific validation to always succeed.
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
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const uint WM_CLOSE = 0x0010;
        const uint BM_CLICK = 0x00F5;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        const int SW_RESTORE = 9;

        public static (IntPtr hProcess, IntPtr moduleBase) Bypass()
        {
            Console.WriteLine("[*] Login Bypasser — Cloud_xajhfuzhu.exe (易语言)");
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

            // Wait for unpacker to finish by checking for MAIN_WIN string
            Console.Write("[*] Waiting for unpacker");
            bool unpacked = WaitForString(hProcess, moduleBase, "MAIN_WIN", 30);
            if (!unpacked)
            {
                Console.WriteLine(" timeout.");
                Console.WriteLine("[!] Could not confirm unpacking. Trying anyway...");
            }
            else
            {
                Console.WriteLine(" done.");
            }
            Thread.Sleep(1000);

            // Search for login-related strings and patch the validation
            bool patched = PatchLoginValidation(hProcess, moduleBase);

            if (patched)
            {
                Console.WriteLine("[+] Login validation patched.");
                Thread.Sleep(1000);
                DismissErrorDialogs(proc.Id);
            }
            else
            {
                Console.WriteLine("[*] Could not find login validation to patch.");
                Console.WriteLine("[*] The program should still work — just login manually.");
            }

            return (hProcess, moduleBase);
        }

        /// <summary>
        /// Waits until a specific ASCII string appears in the process's
        /// memory (signals that the unpacker has finished decrypting).
        /// </summary>
        static bool WaitForString(IntPtr hProcess, IntPtr moduleBase, string target, int timeoutSec)
        {
            byte[] needle = Encoding.ASCII.GetBytes(target);
            IntPtr textStart = IntPtr.Add(moduleBase, 0x1000);

            for (int i = 0; i < timeoutSec * 4; i++)
            {
                // Check a small region of .text to see if it's populated
                byte[] probe = new byte[0x1000];
                MemoryHelper.ReadProcessMemory(hProcess, textStart, probe, probe.Length, out int read);
                if (read > 0 && !AllZero(probe))
                {
                    Console.Write(".");
                    // Now scan .rdata and .data for the target string
                    // .rdata at moduleBase + 0x1C9000, .data at + 0x227000
                    foreach (int sectionOffset in new[] { 0x1C9000, 0x227000, 0x1000 })
                    {
                        IntPtr scanBase = IntPtr.Add(moduleBase, sectionOffset);
                        int scanSize = 0x60000;
                        byte[] buf = new byte[scanSize];
                        MemoryHelper.ReadProcessMemory(hProcess, scanBase, buf, scanSize, out int r);
                        if (r > needle.Length)
                        {
                            int pos = FindBytes(buf, needle, r);
                            if (pos >= 0) return true;
                        }
                    }
                }
                Console.Write(".");
                Thread.Sleep(250);
            }
            return false;
        }

        /// <summary>
        /// Searches runtime memory for login validation code and patches it.
        ///
        /// Strategy: Find the CALL that validates login credentials.
        /// In 易语言 programs, the login button click handler typically:
        ///   1. Reads text from username/password controls
        ///   2. CALLs a validation function (network or local)
        ///   3. Tests the return value (TEST EAX,EAX or CMP EAX,x)
        ///   4. Branches based on result
        ///
        /// We find this by searching for known strings near the login code
        /// (window title, error messages) and patching the branch that
        /// follows the validation call.
        ///
        /// We search for cross-references: code that loads the address
        /// of the login-related string, then find the conditional branch
        /// within ~200 bytes after that reference.
        /// </summary>
        static bool PatchLoginValidation(IntPtr hProcess, IntPtr moduleBase)
        {
            // Step 1: Find login-related strings in .rdata/.data
            var stringAddresses = FindLoginStrings(hProcess, moduleBase);

            if (stringAddresses.Count == 0)
            {
                Console.WriteLine("[*] No login strings found in memory.");
                return false;
            }

            Console.WriteLine($"[*] Found {stringAddresses.Count} login-related string(s).");

            // Step 2: Search .text for code that references these strings
            // (PUSH string_addr or MOV reg, string_addr patterns)
            IntPtr textBase = IntPtr.Add(moduleBase, 0x1000);
            int textSize = 0x1C8000; // .text section virtual size
            byte[] textBuf = new byte[textSize];
            MemoryHelper.ReadProcessMemory(hProcess, textBase, textBuf, textSize, out int textRead);

            if (textRead < 0x1000)
            {
                Console.WriteLine("[!] Cannot read .text section.");
                return false;
            }

            bool anyPatched = false;

            foreach (var (strAddr, strText) in stringAddresses)
            {
                byte[] addrBytes = BitConverter.GetBytes((int)strAddr.ToInt64());

                // Find PUSH <strAddr> (opcode 68 xx xx xx xx)
                for (int i = 0; i < textRead - 200; i++)
                {
                    if (textBuf[i] != 0x68) continue; // PUSH imm32
                    if (textBuf[i + 1] != addrBytes[0] || textBuf[i + 2] != addrBytes[1] ||
                        textBuf[i + 3] != addrBytes[2] || textBuf[i + 4] != addrBytes[3])
                        continue;

                    IntPtr pushAddr = IntPtr.Add(textBase, i);
                    Console.WriteLine($"  [*] Found PUSH ref to \"{strText}\" at 0x{pushAddr.ToInt64():X}");

                    // Look within ~256 bytes BEFORE the PUSH for a CALL + TEST + Jcc
                    // This is where the validation return value is checked
                    int scanStart = Math.Max(0, i - 256);
                    int scanEnd = Math.Min(textRead - 6, i + 256);

                    for (int j = scanStart; j < scanEnd; j++)
                    {
                        // Pattern: CALL rel32 + TEST EAX,EAX + JE/JNE
                        if (textBuf[j] == 0xE8 && // CALL rel32
                            j + 7 < scanEnd &&
                            textBuf[j + 5] == 0x85 && textBuf[j + 6] == 0xC0) // TEST EAX,EAX
                        {
                            if (j + 8 < scanEnd)
                            {
                                byte jccOpcode = textBuf[j + 7];
                                if (jccOpcode == 0x74 || jccOpcode == 0x75) // JE or JNE
                                {
                                    IntPtr patchAddr = IntPtr.Add(textBase, j + 7);

                                    VirtualProtectEx(hProcess, patchAddr, 2,
                                        PAGE_EXECUTE_READWRITE, out uint oldProt);

                                    // JE -> JMP (always skip to fail handler = skip login check)
                                    // JNE -> JMP (always take success path)
                                    byte[] patch = { 0xEB, textBuf[j + 8] }; // JMP short

                                    if (MemoryHelper.WriteProcessMemory(hProcess, patchAddr,
                                        patch, 2, out int written) && written == 2)
                                    {
                                        Console.WriteLine($"  [+] Patched {(jccOpcode == 0x74 ? "JE" : "JNE")} -> JMP at 0x{patchAddr.ToInt64():X}");
                                        anyPatched = true;
                                    }

                                    VirtualProtectEx(hProcess, patchAddr, 2, oldProt, out _);
                                }
                            }
                        }

                        // Pattern: CALL rel32 + CMP EAX,1 + JNE
                        if (textBuf[j] == 0xE8 && // CALL rel32
                            j + 12 < scanEnd &&
                            textBuf[j + 5] == 0x83 && textBuf[j + 6] == 0xF8 && // CMP EAX, imm8
                            textBuf[j + 7] == 0x01) // CMP EAX, 1
                        {
                            byte jccOpcode = textBuf[j + 8];
                            if (jccOpcode == 0x75) // JNE (fail if not 1)
                            {
                                IntPtr patchAddr = IntPtr.Add(textBase, j + 5);

                                VirtualProtectEx(hProcess, patchAddr, 5,
                                    PAGE_EXECUTE_READWRITE, out uint oldProt);

                                // Replace CMP EAX,1 + JNE with NOPs + JMP-never
                                // Actually: change CMP EAX,1 to CMP EAX,EAX (always equal)
                                // so the JNE never triggers
                                byte[] patch = { 0x39, 0xC0, 0x90 }; // CMP EAX,EAX + NOP

                                if (MemoryHelper.WriteProcessMemory(hProcess, patchAddr,
                                    patch, 3, out int written) && written == 3)
                                {
                                    Console.WriteLine($"  [+] Patched CMP EAX,1 -> CMP EAX,EAX at 0x{patchAddr.ToInt64():X}");
                                    anyPatched = true;
                                }

                                VirtualProtectEx(hProcess, patchAddr, 5, oldProt, out _);
                            }
                        }
                    }

                    if (anyPatched) break; // patched near this string ref, move on
                }

                if (anyPatched) break; // one successful patch is enough
            }

            return anyPatched;
        }

        /// <summary>
        /// Searches the .rdata and .data sections for strings related to login.
        /// Returns addresses and decoded text.
        /// </summary>
        static List<(IntPtr addr, string text)> FindLoginStrings(IntPtr hProcess, IntPtr moduleBase)
        {
            var results = new List<(IntPtr, string)>();

            // GBK-encoded login-related strings
            string[] gbkSearches = { "登录", "密码", "用户", "账号", "卡密", "验证", "授权" };
            // ASCII search strings
            string[] asciiSearches = { "VIPQQ", "login", "Login", "password" };

            foreach (int sectionOff in new[] { 0x1C9000, 0x227000 })
            {
                IntPtr sectionBase = IntPtr.Add(moduleBase, sectionOff);
                int sectionSize = 0x60000;
                byte[] buf = new byte[sectionSize];
                MemoryHelper.ReadProcessMemory(hProcess, sectionBase, buf, sectionSize, out int read);
                if (read < 16) continue;

                foreach (string s in gbkSearches)
                {
                    byte[] needle;
                    try { needle = Encoding.GetEncoding("GBK").GetBytes(s); }
                    catch { continue; }

                    int pos = 0;
                    while ((pos = FindBytes(buf, needle, read, pos)) >= 0)
                    {
                        IntPtr addr = IntPtr.Add(sectionBase, pos);
                        Console.WriteLine($"  [*] Found \"{s}\" at 0x{addr.ToInt64():X}");
                        results.Add((addr, s));
                        pos += needle.Length;
                    }
                }

                foreach (string s in asciiSearches)
                {
                    byte[] needle = Encoding.ASCII.GetBytes(s);
                    int pos = 0;
                    while ((pos = FindBytes(buf, needle, read, pos)) >= 0)
                    {
                        IntPtr addr = IntPtr.Add(sectionBase, pos);
                        Console.WriteLine($"  [*] Found \"{s}\" at 0x{addr.ToInt64():X}");
                        results.Add((addr, s));
                        pos += needle.Length;
                    }
                }
            }

            return results;
        }

        static int FindBytes(byte[] haystack, byte[] needle, int haystackLen, int startPos = 0)
        {
            for (int i = startPos; i <= haystackLen - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        static bool AllZero(byte[] buf)
        {
            for (int i = 0; i < buf.Length; i++)
                if (buf[i] != 0) return false;
            return true;
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
                if (wndPid != targetPid)  return true;

                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "#32770")
                    dialogs.Add(hWnd);
                return true;
            }, IntPtr.Zero);

            foreach (var dlg in dialogs)
            {
                Console.WriteLine($"  [*] Dismissing dialog 0x{dlg.ToInt64():X}");

                // Try to click OK button inside
                EnumChildWindows(dlg, (child, __) =>
                {
                    var cc = new StringBuilder(128);
                    GetClassName(child, cc, cc.Capacity);
                    if (cc.ToString().ToLower().Contains("button"))
                    {
                        SendMessage(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        return false; // stop after first button
                    }
                    return true;
                }, IntPtr.Zero);

                Thread.Sleep(200);
                PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
