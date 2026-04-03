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
        private const uint INFINITE = 0xFFFFFFFF;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint WAIT_TIMEOUT = 0x00000102;
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
                        // 3. Execute Auto-Face via Internal Call 0x6ACCCC.
                        // Prefer node pointer first; fallback to npc_obj pointer if needed.
                        IntPtr targetPtr = _target.NodeAddr != 0
                                                    ? new IntPtr(_target.NodeAddr)
                                                    : new IntPtr(_target.NpcObjAddr);
                        ExecuteRemoteFace(new IntPtr(playerObj), targetPtr);

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
            if (!CanRead(playerPtr, 0x108) || !CanRead(targetPtr, 4)) return;
            // --- PROTECTIVE SHELLCODE ---
            // 0x6ACCCC is in the middle of a prologued function and expects [ebp-4] = this.
            // Build a tiny frame so thiscall state is valid before jumping there.
            byte[] code = {
                0x60,                               // pushad
                0x55,                               // push ebp
                0x8B, 0xEC,                         // mov ebp, esp
                0x83, 0xEC, 0x50,                   // sub esp, 0x50
                0xB8, 0,0,0,0,                      // mov eax, playerPtr  (patch bytes 8-11)
                0x89, 0x45, 0xFC,                   // mov [ebp-4], eax
                0x68, 0,0,0,0,                      // push targetPtr      (patch bytes 16-19)
                0xB8, 0xCC,0xCC,0x6A,0x00,          // mov eax, 0x6ACCC0
                0xFF, 0xD0,                         // call eax
                0x8B, 0xE5,                         // mov esp, ebp
                0x5D,                               // pop ebp
                0x61,                               // popad
                0xC2, 0x04, 0x00                    // ret 4 (LPTHREAD_START_ROUTINE stdcall)
            };

            IntPtr allocCode = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)code.Length, 0x1000, 0x40);
            if (allocCode == IntPtr.Zero) return;
            //IntPtr allocData = VirtualAllocEx(_hProcess, IntPtr.Zero, 8, 0x1000, 0x04);

            Array.Copy(BitConverter.GetBytes(playerPtr.ToInt32()), 0, code, 8, 4);
            Array.Copy(BitConverter.GetBytes(targetPtr.ToInt32()), 0, code, 16, 4);

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
                // Avoid freeing shellcode while thread still executes it.
                uint waitRc = WaitForSingleObject(hThread, INFINITE);
                if (waitRc != WAIT_OBJECT_0)
                {
                    if (waitRc == WAIT_TIMEOUT)
                        Console.WriteLine("[!] Remote face thread timed out; keeping code memory allocated.");
                    else
                        Console.WriteLine("[!] WaitForSingleObject failed for remote face thread.");
                    CloseHandle(hThread);
                    return;
                 }

                GetExitCodeThread(hThread, out uint ec);
                if (ec == 0xC0000005)
                    Console.WriteLine("[!] Remote face thread crashed with access violation.");
                CloseHandle(hThread);
            }

            // Clean up
            VirtualFreeEx(_hProcess, allocCode, 0, 0x8000);
            VirtualFreeEx(_hProcess, IntPtr.Zero, 0, 0x8000);
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