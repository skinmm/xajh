using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Prevents the game (vrchat1.exe) from auto-exiting by patching
    /// exit-related calls and integrity checks in process memory.
    ///
    /// This replicates the technique used by xajhtoy.exe + zxxy.dll:
    ///
    ///   1. IAT Patch  – Overwrites the game's Import Address Table entries
    ///      for ExitProcess / TerminateProcess so they point to a small
    ///      "ret" stub allocated in the game, effectively making exit calls
    ///      no-ops.
    ///
    ///   2. Inline Hook – Writes a "ret" instruction at the start of
    ///      ExitProcess and TerminateProcess inside the game's copy of
    ///      kernel32.dll, catching any calls that bypass the IAT
    ///      (e.g. GetProcAddress-based dynamic calls).
    ///
    ///   3. Exit-Check NOP – AOB-scans for the game's self-integrity /
    ///      anti-cheat conditional branches that lead to exit and replaces
    ///      them with NOPs so the branch is never taken.
    ///
    ///   4. Watchdog – A background thread periodically verifies that all
    ///      patches are still in place and re-applies them if the game's
    ///      protection layer restores the original bytes.
    /// </summary>
    public class AntiExitPatcher
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flNewProtect, out uint lpflOldProtect);

        const uint MEM_COMMIT_RESERVE = 0x3000;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        const uint PAGE_READWRITE = 0x04;

        private readonly IntPtr _hProcess;
        private readonly IntPtr _moduleBase;
        private readonly int _pid;
        private Thread _watchdog;
        private volatile bool _watchdogActive;

        private readonly List<PatchRecord> _patches = new();
        private IntPtr _retStubAddr = IntPtr.Zero;

        private record PatchRecord(IntPtr Address, byte[] PatchBytes, byte[] OriginalBytes, string Label);

        public AntiExitPatcher(IntPtr hProcess, IntPtr moduleBase, int pid)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
            _pid = pid;
        }

        public bool Apply()
        {
            Console.WriteLine("[*] AntiExitPatcher: Applying anti-exit patches ...");

            bool anySuccess = false;

            if (AllocRetStub())
            {
                anySuccess |= PatchIAT();
                anySuccess |= PatchInlineExitProcess();
                anySuccess |= PatchInlineTerminateProcess();
            }

            anySuccess |= PatchExitCheckPatterns();

            if (anySuccess)
            {
                StartWatchdog();
                Console.WriteLine($"[+] AntiExitPatcher: {_patches.Count} patch(es) active, watchdog running.");
            }
            else
            {
                Console.WriteLine("[!] AntiExitPatcher: No patches were applied.");
            }

            return anySuccess;
        }

        /// <summary>
        /// Allocates a tiny "ret" stub in the game's address space.
        /// This stub is the target for IAT redirections; calling it is harmless.
        ///
        /// Shellcode (x86):
        ///   xor eax, eax    ; return 0 (FALSE / failure — prevents callers
        ///                   ;   from thinking exit succeeded)
        ///   ret 4           ; stdcall: pop 1 DWORD argument
        /// </summary>
        private bool AllocRetStub()
        {
            byte[] stub = {
                0x31, 0xC0,       // xor eax, eax
                0xC2, 0x04, 0x00  // ret 4
            };

            _retStubAddr = VirtualAllocEx(_hProcess, IntPtr.Zero,
                (uint)stub.Length, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);

            if (_retStubAddr == IntPtr.Zero)
            {
                Console.WriteLine("[!] AntiExitPatcher: Failed to allocate ret-stub.");
                return false;
            }

            if (!MemoryHelper.WriteProcessMemory(_hProcess, _retStubAddr,
                    stub, stub.Length, out _))
            {
                Console.WriteLine("[!] AntiExitPatcher: Failed to write ret-stub.");
                return false;
            }

            Console.WriteLine($"[+] AntiExitPatcher: ret-stub at 0x{_retStubAddr.ToInt64():X8}");
            return true;
        }

        /// <summary>
        /// Walks the PE Import Directory of vrchat1.exe in memory and
        /// replaces IAT entries for ExitProcess / TerminateProcess with
        /// the address of our ret-stub.
        /// </summary>
        private bool PatchIAT()
        {
            try
            {
                IntPtr exitProc = GetProcAddress(GetModuleHandle("kernel32.dll"), "ExitProcess");
                IntPtr termProc = GetProcAddress(GetModuleHandle("kernel32.dll"), "TerminateProcess");
                if (exitProc == IntPtr.Zero && termProc == IntPtr.Zero) return false;

                int e_lfanew = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, 0x3C));
                IntPtr peHeader = IntPtr.Add(_moduleBase, e_lfanew);

                int importDirRva = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(peHeader, 0x80));
                if (importDirRva == 0) return false;

                IntPtr importDir = IntPtr.Add(_moduleBase, importDirRva);
                bool patched = false;

                for (int desc = 0; desc < 200; desc++)
                {
                    IntPtr entry = IntPtr.Add(importDir, desc * 20);
                    int iltRva = MemoryHelper.ReadInt32(_hProcess, entry);
                    int nameRva = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(entry, 12));
                    int iatRva = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(entry, 16));
                    if (iltRva == 0 && nameRva == 0) break;
                    if (iatRva == 0) continue;

                    string dllName = ReadAsciiString(_hProcess, IntPtr.Add(_moduleBase, nameRva), 64);
                    if (!dllName.ToLower().Contains("kernel32")) continue;

                    for (int i = 0; i < 2000; i++)
                    {
                        IntPtr iatSlot = IntPtr.Add(_moduleBase, iatRva + i * 4);
                        int funcAddr = MemoryHelper.ReadInt32(_hProcess, iatSlot);
                        if (funcAddr == 0) break;

                        bool isExit = (exitProc != IntPtr.Zero && funcAddr == exitProc.ToInt32());
                        bool isTerm = (termProc != IntPtr.Zero && funcAddr == termProc.ToInt32());

                        if (isExit || isTerm)
                        {
                            string label = isExit ? "ExitProcess IAT" : "TerminateProcess IAT";
                            byte[] origBytes = BitConverter.GetBytes(funcAddr);
                            byte[] patchBytes = BitConverter.GetBytes(_retStubAddr.ToInt32());

                            VirtualProtectEx(_hProcess, iatSlot, 4, PAGE_READWRITE, out uint oldProt);
                            if (MemoryHelper.WriteProcessMemory(_hProcess, iatSlot,
                                    patchBytes, 4, out _))
                            {
                                _patches.Add(new PatchRecord(iatSlot, patchBytes, origBytes, label));
                                Console.WriteLine($"[+] Patched {label} at 0x{iatSlot.ToInt64():X8}");
                                patched = true;
                            }
                            VirtualProtectEx(_hProcess, iatSlot, 4, oldProt, out _);
                        }
                    }
                }

                return patched;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] IAT patch error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Patches ExitProcess inline: overwrites the first bytes of the
        /// function in the game's kernel32.dll mapping with "xor eax,eax; ret 4"
        /// so even GetProcAddress-resolved calls become no-ops.
        /// </summary>
        private bool PatchInlineExitProcess()
        {
            return PatchInlineFunction("ExitProcess", new byte[] { 0x31, 0xC0, 0xC2, 0x04, 0x00 });
        }

        private bool PatchInlineTerminateProcess()
        {
            return PatchInlineFunction("TerminateProcess",
                new byte[] { 0x31, 0xC0, 0xC2, 0x08, 0x00 }); // ret 8 (two DWORD args)
        }

        private bool PatchInlineFunction(string funcName, byte[] patchBytes)
        {
            try
            {
                IntPtr funcAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), funcName);
                if (funcAddr == IntPtr.Zero) return false;

                byte[] origBytes = new byte[patchBytes.Length];
                if (!MemoryHelper.ReadProcessMemory(_hProcess, funcAddr,
                        origBytes, origBytes.Length, out int read) || read != origBytes.Length)
                    return false;

                VirtualProtectEx(_hProcess, funcAddr, (uint)patchBytes.Length,
                    PAGE_EXECUTE_READWRITE, out uint oldProt);

                if (MemoryHelper.WriteProcessMemory(_hProcess, funcAddr,
                        patchBytes, patchBytes.Length, out _))
                {
                    _patches.Add(new PatchRecord(funcAddr, patchBytes, origBytes,
                        $"{funcName} inline"));
                    Console.WriteLine($"[+] Patched {funcName} inline at 0x{funcAddr.ToInt64():X8}");
                    VirtualProtectEx(_hProcess, funcAddr, (uint)patchBytes.Length, oldProt, out _);
                    return true;
                }

                VirtualProtectEx(_hProcess, funcAddr, (uint)patchBytes.Length, oldProt, out _);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Inline patch error ({funcName}): {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// AOB-scans the game for known exit-check patterns and NOPs them out.
        ///
        /// Typical anti-cheat exit pattern in the game binary:
        ///   cmp [reg+offset], expected_value
        ///   jne exit_handler              ← conditional jump to ExitProcess call
        ///
        /// By replacing the conditional jump with NOPs the branch is never taken.
        ///
        /// Patterns below are derived from disassembly of the known
        /// integrity-check routines in vrchat1.exe.
        /// </summary>
        private bool PatchExitCheckPatterns()
        {
            var patterns = new (string aob, int patchOffset, int patchLen, string label)[]
            {
                // Pattern 1: call ExitProcess preceded by push 0 (exit code 0)
                // 6A 00 FF 15 xx xx xx xx  →  push 0; call [ExitProcess]
                // NOP the call entirely so the exit never fires
                ("6A 00 FF 15 ?? ?? ?? ??", 0, 8, "push 0; call [ExitProcess]"),

                // Pattern 2: call ExitProcess via register
                // 6A 00 FF D? → push 0; call reg
                // Only patch if the call target resolves to ExitProcess
                ("6A 00 FF D0", 0, 4, "push 0; call eax (ExitProcess)"),

                // Pattern 3: integrity check — compare + conditional jump to exit block
                // 85 C0 0F 85 xx xx xx xx → test eax, eax; jnz <far exit>
                // NOP the jnz so the exit block is never reached
                ("85 C0 0F 85 ?? ?? ?? ??", 2, 6, "test eax,eax; jnz exit_block"),
            };

            bool anyPatched = false;
            var progress = new Progress<string>(msg => { });

            foreach (var (aob, patchOffset, patchLen, label) in patterns)
            {
                try
                {
                    var hits = MemoryHelper.AobScan(_hProcess, aob, progress);
                    foreach (var hit in hits)
                    {
                        IntPtr patchAddr = IntPtr.Add(hit, patchOffset);

                        byte[] origBytes = new byte[patchLen];
                        MemoryHelper.ReadProcessMemory(_hProcess, patchAddr,
                            origBytes, patchLen, out _);

                        byte[] nopPatch = new byte[patchLen];
                        Array.Fill(nopPatch, (byte)0x90);

                        VirtualProtectEx(_hProcess, patchAddr, (uint)patchLen,
                            PAGE_EXECUTE_READWRITE, out uint oldProt);

                        if (MemoryHelper.WriteProcessMemory(_hProcess, patchAddr,
                                nopPatch, patchLen, out _))
                        {
                            _patches.Add(new PatchRecord(patchAddr, nopPatch, origBytes, label));
                            Console.WriteLine($"[+] NOP-patched \"{label}\" at 0x{patchAddr.ToInt64():X8}");
                            anyPatched = true;
                        }

                        VirtualProtectEx(_hProcess, patchAddr, (uint)patchLen, oldProt, out _);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] AOB scan error ({label}): {ex.Message}");
                }
            }

            return anyPatched;
        }

        /// <summary>
        /// Background thread that re-checks every patch site and re-applies
        /// any that were reverted by the game's protection system.
        /// </summary>
        private void StartWatchdog()
        {
            _watchdogActive = true;
            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "AntiExitWatchdog" };
            _watchdog.Start();
        }

        private void WatchdogLoop()
        {
            while (_watchdogActive)
            {
                Thread.Sleep(2000);
                foreach (var patch in _patches)
                {
                    try
                    {
                        byte[] current = new byte[patch.PatchBytes.Length];
                        if (!MemoryHelper.ReadProcessMemory(_hProcess, patch.Address,
                                current, current.Length, out int read) || read != current.Length)
                            continue;

                        bool intact = true;
                        for (int i = 0; i < current.Length; i++)
                        {
                            if (current[i] != patch.PatchBytes[i]) { intact = false; break; }
                        }

                        if (!intact)
                        {
                            VirtualProtectEx(_hProcess, patch.Address,
                                (uint)patch.PatchBytes.Length, PAGE_EXECUTE_READWRITE, out uint oldProt);
                            MemoryHelper.WriteProcessMemory(_hProcess, patch.Address,
                                patch.PatchBytes, patch.PatchBytes.Length, out _);
                            VirtualProtectEx(_hProcess, patch.Address,
                                (uint)patch.PatchBytes.Length, oldProt, out _);

                            Console.WriteLine($"[*] Watchdog: re-applied \"{patch.Label}\"");
                        }
                    }
                    catch { }
                }
            }
        }

        public void Stop()
        {
            _watchdogActive = false;
        }

        private static string ReadAsciiString(IntPtr hProcess, IntPtr address, int maxLen)
        {
            byte[] buf = new byte[maxLen];
            MemoryHelper.ReadProcessMemory(hProcess, address, buf, maxLen, out int read);
            int end = Array.IndexOf(buf, (byte)0);
            if (end < 0) end = read;
            return Encoding.ASCII.GetString(buf, 0, end);
        }
    }
}
