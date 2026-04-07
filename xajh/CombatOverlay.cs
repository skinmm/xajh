using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace xajh
{
    /// <summary>
    /// Calls the game's face-target function on the main thread by hijacking
    /// EIP via SuspendThread / GetThreadContext / SetThreadContext.
    ///
    /// Why this is necessary:
    ///   - Writing rotation matrices: values stick but renderer ignores them
    ///   - CreateRemoteThread: runs on wrong thread, game discards result
    ///   - Main thread hijack: the function runs in the game's own loop context,
    ///     exactly as if the player clicked — this is what zxxy.dll does
    /// </summary>
    public class CombatOverlay
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        const uint THREAD_SUSPEND_RESUME = 0x0002;
        const uint THREAD_GET_CONTEXT = 0x0008;
        const uint THREAD_SET_CONTEXT = 0x0010;
        const uint THREAD_ALL = THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT;
        const uint CONTEXT_FULL = 0x10007;

        [StructLayout(LayoutKind.Sequential)]
        struct CONTEXT
        {
            public uint ContextFlags;
            // Debug registers
            public uint Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
            // Float save area (112 bytes)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)]
            public byte[] FloatSave;
            // Segment registers
            public uint SegGs, SegFs, SegEs, SegDs;
            // GP registers
            public uint Edi, Esi, Ebx, Edx, Ecx, Eax;
            // Control registers
            public uint Ebp, Eip, SegCs, EFlags, Esp, SegSs;
            // Extended registers (512 bytes)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
            public byte[] ExtendedRegisters;
        }

        private IntPtr _hProcess;
        private IntPtr _moduleBase;
        private int _pid;
        private IntPtr _funcEntry = IntPtr.Zero;

        public CombatOverlay(IntPtr hProcess, IntPtr moduleBase, int pid)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
            _pid = pid;
        }

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

            if (targetPtr == IntPtr.Zero) return null;

            if (_funcEntry == IntPtr.Zero)
            {
                _funcEntry = FindFunctionEntry(new IntPtr(0x6ACCCC));
                if (_funcEntry == IntPtr.Zero)
                    _funcEntry = new IntPtr(0x6ACCCC);
                Console.WriteLine($"[+] Face function at 0x{_funcEntry.ToInt64():X8}");
            }

            bool ok = ExecuteOnMainThread(new IntPtr(playerObj), targetPtr);

            // After the player turns, sync the back-trace camera to face the same direction.
            if (ok)
            {
                float dx = nearest.X - px;
                float dz = nearest.Z - pz;
                float yaw = (float)Math.Atan2(dx, dz);
                SyncCameraYaw(yaw);
            }

            float ddx = nearest.X - px;
            float ddz = nearest.Z - pz;
            double dist = Math.Sqrt(ddx * ddx + ddz * ddz);
            string status = ok ? "OK" : "FAIL";
            return $"{nearest.Name} (dist={dist:F0}) [{status}]";
        }

        /// <summary>
        /// Writes the yaw angle into the back-trace camera's rotation matrices.
        /// The camera object is accessed via [moduleBase + 0x9D4518] -> mgr.
        /// The camera typically stores its own 3x3 rotation at an offset from
        /// a camera pointer accessible near the player manager.
        ///
        /// Common camera pointer chains in this engine:
        ///   [moduleBase + 0x9E2C60] -> camera mgr -> camera obj
        /// or the camera transform is embedded in the same manager.
        ///
        /// We scan for the camera yaw by looking at the player's rotation
        /// matrices (which the face function just updated) and writing the
        /// same cos/sin values to the camera's known rotation offsets.
        /// </summary>
        private void SyncCameraYaw(float yaw)
        {
            float cosY = (float)Math.Cos(yaw);
            float sinY = (float)Math.Sin(yaw);

            // The camera yaw address is found via a scan the first time.
            // Common approach: write to a known static camera address.
            if (_camYawAddr == IntPtr.Zero)
                _camYawAddr = FindCameraYawAddr(yaw);

            if (_camYawAddr != IntPtr.Zero)
            {
                MemoryHelper.WriteFloat(_hProcess, _camYawAddr, yaw);
            }

            // Also write camera rotation matrices if we found them
            if (_camMatrixBase != IntPtr.Zero)
            {
                // Same layout as player: forward rotation at +0x10, inverse at +0x40
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x10), cosY);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x14), -sinY);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x1C), sinY);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x20), cosY);

                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x40), cosY);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x44), sinY);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x4C), -sinY);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(_camMatrixBase, 0x50), cosY);
            }
        }

        private IntPtr _camYawAddr = IntPtr.Zero;
        private IntPtr _camMatrixBase = IntPtr.Zero;

        /// <summary>
        /// Scans writable memory for float values matching the current camera yaw.
        /// The camera yaw should closely match the player's current facing direction.
        /// We look for a standalone yaw float (not part of the player object).
        /// </summary>
        private IntPtr FindCameraYawAddr(float expectedYaw)
        {
            // Try known camera pointer chains first
            // Chain 1: [moduleBase + 0x9E2C60] -> +0x?? -> camera yaw
            int camMgr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, 0x9E2C60));
            if (camMgr != 0)
            {
                var camBase = new IntPtr((uint)camMgr);
                // Scan the camera manager object for yaw-like floats
                for (int off = 0; off < 0x200; off += 4)
                {
                    float val = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(camBase, off));
                    if (!float.IsNaN(val) && !float.IsInfinity(val) &&
                        Math.Abs(val - expectedYaw) < 0.5f)
                    {
                        Console.WriteLine($"[+] Camera yaw candidate at camMgr+0x{off:X} = {val:F4}");
                        return IntPtr.Add(camBase, off);
                    }
                }

                // Also try following pointers from the camera manager
                for (int ptrOff = 0; ptrOff < 0x40; ptrOff += 4)
                {
                    int sub = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(camBase, ptrOff));
                    if (sub < 0x10000 || sub > 0x7FFFFFFF) continue;
                    var subPtr = new IntPtr((uint)sub);
                    for (int off = 0; off < 0x200; off += 4)
                    {
                        float val = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(subPtr, off));
                        if (!float.IsNaN(val) && !float.IsInfinity(val) &&
                            Math.Abs(val - expectedYaw) < 0.5f)
                        {
                            Console.WriteLine($"[+] Camera yaw at [camMgr+0x{ptrOff:X}]+0x{off:X} = {val:F4}");
                            _camMatrixBase = subPtr;
                            return IntPtr.Add(subPtr, off);
                        }
                    }
                }
            }

            Console.WriteLine("[!] Camera yaw address not found automatically.");
            return IntPtr.Zero;
        }

        /// <summary>
        /// Hijacks the game's main thread to execute the face function:
        ///   1. Suspend the main thread
        ///   2. Save its context (registers + EIP)
        ///   3. Write shellcode that calls face(player, target) then spins on a flag
        ///   4. Set EIP to shellcode, resume thread
        ///   5. Wait for the flag, then restore original context and resume
        /// </summary>
        private bool ExecuteOnMainThread(IntPtr playerPtr, IntPtr targetPtr)
        {
            var proc = System.Diagnostics.Process.GetProcessById(_pid);
            if (proc.Threads.Count == 0) return false;

            uint mainTid = (uint)proc.Threads[0].Id;
            IntPtr hThread = OpenThread(THREAD_ALL, false, mainTid);
            if (hThread == IntPtr.Zero) return false;

            try
            {
                if (SuspendThread(hThread) == 0xFFFFFFFF) return false;

                var ctx = new CONTEXT
                {
                    ContextFlags = CONTEXT_FULL,
                    FloatSave = new byte[112],
                    ExtendedRegisters = new byte[512]
                };

                if (!GetThreadContext(hThread, ref ctx))
                {
                    ResumeThread(hThread);
                    return false;
                }

                uint savedEip = ctx.Eip;
                uint savedEcx = ctx.Ecx;
                uint savedEax = ctx.Eax;
                uint savedEsp = ctx.Esp;

                // Shellcode: call face function then loop on a "done" flag.
                //
                //   mov ecx, <playerPtr>         ; __thiscall this
                //   push <targetPtr>             ; argument
                //   mov eax, <funcEntry>
                //   call eax                     ; face function
                //   mov byte [<flagAddr>], 1     ; signal completion
                // spin:
                //   pause
                //   jmp spin                     ; spin until we restore context
                //
                // We use a flag so we know the call completed before restoring.

                byte[] code = {
                    0xB9, 0,0,0,0,                  // +0x00: mov ecx, playerPtr  [1..4]
                    0x68, 0,0,0,0,                  // +0x05: push targetPtr      [6..9]
                    0xB8, 0,0,0,0,                  // +0x0A: mov eax, funcEntry  [11..14]
                    0xFF, 0xD0,                     // +0x0F: call eax
                    0xC6, 0x05, 0,0,0,0, 0x01,      // +0x11: mov byte [flagAddr], 1  [19..22]
                    0xF3, 0x90,                     // +0x18: pause
                    0xEB, 0xFC                      // +0x1A: jmp -4 (back to pause)
                };

                IntPtr codeMem = VirtualAllocEx(_hProcess, IntPtr.Zero,
                    (uint)(code.Length + 4), 0x3000, 0x40);
                if (codeMem == IntPtr.Zero) { ResumeThread(hThread); return false; }

                IntPtr flagAddr = IntPtr.Add(codeMem, code.Length);

                Array.Copy(BitConverter.GetBytes(playerPtr.ToInt32()), 0, code, 1, 4);
                Array.Copy(BitConverter.GetBytes(targetPtr.ToInt32()), 0, code, 6, 4);
                Array.Copy(BitConverter.GetBytes(_funcEntry.ToInt32()), 0, code, 11, 4);
                Array.Copy(BitConverter.GetBytes(flagAddr.ToInt32()), 0, code, 19, 4);

                // Write shellcode + zero flag byte
                byte[] codeAndFlag = new byte[code.Length + 4];
                Array.Copy(code, codeAndFlag, code.Length);
                MemoryHelper.WriteProcessMemory(_hProcess, codeMem,
                    codeAndFlag, codeAndFlag.Length, out _);

                // Redirect main thread to our shellcode
                ctx.Eip = (uint)codeMem.ToInt32();
                // Push a fake return address on the stack so the call doesn't corrupt
                // the real stack — point it at our spin loop so if the function returns
                // via ret it lands in the spin.
                ctx.Esp -= 4;
                byte[] retAddr = BitConverter.GetBytes((uint)codeMem.ToInt32() + 0x18);
                MemoryHelper.WriteProcessMemory(_hProcess, new IntPtr(ctx.Esp),
                    retAddr, 4, out _);

                SetThreadContext(hThread, ref ctx);
                ResumeThread(hThread);

                // Wait for the face function to complete (flag byte becomes 1)
                byte[] flagBuf = new byte[1];
                for (int i = 0; i < 500; i++)
                {
                    System.Threading.Thread.Sleep(10);
                    MemoryHelper.ReadProcessMemory(_hProcess, flagAddr, flagBuf, 1, out _);
                    if (flagBuf[0] == 1) break;
                }

                // Restore original context: suspend again, put registers back
                SuspendThread(hThread);
                ctx.Eip = savedEip;
                ctx.Ecx = savedEcx;
                ctx.Eax = savedEax;
                ctx.Esp = savedEsp;
                SetThreadContext(hThread, ref ctx);
                ResumeThread(hThread);

                VirtualFreeEx(_hProcess, codeMem, 0, 0x8000);
                return flagBuf[0] == 1;
            }
            finally
            {
                CloseHandle(hThread);
            }
        }

        private IntPtr FindFunctionEntry(IntPtr midFuncAddr, int maxScanBack = 0x200)
        {
            byte[] buf = new byte[maxScanBack];
            IntPtr scanStart = IntPtr.Add(midFuncAddr, -maxScanBack);
            if (!MemoryHelper.ReadProcessMemory(_hProcess, scanStart,
                    buf, buf.Length, out int read) || read < 3)
                return IntPtr.Zero;

            for (int i = read - 3; i >= 0; i--)
            {
                if (buf[i] == 0x55 && buf[i + 1] == 0x8B && buf[i + 2] == 0xEC)
                    return IntPtr.Add(scanStart, i);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Wide dump: reads +0x000 to +0x400 of the player object, prints every
        /// DWORD as both float and int.  Highlights values in the yaw range
        /// (-π to +π) so we can find the source yaw field that the game uses to
        /// recalculate the rotation matrices each frame.
        ///
        /// Usage: press [D] to dump, turn in-game, press key to dump again.
        /// Compare both dumps — the float that changed AND is in [-3.15, 3.15]
        /// range is the source yaw field.
        /// </summary>
        public float[] DumpPlayerWide()
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0)
            {
                Console.WriteLine("[!] Cannot read player object.");
                return null;
            }

            var obj = new IntPtr((uint)playerObj);
            const int dumpLen = 0x400;
            var buf = new byte[dumpLen];
            MemoryHelper.ReadProcessMemory(_hProcess, obj, buf, dumpLen, out _);

            float cosA = BitConverter.ToSingle(buf, 0x70);
            float sinA = BitConverter.ToSingle(buf, 0x7C);
            float curYaw = (float)Math.Atan2(sinA, cosA);

            Console.WriteLine($"\n── Player 0x{playerObj:X8} — wide dump (0x000..0x{dumpLen:X}) ──");
            Console.WriteLine($"  Matrix yaw: {curYaw:F4} rad ({curYaw * 180f / Math.PI:F1} deg)");
            Console.WriteLine($"  {"Off",-7} {"float",12} {"int",12}  Note");
            Console.WriteLine(new string('─', 65));

            var floats = new float[dumpLen / 4];
            for (int off = 0; off < dumpLen; off += 4)
            {
                float fv = BitConverter.ToSingle(buf, off);
                int iv = BitConverter.ToInt32(buf, off);
                floats[off / 4] = fv;

                string note = "";
                if (off == 0x94) note = "  ← X";
                else if (off == 0x98) note = "  ← Y";
                else if (off == 0x9C) note = "  ← Z";
                else if (off == 0x70) note = "  ← A.cos";
                else if (off == 0x7C) note = "  ← A.sin";
                else if (!float.IsNaN(fv) && !float.IsInfinity(fv) &&
                         Math.Abs(fv) > 0.01f && Math.Abs(fv) <= Math.PI + 0.1f)
                {
                    note = "  ← YAW?";
                }

                if (!float.IsNaN(fv) && !float.IsInfinity(fv))
                    Console.WriteLine($"  +0x{off:X3}  {fv,12:F4} {iv,12}{note}");
            }
            Console.WriteLine();
            return floats;
        }

        /// <summary>
        /// Compares two wide dumps and prints offsets where the float value changed.
        /// </summary>
        public static void CompareDumps(float[] before, float[] after)
        {
            if (before == null || after == null) return;
            Console.WriteLine("── Changed floats ──");
            Console.WriteLine($"  {"Off",-7} {"Before",12} {"After",12}");
            Console.WriteLine(new string('─', 40));
            int count = Math.Min(before.Length, after.Length);
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(before[i] - after[i]) > 0.0001f)
                {
                    int off = i * 4;
                    Console.WriteLine($"  +0x{off:X3}  {before[i],12:F4} {after[i],12:F4}");
                }
            }
            Console.WriteLine();
        }

        private int GetPlayerObject()
        {
            int mgr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, 0x9D4518));
            if (mgr == 0) return 0;
            int list = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)(mgr + 8)));
            if (list == 0) return 0;
            return MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)(list + 0x4C)));
        }
    }
}