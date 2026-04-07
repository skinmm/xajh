using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace xajh
{
    public class CombatOverlay
    {
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private IntPtr _hProcess;
        private IntPtr _moduleBase;
        private bool _running = true;
        private Npc _target;
        private NpcReader _npcReader;
        private PlayerReader _playerReader;
        private IntPtr _funcEntryCache = IntPtr.Zero;

        // --- Win32 API for Remote Execution ---
        // Add this to your Win32 imports in CombatOverlay.cs
        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        public CombatOverlay(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
            _npcReader = new NpcReader(hProcess, moduleBase);
            _playerReader = new PlayerReader(hProcess, moduleBase);
        }

        /// <summary>
        /// One-shot: face the nearest NPC from a given player position.
        /// Returns the name of the NPC faced, or null if none found.
        /// </summary>
        public string FaceNearest(float px, float py, float pz, List<Npc> npcs)
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0) return null;

            var nearest = npcs
                .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                .FirstOrDefault();

            if (nearest == null) return null;

            IntPtr targetPtr = nearest.NodeAddr != 0
                ? new IntPtr(nearest.NodeAddr)
                : new IntPtr(nearest.NpcObjAddr);

            ExecuteRemoteFace(new IntPtr(playerObj), targetPtr);
            return nearest.Name;
        }

        public void Run()
        {
            Console.Clear();
            Console.WriteLine("=== XAJH Combat Engine Initialized ===");
            Console.WriteLine("Press [Home] to Toggle Auto-Combat | [End] to Exit");

            new Thread(CombatLoop) { IsBackground = true }.Start();

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.Home) _running = !_running;
                    if (key == ConsoleKey.End) break;
                }
                UpdateUI();
                Thread.Sleep(100);
            }
        }

        private void CombatLoop()
        {
            while (true)
            {
                if (!_running) { Thread.Sleep(500); continue; }

                try
                {
                    var npcs = _npcReader.GetAllNpcs();
                    var (px, py, pz) = _playerReader.Get();

                    string faced = FaceNearest(px, py, pz, npcs);
                    if (faced != null)
                    {
                        _target = npcs
                            .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                            .FirstOrDefault();
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Loop Error: {ex.Message}"); }
                Thread.Sleep(200);
            }
        }

        /// <summary>
        /// Searches backwards from a known mid-function address to locate the
        /// function's real entry point (the <c>push ebp; mov ebp, esp</c> prologue).
        ///
        /// The game is compiled with MSVC debug CRT which inserts stack-check
        /// guards (chkesp).  Jumping into the middle of a function bypasses
        /// the prologue, causing an ESP mismatch on return and triggering
        /// the CRT "Debug Error — ESP not properly saved" dialog, which
        /// terminates the process.
        ///
        /// xajhtoy.exe / zxxy.dll avoids this by always calling the function
        /// from its real entry so the prologue and epilogue stay balanced.
        /// </summary>
        private IntPtr FindFunctionEntry(IntPtr midFuncAddr, int maxScanBack = 0x200)
        {
            byte[] buf = new byte[maxScanBack];
            IntPtr scanStart = IntPtr.Add(midFuncAddr, -maxScanBack);
            if (!MemoryHelper.ReadProcessMemory(_hProcess, scanStart,
                    buf, buf.Length, out int read) || read < 3)
                return IntPtr.Zero;

            // Scan backwards from the known address looking for 55 8B EC
            // (push ebp; mov ebp, esp) — standard MSVC function prologue.
            // Take the LAST occurrence before midFuncAddr (closest match).
            int bestOffset = -1;
            for (int i = read - 3; i >= 0; i--)
            {
                if (buf[i] == 0x55 && buf[i + 1] == 0x8B && buf[i + 2] == 0xEC)
                {
                    bestOffset = i;
                    break;
                }
            }

            if (bestOffset < 0) return IntPtr.Zero;
            return IntPtr.Add(scanStart, bestOffset);
        }

        /// <summary>
        /// Injects shellcode that calls the face-target game function using
        /// proper __thiscall calling convention:
        ///   - ECX = player this-pointer
        ///   - Single stack argument = target NPC pointer
        ///   - Function called at its real entry point (not mid-function)
        ///   - Callee cleans the stack argument (__thiscall)
        ///
        /// This is how xajhtoy.exe/zxxy.dll does it: the DLL lives inside
        /// the game process and calls the function at its entry with the
        /// correct convention, so the prologue/epilogue stay balanced and
        /// the MSVC CRT stack-check (chkesp) never fires.
        /// </summary>
        private void ExecuteRemoteFace(IntPtr playerPtr, IntPtr targetPtr)
        {
            if (playerPtr == IntPtr.Zero || targetPtr == IntPtr.Zero) return;
            if (!CanRead(playerPtr, 0x108) || !CanRead(targetPtr, 4)) return;

            // 0x6ACCCC is a known code address inside the face-target function.
            // Find the real entry point by scanning back for the MSVC prologue.
            IntPtr knownAddr = new IntPtr(0x6ACCCC);
            if (_funcEntryCache == IntPtr.Zero)
            {
                _funcEntryCache = FindFunctionEntry(knownAddr);
                if (_funcEntryCache == IntPtr.Zero)
                {
                    Console.WriteLine("[!] Could not locate face-function entry; " +
                                      "falling back to known address.");
                    _funcEntryCache = knownAddr;
                }
                else
                {
                    Console.WriteLine($"[+] Face-function entry found at " +
                                      $"0x{_funcEntryCache.ToInt64():X8} " +
                                      $"(scanned back from 0x{knownAddr.ToInt64():X8})");
                }
            }

            // Shellcode layout (x86):
            //
            //   pushad                 ; save all GP registers
            //   pushfd                 ; save EFLAGS
            //   mov ecx, <playerPtr>   ; __thiscall: ECX = this
            //   push <targetPtr>       ; single stack argument (target NPC)
            //   mov eax, <funcEntry>   ; address of the function entry
            //   call eax               ; call — function does its own
            //                          ;   push ebp / mov ebp,esp / sub esp,N
            //                          ;   and cleans the 4-byte arg on ret
            //   popfd                  ; restore EFLAGS
            //   popad                  ; restore GP registers
            //   ret 4                  ; stdcall return for CreateRemoteThread
            //                          ;   (cleans the LPVOID lpParameter)
            //
            // The function's own prologue/epilogue are fully balanced, so
            // ESP is preserved across the call and chkesp never fires.

            byte[] code = {
                0x60,                               // pushad
                0x9C,                               // pushfd
                0xB9, 0,0,0,0,                      // mov ecx, playerPtr   [patch 3..6]
                0x68, 0,0,0,0,                      // push targetPtr       [patch 8..11]
                0xB8, 0,0,0,0,                      // mov eax, funcEntry   [patch 13..16]
                0xFF, 0xD0,                         // call eax
                0x9D,                               // popfd
                0x61,                               // popad
                0xC2, 0x04, 0x00                    // ret 4
            };

            Array.Copy(BitConverter.GetBytes(playerPtr.ToInt32()), 0, code, 3, 4);
            Array.Copy(BitConverter.GetBytes(targetPtr.ToInt32()), 0, code, 8, 4);
            Array.Copy(BitConverter.GetBytes(_funcEntryCache.ToInt32()), 0, code, 13, 4);

            IntPtr allocCode = VirtualAllocEx(_hProcess, IntPtr.Zero,
                (uint)code.Length, 0x1000, 0x40);
            if (allocCode == IntPtr.Zero) return;

            if (!WriteProcessMemory(_hProcess, allocCode, code,
                    (uint)code.Length, out int written) || written != code.Length)
            {
                VirtualFreeEx(_hProcess, allocCode, 0, 0x8000);
                return;
            }

            IntPtr hThread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0,
                allocCode, IntPtr.Zero, 0, out _);

            if (hThread != IntPtr.Zero)
            {
                uint waitRc = WaitForSingleObject(hThread, 5000);
                if (waitRc != WAIT_OBJECT_0)
                {
                    Console.WriteLine("[!] Remote face thread did not complete in time.");
                    CloseHandle(hThread);
                    return;
                }

                GetExitCodeThread(hThread, out uint ec);
                if (ec == 0xC0000005)
                    Console.WriteLine("[!] Remote face thread: access violation.");
                CloseHandle(hThread);
            }

            VirtualFreeEx(_hProcess, allocCode, 0, 0x8000);
        }

        private bool CanRead(IntPtr basePtr, int size)
        {
            if (basePtr == IntPtr.Zero || size <= 0) return false;
            var tmp = new byte[size];
            return MemoryHelper.ReadProcessMemory(_hProcess, basePtr, tmp, size, out int read) &&
                   read == size;
        }
        private int GetPlayerObject()
        {
            // Based on your NpcReader notes: [moduleBase + 0x9D4518] is the Manager
            int mgr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, 0x9D4518));
            if (mgr == 0) return 0;
            int list = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)(mgr + 8)));
            if (list == 0) return 0;
            // Usually the first entry or a dedicated offset 0x4C
            return MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)(list + 0x4C)));
        }

        private void UpdateUI()
        {
            try
            {
                Console.SetCursorPosition(0, 5);
                string status = _running ? "ACTIVE  " : "DISABLED";
                Console.WriteLine($"Status: [{status}] Target: {(_target?.Name ?? "None"),-20}");
            }
            catch { }
        }
    }
}