using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;

namespace xajh
{
    public class CombatOverlay
    {
        private IntPtr _hProcess;
        private IntPtr _moduleBase;
        private bool _running = true;
        private Npc _target;
        private NpcReader _npcReader;
        private PlayerReader _playerReader;

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

        public void Run()
        {
            Console.Clear();
            Console.WriteLine("=== XAJH Combat Engine Initialized ===");
            Console.WriteLine("Press [Home] to Toggle Auto-Combat | [End] to Exit");

            // Start logic thread
            new Thread(CombatLoop) { IsBackground = true }.Start();

            // Main UI Loop
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
                    // 1. Get Player Object Address (using the offset from your NpcReader notes)
                    // [moduleBase + 0x9D451C] -> NpcListMgr -> Player is usually found here or via Manager
                    int playerObj = GetPlayerObject();
                    if (playerObj == 0) { Thread.Sleep(1000); continue; }

                    // 2. Scan for nearest NPC
                    var npcs = _npcReader.GetAllNpcs();
                    var (px, py, pz) = _playerReader.Get();

                    _target = npcs
                        .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                        .FirstOrDefault();

                    if (_target != null)
                    {
                        // 3. Execute Auto-Face via Internal Call 0x6ACCCC
                        ExecuteRemoteFace(new IntPtr(playerObj), new IntPtr(_target.NpcObjAddr));

                        // 4. Brief delay to let the engine process the turn, then Send Attack Key
                        Thread.Sleep(1000);
                        // PostMessage 'F' to the game window if needed, 
                        // though 0x6ACCCC might trigger auto-pathing/combat depending on state.
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Loop Error: {ex.Message}"); }
                Thread.Sleep(200);
            }
        }

        /// <summary>
        /// Injects and executes shellcode to call the game's internal 0x6ACCCC function.
        /// This handles the __thiscall convention: ECX = Player, Param1 = Target.
        /// </summary>
        private void ExecuteRemoteFace(IntPtr playerPtr, IntPtr targetPtr)
        {
            if (playerPtr == IntPtr.Zero || targetPtr == IntPtr.Zero) return;

            // --- PROTECTIVE SHELLCODE ---
            // This version preserves all registers (pushad/popad) to prevent crashing 
            // the game's thread context.
            byte[] code = {
                0x60,                               // pushad
                0xB9, 0,0,0,0,                      // mov ecx, playerPtr  (patch bytes 2-5)
                0x68, 0,0,0,0,                      // push targetPtr      (patch bytes 7-10)
                0xB8, 0xCC,0xCC,0x6A,0x00,          // mov eax, 0x6ACCC0
                0xFF, 0xD0,                         // call eax
                0x61,                               // popad
                0xC2, 0x04, 0x00                    // ret 4 (LPTHREAD_START_ROUTINE stdcall)
            };

            IntPtr allocCode = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)code.Length, 0x1000, 0x40);
            if (allocCode == IntPtr.Zero) return;
            //IntPtr allocData = VirtualAllocEx(_hProcess, IntPtr.Zero, 8, 0x1000, 0x04);

            Array.Copy(BitConverter.GetBytes(playerPtr.ToInt32()), 0, code, 2, 4);
            Array.Copy(BitConverter.GetBytes(targetPtr.ToInt32()), 0, code, 7, 4);

            int written;
            WriteProcessMemory(_hProcess, allocCode, code, (uint)code.Length, out written);
            if (!WriteProcessMemory(_hProcess, allocCode, code, (uint)code.Length, out written) ||
                written != code.Length)
            {
                VirtualFreeEx(_hProcess, allocCode, 0, 0x8000);
                return;
            }

            // Create the thread
            IntPtr hThread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, allocCode, IntPtr.Zero, 0, out _);

            if (hThread != IntPtr.Zero)
            {
                // Wait for the thread to finish before freeing memory
                // Using WaitForSingleObject is safer than Thread.Sleep
                WaitForSingleObject(hThread, 1000);
                GetExitCodeThread(hThread, out uint ec);
                if (ec == 0xC0000005)
                    Console.WriteLine("[!] Remote face thread crashed with access violation.");
                CloseHandle(hThread);
            }

            // Clean up
            VirtualFreeEx(_hProcess, allocCode, 0, 0x8000);
            VirtualFreeEx(_hProcess, IntPtr.Zero, 0, 0x8000);
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