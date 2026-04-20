using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Fight by calling game+0x2859AE(Y, X, 1) with NPC coordinates.
    /// This mirrors XajhSmileDll.dll DLL@0x1790 exactly — no X/F keys.
    /// The game engine handles auto-attack when player moves toward target.
    ///
    /// Signature: game+0x2859AE(ECX=[978AE0→+58→+0C→+94], arg1=Y, arg2=X, arg3=1)
    /// Called TWICE as DLL does it.
    ///
    /// Turn: game+0x277EE9→game+0x277E53 (confirmed working).
    /// </summary>
    public class TurnHelper
    {
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);
        [DllImport("kernel32.dll")] static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttr, uint stackSize, IntPtr lpStartAddr, IntPtr lpParam, uint flags, out uint lpThreadId);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr hObject, uint dwMilliseconds);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr hThread);
        [DllImport("kernel32.dll")] static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT lpContext);
        [DllImport("kernel32.dll")] static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT lpContext);
        [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint dwAccess, bool inherit, uint threadId);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        static void ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            IntPtr fg = GetForegroundWindow();
            if (fg == hWnd) return;
            uint fgTid = GetWindowThreadProcessId(fg, out _);
            uint curTid = GetCurrentThreadId();
            AttachThreadInput(curTid, fgTid, true);
            ShowWindow(hWnd, 9); // SW_RESTORE
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            AttachThreadInput(curTid, fgTid, false);
        }
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, SINPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT2 { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT2 { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct SINPUTUNION { [FieldOffset(0)] public KEYBDINPUT2 ki; [FieldOffset(0)] public MOUSEINPUT2 mi; }
        [StructLayout(LayoutKind.Sequential)] struct SINPUT { public uint type; public SINPUTUNION u; }

        [StructLayout(LayoutKind.Sequential)]
        struct CONTEXT
        {
            public uint ContextFlags; // must be 0x10007
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] Dr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)] public byte[] FloatSave;
            public uint SegGs, SegFs, SegEs, SegDs;
            public uint Edi, Esi, Ebx, Edx, Ecx, Eax;
            public uint Ebp, Eip, SegCs, EFlags, Esp, SegSs;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] ExtendedRegisters;
        }

        const uint MEM_COMMIT_RESERVE = 0x3000;
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        const uint MEM_RELEASE = 0x8000;
        const int VK_X = 0x58, VK_F = 0x46;
        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;

        // Scratch globals for passing float coords to shellcode
        const uint ADDR_SCRATCH_X = 0xDD55C4;   // game+0x9D55C4 — target X float
        const uint ADDR_SCRATCH_Y = 0xDD55E4;   // game+0x9D55E4 — target Y float
        const uint ADDR_SCRATCH_A = 0xDD5574;   // game+0x9D5574 — angle/FPU scratch
        const uint ADDR_NPC_TARGET = 0xDD55BC;   // game+0x9D55BC — NPC entity ptr for attack
        const uint ADDR_CHAIN_D = 0xDCA8A0;   // [game+0x9CA8A0] for turn
        const uint FN_277EE9 = 0x677EE9;
        const uint FN_277E53 = 0x677E53;
        const uint FN_2859AE = 0x6859AE;
        const uint FN_2871AF = 0x6871AF;
        const uint FN_27DD35 = 0x67DD35;

        readonly IntPtr _hProcess, _moduleBase, _gameHwnd;
        uint _mainThreadId = 0;

        public TurnHelper(IntPtr hProcess, IntPtr moduleBase, IntPtr gameHwnd)
        {
            _hProcess = hProcess; _moduleBase = moduleBase; _gameHwnd = gameHwnd;
        }

        public void SetMainThreadId(uint threadId) { _mainThreadId = threadId; }

        public void SetPlayerObjectHint(int obj) { }
        public float ReadPlayerYaw(int playerObj) => float.NaN;
        public void ResetCalibration() { }

        /// <summary>
        /// Turn toward (tx, ty) via game+0x277EE9→game+0x277E53.
        /// </summary>
        /// <summary>Send a raw angle directly to 277EE9→277E53 for calibration.</summary>
        public void FaceTargetRaw(float angle)
        {
            CallFace277E53Direct(angle);
        }

        public string FaceTarget(Func<(float x, float y, float z)> readPos, float tx, float ty)
        {
            var (px, py, _) = readPos();
            float dx = tx - px, dy = ty - py;
            float angle = (float)Math.Atan2(dy, dx);  // standard: +Y=north, +X=east
            float angleDeg = angle * 180f / (float)Math.PI;

            CallFace277E53Direct(angle);
            return $"p=({px:F0},{py:F0}) npc=({tx:F0},{ty:F0}) dx={dx:F0} dy={dy:F0} angle={angleDeg:F0}deg raw={angleDeg:F0}deg";
        }

        /// <summary>
        /// Call game+0x277E53 directly with absolute angle, bypassing 277EE9.
        /// 277EE9 is non-deterministic (reads stale FPU state from game thread).
        /// 277E53(ECX=[9CA8A0], float_angle, -1, 0, 1) sets absolute player facing.
        /// </summary>
        private void CallFace277E53Direct(float angle)
        {
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(ADDR_SCRATCH_A), angle);
            byte[] sc = {
                0x60,
                0x6A, 0x01,                                   // PUSH 1
                0x6A, 0x00,                                   // PUSH 0
                0x68, 0xFF, 0xFF, 0xFF, 0xFF,                // PUSH -1
                0xA1, 0x74, 0x55, 0xDD, 0x00, 0x50,         // MOV EAX,[ADDR_SCRATCH_A]; PUSH (float)
                0xA1, 0xA0, 0xA8, 0xDC, 0x00,               // MOV EAX,[9CA8A0]
                0x85, 0xC0, 0x74, 0x07,                      // JZ skip
                0x8B, 0xC8,                                   // MOV ECX,EAX
                0xB8, 0x53, 0x7E, 0x67, 0x00,               // MOV EAX,0x677E53
                0xFF, 0xD0,                                   // CALL 277E53
                0x61, 0xC3
            };
            RunShellcode(sc);
        }


        /// <summary>
        /// Move toward NPC at (tx, ty) via game+0x2859AE(Y, X, 1) called twice.
        /// Mirrors DLL@0x1790 exactly — game auto-attacks when player reaches target.
        /// </summary>
        /// <summary>
        /// Attack NPC via DLL@0x1940 pattern:
        ///   game+0x2859AE(4,2,8) via 978AE0 chain  — enter combat stance
        ///   game+0x2871AF(npcPtr,8,2) via 9D4514   — attack action
        ///   game+0x2859AE(4,2,8) via 978AE0 chain  — confirm
        /// </summary>
        /// <summary>
        /// <summary>
        /// Find XajhSmileDll.dll base address in the game process.
        /// </summary>
        private uint FindDllBase()
        {
            // Scan memory for the DLL's preferred base 0x10000000 area
            // The DLL loads at preferred base unless ASLR moves it
            // Verify by checking for DLL@0x1700's known first bytes: 55 8B EC 51 A1
            byte[] sig = { 0x55, 0x8B, 0xEC, 0x51, 0xA1 };
            // Try preferred base first
            IntPtr testAddr = new IntPtr(0x10001700);
            var buf = new byte[5];
            MemoryHelper.ReadProcessMemory(_hProcess, testAddr, buf, 5, out int nr);
            if (nr == 5 && buf[0] == sig[0] && buf[1] == sig[1] && buf[2] == sig[2])
                return 0x10000000;

            // Scan for DLL in 0x10000000-0x18000000 range at 0x10000 boundaries
            for (uint b = 0x10000000; b < 0x18000000; b += 0x10000)
            {
                var tb = new byte[5];
                MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(b + 0x1700), tb, 5, out int n2);
                if (n2 == 5 && tb[0] == sig[0] && tb[1] == sig[1] && tb[2] == sig[2])
                    return b;
            }
            return 0;
        }

        uint _dllBase = 0;

        /// <summary>
        /// 定点打怪 — calls DLL@0x1700 directly at its loaded address.
        /// DLL@0x1700 reads all state from game globals (no params needed).
        /// Uses PUSHAD/POPAD wrapper — safe to call from remote thread.
        /// </summary>
        public uint FindDllBasePublic() { if (_dllBase == 0) _dllBase = FindDllBase(); return _dllBase; }

        public void CallDll1700Direct()
        {
            if (_dllBase == 0) _dllBase = FindDllBase();
            if (_dllBase == 0) return;
            uint fn1700 = _dllBase + 0x1700;
            byte[] a = BitConverter.GetBytes(fn1700);
            RunShellcode(new byte[] { 0x60, 0xB8, a[0], a[1], a[2], a[3], 0xFF, 0xD0, 0x61, 0xC3 });
        }

        public void CallDll16A0Direct()
        {
            if (_dllBase == 0) _dllBase = FindDllBase();
            if (_dllBase == 0) return;
            uint fn = _dllBase + 0x16A0;
            byte[] a = BitConverter.GetBytes(fn);
            RunShellcode(new byte[] { 0x60, 0xB8, a[0], a[1], a[2], a[3], 0xFF, 0xD0, 0x61, 0xC3 });
        }

        /// <summary>
        /// Write dispatch condition 3131 (0xC3B) to trigger Cloud's 定点打怪 dispatch.
        /// ECX = [[9DD6C4]+0xC]; write 3131 to [ECX+0xC] so timer fires DLL@16A0+1700.
        /// </summary>
        public void TriggerDispatch()
        {
            // Read [[game+0x9DD6C4]+0xC]
            int g1 = MemoryHelper.ReadInt32(_hProcess, new IntPtr(0xDD9DD6C4 - 0xC0000000));  // game+0x9DD6C4
            // simplified: just write to the known dispatch addr pattern via shellcode
            // [[0xDDD6C4]→[+0xC]+0xC] = 0xC3B
            byte[] sc = {
                0x60,
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,       // MOV EAX,[0xDDD6C4]
                0x85, 0xC0, 0x74, 0x0D,               // JZ skip
                0x8B, 0x40, 0x0C,                     // MOV EAX,[EAX+0xC]
                0x85, 0xC0, 0x74, 0x07,               // JZ skip
                0xC7, 0x40, 0x0C, 0x3B, 0x0C, 0x00, 0x00,  // MOV [EAX+0xC],0xC3B
                0x61, 0xC3
            };
            RunShellcode(sc);
        }


        /// <summary>Face toward (tx,ty) via 277E53 then DLL@1700. No X key — don't switch target.</summary>
        public void FaceAndFight(float tx, float ty)
        {
            if (_dllBase == 0) _dllBase = FindDllBase();
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD != 0 && tx > 100f && ty > 100f)
            {
                float px = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x034));
                float py = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x038));
                float angle = (float)Math.Atan2(ty - py, tx - px);
                CallFace277E53Direct(angle);
                System.Threading.Thread.Sleep(50);
            }
            if (_dllBase != 0)
                CallDll1700Direct();
        }

        /// <summary>
        /// Directly write player facing direction to chain D rotation matrix.
        /// From scan data: chainD+0x010 = -dy_n, chainD+0x014 = dx_n (normalized direction).
        /// Repeated at +0x040,+0x050,+0x070,+0x080,+0x0A0,+0x0B0.
        /// </summary>
        public void SetFacingDirect(float tx, float ty)
        {
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD == 0) return;
            float px = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x034));
            float py = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x038));
            float dx = tx - px, dy = ty - py;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.01f) return;
            float dxn = dx / dist, dyn = dy / dist;
            float f010 = -dyn, f014 = dxn;
            // 4 main blocks
            foreach (uint off in new uint[] { 0x010, 0x040, 0x070, 0x0A0 })
            {
                MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + off), f010);
                MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + off + 0x04), f014);
                MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + off + 0x0C), -f014);
                MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + off + 0x10), f010);
            }
            // 0x0E0 and 0x1DC area
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + 0x0E0), f014);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + 0x0E4), f010);
            // Physics facing: +0x1E4=dxn, +0x1E8=dyn (scan confirmed)
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + 0x1E4), dxn);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)chainD + 0x1E8), dyn);
            // Sub-object writes REMOVED — they were corrupting selected target data
        }

        /// <summary>Write facing values to combat_obj+0x44/0x48 — where game reads actual facing.</summary>
        public void FaceViaCombatObj(float tx, float ty)
        {
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD == 0) return;
            int combatObj = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)chainD + 0x648));
            if (combatObj < 0x1000000) return;

            float px = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x034));
            float py = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x038));
            float dx = tx - px, dy = ty - py;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.01f) return;
            float dxn = dx / dist, dyn = dy / dist;

            // Observed pattern for +0x44: first float = -dxn, second = -dyn  (both negated)
            // Let me write common rotation matrix layouts at +0x44 and +0x50
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)combatObj + 0x44), -dxn);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)combatObj + 0x48), -dyn);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)combatObj + 0x50), -dxn);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr((uint)combatObj + 0x54), -dyn);
            // +0x60=(0,1), +0x64=(1,0) was STABLE across both scans → skip
        }

        /// <summary>Call game function 0x675784 — INSTANT TURN to target coordinates.
        /// The DLL writes X/Y to 0xDD54FC/0xDD5500 before calling, then ECX=chainD.</summary>
        public bool GameTurn(float tx, float ty)
        {
            // Write target coordinates where game reads them
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(0xDD54FC), tx);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(0xDD5500), ty);

            // Shellcode: build vec3 on stack, set ECX=chainD, EBX=&vec3, call 0x675784
            byte[] txBytes = BitConverter.GetBytes(tx);
            byte[] tyBytes = BitConverter.GetBytes(ty);

            byte[] sc = new byte[] {
                0x55,                                         // push ebp
                0x8B, 0xEC,                                   // mov ebp, esp
                0x83, 0xEC, 0x0C,                            // sub esp, 0x0C
                0xC7, 0x45, 0xF4, txBytes[0], txBytes[1], txBytes[2], txBytes[3],  // [ebp-C] = tx
                0xC7, 0x45, 0xF8, tyBytes[0], tyBytes[1], tyBytes[2], tyBytes[3],  // [ebp-8] = ty
                0xC7, 0x45, 0xFC, 0x00, 0x00, 0x00, 0x00,    // [ebp-4] = 0
                0x60,                                         // pushad
                0x8D, 0x5D, 0xF4,                            // lea ebx, [ebp-C]
                0xA1, 0xA0, 0xA8, 0xDC, 0x00,                // mov eax, [0xDCA8A0]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x09,                                   // jz +9 → popad
                0x8B, 0xC8,                                   // mov ecx, eax
                0xB8, 0x84, 0x57, 0x67, 0x00,                // mov eax, 0x675784
                0xFF, 0xD0,                                   // call eax
                0x61,                                         // popad
                0x8B, 0xE5,                                   // mov esp, ebp
                0x5D,                                         // pop ebp
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Call game function 0x6871AF — attack toward (face_X, face_Y).
        /// Matches DLL wrapper at 0x100022A0. Writes coords to face globals, then calls with
        /// push face_Y, push face_X, push state, ECX=playerObj, call 0x6871AF, [0xDD55A4]=1.</summary>
        /// <summary>Attack NPC by calling game function 0x6842CA directly with NPC pointer.</summary>
        /// <summary>Call 0x6842CA with custom first arg. arg1 can be 1, 2, -1, etc. to test different actions.</summary>
        public bool InteractNpcArg(uint npcPtr, int arg1)
        {
            if (npcPtr < 0x1000000) return false;
            uint npcOid = (uint)MemoryHelper.ReadInt32(_hProcess, new IntPtr(npcPtr + 0x644));

            byte[] oidBytes = BitConverter.GetBytes(npcOid);
            byte[] ptrBytes = BitConverter.GetBytes(npcPtr);
            byte[] a1Bytes = BitConverter.GetBytes(arg1);

            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, ptrBytes[0], ptrBytes[1], ptrBytes[2], ptrBytes[3],  // push NPC_ptr
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push OID
                0x68, a1Bytes[0], a1Bytes[1], a1Bytes[2], a1Bytes[3],      // push arg1
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                // mov eax, [0xDDD6C4]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x10,                                   // jz cleanup (+16)
                0x8B, 0x48, 0x0C,                             // mov ecx, [eax+0x0C]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x09,                                   // jz cleanup (+9)
                0xB8, 0xCA, 0x42, 0x68, 0x00,                // mov eax, 0x6842CA
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end (+3)
                0x83, 0xC4, 0x0C,                             // add esp, 0xC
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        public bool AttackNpcDirect(uint npcPtr)
        {
            if (npcPtr < 0x1000000) return false;
            uint npcOid = (uint)MemoryHelper.ReadInt32(_hProcess, new IntPtr(npcPtr + 0x644));

            byte[] oidBytes = BitConverter.GetBytes(npcOid);
            byte[] ptrBytes = BitConverter.GetBytes(npcPtr);

            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, ptrBytes[0], ptrBytes[1], ptrBytes[2], ptrBytes[3],  // push NPC_ptr (first → bottom of stack)
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push NPC_OID
                0x6A, 0x01,                                   // push 1 (last → top of stack = first arg)
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                // mov eax, [0xDDD6C4]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x10,                                   // jz cleanup (+16)
                0x8B, 0x48, 0x0C,                             // mov ecx, [eax+0x0C]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x09,                                   // jz cleanup (+9)
                0xB8, 0xCA, 0x42, 0x68, 0x00,                // mov eax, 0x6842CA
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end (+3)
                0x83, 0xC4, 0x0C,                             // add esp, 0xC
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Attack by just writing face globals + trigger flag. No function call.</summary>
        public bool SimpleAttack(float tx, float ty)
        {
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(0xDD5594), tx);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(0xDD569C), ty);
            MemoryHelper.WriteInt32(_hProcess, new IntPtr(0xDD55A4), 1);
            return true;
        }

        public bool AttackTarget(float tx, float ty)
        {
            // Write target coords to face globals (where the game func reads them)
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(0xDD5594), tx);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(0xDD569C), ty);

            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0xA1, 0xA0, 0xA8, 0xDC, 0x00,                // mov eax, [0xDCA8A0] (chainD)
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x3A,                                   // jz popad (+58)
                0x8B, 0x80, 0x48, 0x06, 0x00, 0x00,          // mov eax, [eax+0x648] (combat_obj)
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x30,                                   // jz popad (+48)
                0xFF, 0x70, 0x14,                             // push [eax+0x14] (state)
                0xFF, 0x35, 0x94, 0x55, 0xDD, 0x00,          // push [0xDD5594] (face_X)
                0xFF, 0x35, 0x9C, 0x56, 0xDD, 0x00,          // push [0xDD569C] (face_Y)
                0xA1, 0x14, 0x45, 0xDD, 0x00,                // mov eax, [0xDD4514] (playerObj)
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x15,                                   // jz cleanup (+21)
                0x8B, 0xC8,                                   // mov ecx, eax (ECX=playerObj)
                0xB8, 0xAF, 0x71, 0x68, 0x00,                // mov eax, 0x006871AF
                0xFF, 0xD0,                                   // call eax
                0xC7, 0x05, 0xA4, 0x55, 0xDD, 0x00, 0x01, 0x00, 0x00, 0x00,  // [0xDD55A4]=1
                0xEB, 0x03,                                   // jmp +3 → popad
                // cleanup: add esp, 0x0C
                0x83, 0xC4, 0x0C,                             // add esp, 0xC (remove 3 pushed args)
                // end (popad here):
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) { Console.WriteLine("  [AttackTarget] VirtualAllocEx failed"); return false; }
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                uint waitResult = WaitForSingleObject(thread, 2000);
                uint exitCode = 0;
                GetExitCodeThread(thread, out exitCode);
                Console.WriteLine($"  [AttackTarget] tx={tx:F0} ty={ty:F0} thread=0x{thread.ToInt64():X} tid={tid} wait=0x{waitResult:X} exitCode=0x{exitCode:X}");
                CloseHandle(thread);
            }
            else
            {
                Console.WriteLine("  [AttackTarget] CreateRemoteThread failed!");
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Use inventory item (全抗散) — this was the function that was mistakenly called before.</summary>
        public bool UseItem()
        {
            // push/pop around call; check null pointers
            byte[] sc = {
                0x60,                                             // pushad
                0xA1, 0xA0, 0xA8, 0xDC, 0x00,                    // mov eax, [0xDCA8A0]
                0x85, 0xC0,                                       // test eax, eax
                0x74, 0x35,                                       // jz end (+53)
                0x8B, 0x80, 0x48, 0x06, 0x00, 0x00,              // mov eax, [eax+0x648]
                0x85, 0xC0,                                       // test eax, eax
                0x74, 0x2B,                                       // jz end (+43)
                0x8B, 0x40, 0x14,                                 // mov eax, [eax+0x14]
                0x50,                                             // push state
                0xFF, 0x35, 0x94, 0x55, 0xDD, 0x00,              // push X
                0xFF, 0x35, 0x9C, 0x56, 0xDD, 0x00,              // push Y
                0x8B, 0x0D, 0x14, 0x45, 0xDD, 0x00,              // mov ecx, [0xDD4514]
                0x85, 0xC9,                                       // test ecx, ecx
                0x74, 0x11,                                       // jz end (+17)
                0xB8, 0xAF, 0x71, 0x68, 0x00,                    // mov eax, 0x006871AF
                0xFF, 0xD0,                                       // call eax
                0xC7, 0x05, 0xA4, 0x55, 0xDD, 0x00, 0x01, 0x00, 0x00, 0x00,
                // end:
                0x61,                                             // popad
                0xC3                                              // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        private bool _fightToggled = false;

        /// <summary>Call when A key is toggled off to reset F toggle state.</summary>
        public void ResetFToggle() { _fightToggled = false; }

        /// <summary>DISABLED: writing to combat_obj crashes the game.</summary>
        public void SetCombatTarget(uint npcObjAddr, float tx, float ty) { }

        /// <summary>Turn player by holding A/D keys until facing target. Reads real facing from chainD+0x10/+0x14.</summary>
        public void TurnByKeys(float tx, float ty)
        {
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD == 0) return;
            float px = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x034));
            float py = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x038));

            float dx = tx - px, dy = ty - py;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.01f) return;
            float wantX = dx / dist, wantY = dy / dist;

            ForceForeground(_gameHwnd);
            Thread.Sleep(20);

            for (int iter = 0; iter < 30; iter++)
            {
                // Rotation matrix: f010 = -dyn, f014 = dxn  →  curDxn = f014, curDyn = -f010
                float f010 = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x10));
                float f014 = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x14));
                float curDxn = f014;
                float curDyn = -f010;
                float dot = curDxn * wantX + curDyn * wantY;
                if (dot > 0.998f) return; // within ~3.5°

                float cross = curDxn * wantY - curDyn * wantX;
                byte vk = (byte)(cross > 0 ? 0x41 : 0x44); // A=left, D=right
                byte sc = (byte)MapVirtualKey(vk, 0);

                float angleDeg = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, dot))) * 180.0 / Math.PI);
                int holdMs;
                if (angleDeg > 90) holdMs = 150;
                else if (angleDeg > 30) holdMs = 80;
                else if (angleDeg > 10) holdMs = 30;
                else holdMs = 15;

                keybd_event(vk, sc, 0, UIntPtr.Zero);
                Thread.Sleep(holdMs);
                keybd_event(vk, sc, 2, UIntPtr.Zero);
                Thread.Sleep(30);
            }
        }

        /// <summary>Check auto-fight state via chainD+0x508 (0=idle, 1=fighting).</summary>
        public bool IsFightOn()
        {
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD == 0) return false;
            int flag = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)chainD + 0x508));
            return flag == 1;
        }

        /// <summary>Call 0x6842CA(-1, 0, 0) — searches/interacts with nearest NPC (loot corpse?).
        /// Replicates DLL function at 0x10002A50.
        /// ECX chain: [0xD78AE0]+0x58+0x0C</summary>
        public bool SearchCorpse()
        {
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x6A, 0x00,                                   // push 0
                0x6A, 0x00,                                   // push 0
                0x6A, 0xFF,                                   // push -1
                0xA1, 0xE0, 0x8A, 0xD7, 0x00,                // mov eax, [0xD78AE0]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x17,                                   // jz cleanup (+23)
                0x8B, 0x40, 0x58,                             // mov eax, [eax+0x58]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x10,                                   // jz cleanup (+16)
                0x8B, 0x48, 0x0C,                             // mov ecx, [eax+0x0C]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x09,                                   // jz cleanup (+9)
                0xB8, 0xCA, 0x42, 0x68, 0x00,                // mov eax, 0x6842CA
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end (+3)
                0x83, 0xC4, 0x0C,                             // add esp, 0xC (cleanup)
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint exitCode;
                GetExitCodeThread(thread, out exitCode);
                Console.WriteLine($"  [SearchCorpse] thread done exitCode=0x{exitCode:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Check if player is currently attacking a target (combat_obj+0 = NPC pointer).</summary>
        public uint GetAttackTarget()
        {
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD == 0) return 0;
            int combatObj = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)chainD + 0x648));
            if (combatObj < 0x1000000) return 0;
            int tgt = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)combatObj + 0x00));
            return tgt > 0x1000000 ? (uint)tgt : 0u;
        }

        /// <summary>Call game function 0x6859AE(4, 2, 8) — from DLL call #3/#4.
        /// ECX chain: [0xD78AE0]+0x58+0x0C+0x94. Post-flag: [0xDD557C]=1.</summary>
        public bool GameAttack()
        {
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x6A, 0x04,                                   // push 4
                0x6A, 0x02,                                   // push 2
                0x6A, 0x08,                                   // push 8
                0xA1, 0xE0, 0x8A, 0xD7, 0x00,                // mov eax, [0xD78AE0]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x2B,                                   // jz cleanup (+43)
                0x8B, 0x40, 0x58,                             // mov eax, [eax+0x58]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x24,                                   // jz (+36)
                0x8B, 0x40, 0x0C,                             // mov eax, [eax+0x0C]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x1D,                                   // jz (+29)
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,           // mov ecx, [eax+0x94]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x13,                                   // jz (+19)
                0xB8, 0xAE, 0x59, 0x68, 0x00,                 // mov eax, 0x6859AE
                0xFF, 0xD0,                                   // call eax
                // set trigger flag:
                0xC7, 0x05, 0x7C, 0x55, 0xDD, 0x00, 0x01, 0x00, 0x00, 0x00,  // mov [0xDD557C], 1
                0xEB, 0x03,                                   // jmp +3 (skip cleanup)
                // cleanup if any jz taken: add esp, 0x0C (3 args pushed)
                0x83, 0xC4, 0x0C,                             // add esp, 0x0C
                // common end:
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>OLD unused placeholder</summary>
        public bool _OldGameAttack()
        {
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x6A, 0x01,                                   // push 1
                0x6A, 0x01,                                   // push 1
                0xA1, 0x14, 0x45, 0xDD, 0x00,                // mov eax, [0xDD4514] (playerObj)
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x13,                                   // jz end (+19)
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,          // mov ecx, [eax+0x94]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x09,                                   // jz end (+9)
                0xB8, 0x3F, 0x41, 0x68, 0x00,                // mov eax, 0x68413F
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x04,                                   // jmp skip cleanup pops
                // end — clean up stack if we jumped
                0x83, 0xC4, 0x08,                            // add esp, 8 (clean pushed args)
                0x90,                                         // nop
                // common end:
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        public bool AttackNpc(uint npcObjAddr, float tx = 0f, float ty = 0f)
        {
            if (!(tx == 0f && ty == 0f))
            {
                TurnByKeys(tx, ty);
            }
            TriggerTarget();   // X — select target
            Thread.Sleep(100);
            // Only press F if auto-fight is OFF (avoid toggling it off when already attacking)
            if (!IsFightOn())
            {
                TriggerFight();
                Thread.Sleep(100);
                // Verify it turned ON; if not, press again
                if (!IsFightOn()) TriggerFight();
            }
            return true;
        }

        public void ResetFightState() { }
        public void NudgeWithKey() { }
        public void ForceIdleState()
        {
            int playerObj = MemoryHelper.ReadInt32(_hProcess, new IntPtr(0xDD4514));
            if (playerObj != 0)
                MemoryHelper.WriteInt32(_hProcess, new IntPtr((uint)playerObj + 0x128), 3);
        }

        public bool IsFacingTarget(float tx, float ty, out float angleDeg)
        {
            angleDeg = float.NaN;
            int chainD = MemoryHelper.ReadInt32(_hProcess, new IntPtr(ADDR_CHAIN_D));
            if (chainD == 0) return false;
            float px = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x034));
            float py = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x038));
            float dx = tx - px, dy = ty - py;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.01f) return true;
            float dxn = dx / dist, dyn = dy / dist;
            float expectedF010 = -dyn, expectedF014 = dxn;
            float actualF010 = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x010));
            float actualF014 = MemoryHelper.ReadFloat(_hProcess, new IntPtr((uint)chainD + 0x014));
            float dot = actualF010 * expectedF010 + actualF014 * expectedF014;
            angleDeg = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, dot))) * 180f / Math.PI);
            return dot > 0.7f; // within ~45°
        }


        public bool MoveToFight(float tx, float ty)
        {
            // Write coords to scratch globals
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(ADDR_SCRATCH_X), tx);
            MemoryHelper.WriteFloat(_hProcess, new IntPtr(ADDR_SCRATCH_Y), ty);

            // Shellcode mirrors DLL@0x1790: push Y, X, 1 → arg1=Y, arg2=X, arg3=1
            byte[] sc = {
                0x60,
                0x6A, 0x01,                                   // PUSH 1           (arg3, rightmost)
                0xA1, 0xC4, 0x55, 0xDD, 0x00, 0x50,         // MOV EAX,[scrX]; PUSH EAX  (arg2=X)
                0xA1, 0xE4, 0x55, 0xDD, 0x00, 0x50,         // MOV EAX,[scrY]; PUSH EAX  (arg1=Y, leftmost/top)
                0xA1, 0xE0, 0x8A, 0xD7, 0x00,               // MOV EAX,[D78AE0]
                0x85, 0xC0, 0x74, 0x0D,                      // JZ +13
                0x8B, 0x40, 0x58,                            // MOV EAX,[EAX+58]
                0x85, 0xC0, 0x74, 0x08,                      // JZ +8
                0x8B, 0x40, 0x0C,                            // MOV EAX,[EAX+0C]
                0x85, 0xC0, 0x74, 0x03,                      // JZ +3
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,         // MOV ECX,[EAX+94]
                0xB8, 0xAE, 0x59, 0x68, 0x00, 0xFF, 0xD0,   // CALL 2859AE (1st)
                // 2nd call
                0x6A, 0x01,
                0xA1, 0xC4, 0x55, 0xDD, 0x00, 0x50,
                0xA1, 0xE4, 0x55, 0xDD, 0x00, 0x50,
                0xA1, 0xE0, 0x8A, 0xD7, 0x00,
                0x85, 0xC0, 0x74, 0x0D,
                0x8B, 0x40, 0x58, 0x85, 0xC0, 0x74, 0x08,
                0x8B, 0x40, 0x0C, 0x85, 0xC0, 0x74, 0x03,
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,
                0xB8, 0xAE, 0x59, 0x68, 0x00, 0xFF, 0xD0,   // CALL 2859AE (2nd)
                0x61, 0xC3
            };
            RunShellcode(sc);
            return true;
        }

        /// <summary>Legacy X+F for fallback.</summary>
        public bool TriggerTarget()
        {
            if (_gameHwnd == IntPtr.Zero) return false;
            ForceForeground(_gameHwnd);
            Thread.Sleep(30);
            // keybd_event with real scancode — works with DirectInput/RawInput games
            byte sc = (byte)MapVirtualKey((uint)VK_X, 0);
            keybd_event((byte)VK_X, sc, 0, UIntPtr.Zero);          // down
            Thread.Sleep(30);
            keybd_event((byte)VK_X, sc, 2, UIntPtr.Zero);          // up (KEYEVENTF_KEYUP=2)
            return true;
        }

        public bool TriggerFight(int holdMs = 50)
        {
            if (_gameHwnd == IntPtr.Zero) return false;
            ForceForeground(_gameHwnd);
            Thread.Sleep(30);
            byte sc = (byte)MapVirtualKey((uint)VK_F, 0);
            keybd_event((byte)VK_F, sc, 0, UIntPtr.Zero);
            Thread.Sleep(holdMs);
            keybd_event((byte)VK_F, sc, 2, UIntPtr.Zero);
            Thread.Sleep(20);
            return true;
        }

        /// <summary>Test calling 0x67F644 with configurable ECX - may trigger loot packet.
        /// ecxMode: 0 = [0xDDD6C4], 1 = [0xDDD6C4]+0x0C, 2 = [0xDDD6C4]+0x04, 3 = playerObj, 4 = NPC ptr</summary>
        public bool TestSendPacket(int ecxMode, uint npcPtr = 0)
        {
            byte[] sc;
            if (ecxMode == 0)
            {
                sc = new byte[] {
                    0x60,                                     // pushad
                    0x8B, 0x0D, 0xC4, 0xD6, 0xDD, 0x00,      // mov ecx, [0xDDD6C4]
                    0x85, 0xC9,                               // test ecx, ecx
                    0x74, 0x07,                               // jz +7
                    0xB8, 0x44, 0xF6, 0x67, 0x00,            // mov eax, 0x67F644
                    0xFF, 0xD0,                               // call eax
                    0x61,                                     // popad
                    0xC3                                      // ret
                };
            }
            else if (ecxMode == 1)
            {
                sc = new byte[] {
                    0x60,
                    0xA1, 0xC4, 0xD6, 0xDD, 0x00,            // mov eax, [0xDDD6C4]
                    0x85, 0xC0,                               // test eax, eax
                    0x74, 0x0A,                               // jz +10
                    0x8B, 0x48, 0x0C,                         // mov ecx, [eax+0x0C]
                    0xB8, 0x44, 0xF6, 0x67, 0x00,            // mov eax, 0x67F644
                    0xFF, 0xD0,
                    0x61,
                    0xC3
                };
            }
            else if (ecxMode == 2)
            {
                sc = new byte[] {
                    0x60,
                    0xA1, 0xC4, 0xD6, 0xDD, 0x00,            // mov eax, [0xDDD6C4]
                    0x85, 0xC0,
                    0x74, 0x0A,
                    0x8B, 0x48, 0x04,                         // mov ecx, [eax+0x04]
                    0xB8, 0x44, 0xF6, 0x67, 0x00,
                    0xFF, 0xD0,
                    0x61,
                    0xC3
                };
            }
            else if (ecxMode == 3)
            {
                sc = new byte[] {
                    0x60,
                    0x8B, 0x0D, 0x14, 0x45, 0xDD, 0x00,      // mov ecx, [0xDD4514] (playerObj)
                    0x85, 0xC9,
                    0x74, 0x07,
                    0xB8, 0x44, 0xF6, 0x67, 0x00,
                    0xFF, 0xD0,
                    0x61,
                    0xC3
                };
            }
            else if (ecxMode == 4)
            {
                if (npcPtr < 0x1000000) return false;
                byte[] ptrBytes = BitConverter.GetBytes(npcPtr);
                sc = new byte[] {
                    0x60,
                    0xB9, ptrBytes[0], ptrBytes[1], ptrBytes[2], ptrBytes[3],  // mov ecx, npcPtr
                    0xB8, 0x44, 0xF6, 0x67, 0x00,
                    0xFF, 0xD0,
                    0x61,
                    0xC3
                };
            }
            else return false;

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out _);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1500);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Replicate XajhSmileDll.dll's search corpse wrapper (0x10002410).
        /// Logic: call 0x67E62C(corpseOid) with ECX=playerObj → get packet obj
        ///        call 0x67F644 with ECX=packet obj → send packet to server
        /// The DLL uses global 0xDD55C4 as input buffer — we inline the OID.</summary>
        public bool LootCorpse(uint corpseOid)
        {
            byte[] oidBytes = BitConverter.GetBytes(corpseOid);
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid (arg for 0x67E62C)
                0x8B, 0x0D, 0x14, 0x45, 0xDD, 0x00,          // mov ecx, [0xDD4514]  (playerObj)
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x16,                                   // jz cleanup (+22)
                0xB8, 0x2C, 0xE6, 0x67, 0x00,                // mov eax, 0x67E62C
                0xFF, 0xD0,                                   // call eax
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x0B,                                   // jz cleanup (+11)
                0x8B, 0xC8,                                   // mov ecx, eax
                0xB8, 0x44, 0xF6, 0x67, 0x00,                // mov eax, 0x67F644
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end
                0x83, 0xC4, 0x04,                             // add esp, 4 (cleanup)
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [LootCorpse] thread done exitCode=0x{ec:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Replicate XajhSmileDll.dll's 0x10002450 — likely "confirm/take-all loot".
        /// Calls 0x9A3430(arg) with ECX = [0xDDD6C4]+0x0C+0x94.
        /// Packet 0x1C3 with single arg (corpse OID).</summary>
        public bool ConfirmLoot(uint corpseOid)
        {
            byte[] oidBytes = BitConverter.GetBytes(corpseOid);
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid
                // ECX chain: [0xDDD6C4] → +0x0C → +0x94
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                // mov eax, [0xDDD6C4]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x1A,                                   // jz cleanup (+26)
                0x8B, 0x40, 0x0C,                             // mov eax, [eax+0x0C]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x13,                                   // jz cleanup (+19)
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,          // mov ecx, [eax+0x94]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x09,                                   // jz cleanup (+9)
                0xB8, 0x30, 0x34, 0x9A, 0x00,                // mov eax, 0x9A3430
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end
                0x83, 0xC4, 0x04,                             // add esp, 4
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [ConfirmLoot] thread done exitCode=0x{ec:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Replicate XajhSmileDll.dll's 0x100024A0 — another corpse-related action.
        /// Calls 0x679B5E(oid, 1, count, count) with ECX = [0xDDD6C4]+0x0C+0x94.
        /// Try this if 0x10002450 doesn't close the loot window.</summary>
        public bool CloseLootWindow(uint corpseOid)
        {
            byte[] oidBytes = BitConverter.GetBytes(corpseOid);
            // XajhSmileDll 0x100024A0: pushes [0xDD5654], 1, [0xDD5654] (NOT [0xDD5664] — see below), [0xDD5664]
            // Actually re-reading: pushes [ebp-4]=[0xDD5654], 1, EAX, [ebp-8]=[0xDD5664]
            // where EAX was [0xDD5664] (set just before pushad and still in eax after pushad pop?)
            // Simpler: assume both globals are same value = corpseOid, so push oid,1,oid,oid
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid  (arg4)
                0x6A, 0x01,                                   // push 1                       (arg3)
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid  (arg2)
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid  (arg1)
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                // mov eax, [0xDDD6C4]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x1A,                                   // jz cleanup
                0x8B, 0x40, 0x0C,                             // mov eax, [eax+0x0C]
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x13,                                   // jz cleanup
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,          // mov ecx, [eax+0x94]
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x09,                                   // jz cleanup
                0xB8, 0x5E, 0x9B, 0x67, 0x00,                // mov eax, 0x679B5E
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end
                0x83, 0xC4, 0x10,                             // add esp, 0x10 (4 args cleanup)
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [CloseLootWindow] thread done exitCode=0x{ec:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>CSvClient::GetAllFromCorpse — take all items from corpse.
        /// Writes corpseOid to [0xDD5614], then calls 0x9A3430 with ECX = [0xDDD6C4]+0x0C+0x94.
        /// Packet: field 0x1C3 (size 0x20) + field 0x14 = corpseOid.</summary>
        public bool TakeAllLoot(uint corpseOid)
        {
            byte[] oidBytes = BitConverter.GetBytes(corpseOid);
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                // Write corpseOid to [0xDD5614]
                0xC7, 0x05, 0x14, 0x56, 0xDD, 0x00, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // mov [0xDD5614], corpseOid
                // Now call CSvClient::GetAllFromCorpse
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid (arg1)
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                // mov eax, [0xDDD6C4]
                0x85, 0xC0, 0x74, 0x1A,                      // jz cleanup
                0x8B, 0x40, 0x0C,                             // mov eax, [eax+0x0C]
                0x85, 0xC0, 0x74, 0x13,                      // jz cleanup
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,          // mov ecx, [eax+0x94]
                0x85, 0xC9, 0x74, 0x09,                      // jz cleanup
                0xB8, 0x30, 0x34, 0x9A, 0x00,                // mov eax, 0x9A3430
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end
                0x83, 0xC4, 0x04,                             // add esp, 4 (cleanup)
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [TakeAllLoot] thread done exitCode=0x{ec:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>CSvClient::ReleaseCorpse (0x9A3590) — closes the corpse loot window.
        /// Packet: 0x153 with field 0x14 = corpseOid.</summary>
        public bool ReleaseCorpse(uint corpseOid)
        {
            byte[] oidBytes = BitConverter.GetBytes(corpseOid);
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, oidBytes[0], oidBytes[1], oidBytes[2], oidBytes[3],  // push corpseOid
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                // mov eax, [0xDDD6C4]
                0x85, 0xC0, 0x74, 0x1A,                      // jz cleanup
                0x8B, 0x40, 0x0C,                             // mov eax, [eax+0x0C]
                0x85, 0xC0, 0x74, 0x13,                      // jz cleanup
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,          // mov ecx, [eax+0x94]
                0x85, 0xC9, 0x74, 0x09,                      // jz cleanup
                0xB8, 0x90, 0x35, 0x9A, 0x00,                // mov eax, 0x9A3590
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end
                0x83, 0xC4, 0x04,                             // add esp, 4 (cleanup)
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [ReleaseCorpse] thread done exitCode=0x{ec:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>Click the "蓝色按钮" to take all items + close loot window.
        /// Uses real hardware mouse event (moves the cursor, then clicks).
        /// Client coords default to (62, 277) — loot window is anchored top-left of game client.</summary>
        public bool ClickLootConfirmButton(int clientX = 62, int clientY = 277)
        {
            if (_gameHwnd == IntPtr.Zero) { Console.WriteLine("  [ClickLootConfirmButton] no game window"); return false; }

            // Convert client coords to screen coords
            POINT pt = new POINT { X = clientX, Y = clientY };
            if (!ClientToScreen(_gameHwnd, ref pt))
            {
                Console.WriteLine("  [ClickLootConfirmButton] ClientToScreen failed");
                return false;
            }

            // Save original cursor pos to restore later
            POINT origPt;
            GetCursorPos(out origPt);

            // Move cursor, click, restore
            SetCursorPos(pt.X, pt.Y);
            Thread.Sleep(80);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(60);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(80);
            SetCursorPos(origPt.X, origPt.Y);

            Console.WriteLine($"  [ClickLootConfirmButton] clicked at screen ({pt.X},{pt.Y}) [client ({clientX},{clientY})], restored to ({origPt.X},{origPt.Y})");
            return true;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        /// <summary>Sets the ESC key flag [0xDDD6C8] = 1, which simulates ESC press.
        /// Found in the key handler at game+0x9EA9C9: cmp eax, 0x1B; jnz; mov [0xDDD6C8], 1.
        /// This may close the loot window (same as manual ESC).</summary>
        public bool SetEscapeFlag()
        {
            // Simple direct write
            byte[] val = { 0x01, 0x00, 0x00, 0x00 };
            int written;
            bool ok = MemoryHelper.WriteProcessMemory(_hProcess, new IntPtr(0xDDD6C8), val, 4, out written);
            Console.WriteLine($"  [SetEscapeFlag] wrote [0xDDD6C8]=1, ok={ok}");
            return ok;
        }

        /// <summary>Call CGwPuCorpse::Visible(show=false) on a given instance.
        /// Instance must be found by scanning for vtable=0xBB248C in memory.
        /// Method 0x912950 is at sub-vtable +8, slot 9. Takes (this+8, bShow, arg2=0).</summary>
        public bool HideLootWindow(uint instancePtr)
        {
            // Shellcode: ECX = instance+8, push 0, push 0, call 0x912950
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x6A, 0x00,                                   // push 0 (arg2)
                0x6A, 0x00,                                   // push 0 (bShow=false)
                0xB9, 0x00, 0x00, 0x00, 0x00,                // mov ecx, instancePtr+8  (patched below)
                0xB8, 0x50, 0x29, 0x91, 0x00,                // mov eax, 0x912950
                0xFF, 0xD0,                                   // call eax
                0x61,                                         // popad
                0xC3                                          // ret
            };
            // Patch ECX with instance+8
            uint thisArg = instancePtr + 8;
            byte[] thisBytes = BitConverter.GetBytes(thisArg);
            sc[6] = thisBytes[0];
            sc[7] = thisBytes[1];
            sc[8] = thisBytes[2];
            sc[9] = thisBytes[3];

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [HideLootWindow] called Visible(false) on 0x{instancePtr:X}, exitCode=0x{ec:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Generic hide: call Visible(false) on any GUi-derived object.
        /// ECX = objPtr, pushes (0, 0), calls vtable[9] = method at offset 0x24 in vtable.
        /// Works for GUiObject, GUiWidget, GUiTouch, CGWTipTouch, etc.</summary>
        public bool HideGuiObject(uint objPtr)
        {
            byte[] ptrBytes = BitConverter.GetBytes(objPtr);
            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x6A, 0x00,                                   // push 0 (arg2)
                0x6A, 0x00,                                   // push 0 (bShow=false)
                0xB9, ptrBytes[0], ptrBytes[1], ptrBytes[2], ptrBytes[3],  // mov ecx, objPtr
                0x85, 0xC9,                                   // test ecx, ecx
                0x74, 0x0D,                                   // jz cleanup
                0x8B, 0x01,                                   // mov eax, [ecx] (vtable)
                0x85, 0xC0,                                   // test eax, eax
                0x74, 0x07,                                   // jz cleanup
                0x8B, 0x40, 0x24,                             // mov eax, [eax+0x24] (vtable[9] = Visible)
                0xFF, 0xD0,                                   // call eax
                0xEB, 0x03,                                   // jmp end
                0x83, 0xC4, 0x08,                             // add esp, 8 (cleanup)
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 1000);
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Press skill key (1-8). Unlike F, skill keys are one-shot not toggle.</summary>
        public bool TriggerSkill(int skillNum = 1)
        {
            if (_gameHwnd == IntPtr.Zero) return false;
            ushort vk = (ushort)(0x30 + skillNum); // '1'=0x31, '2'=0x32, etc.
            ForceForeground(_gameHwnd);
            Thread.Sleep(15);
            var inp = new SINPUT[2];
            inp[0].type = 1; inp[0].u.ki.vk = vk; inp[0].u.ki.flags = 0;
            inp[1].type = 1; inp[1].u.ki.vk = vk; inp[1].u.ki.flags = 2;
            SendInput(2, inp, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SINPUT)));
            return true;
        }

        public bool TriggerTargetAndFight()
        {
            TriggerTarget(); Thread.Sleep(20); return TriggerFight();
        }

        private void RunShellcode(byte[] sc)
        {
            IntPtr stub = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length,
                                         MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (stub == IntPtr.Zero) return;
            MemoryHelper.WriteProcessMemory(_hProcess, stub, sc, sc.Length, out _);

            // Try main thread injection first (game context), fall back to remote thread
            if (_mainThreadId != 0 && RunInMainThread(stub))
            {
                VirtualFreeEx(_hProcess, stub, 0, MEM_RELEASE);
                return;
            }

            IntPtr hThread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0,
                                                stub, IntPtr.Zero, 0, out _);
            if (hThread != IntPtr.Zero)
            {
                WaitForSingleObject(hThread, 80);
                CloseHandle(hThread);
            }
            VirtualFreeEx(_hProcess, stub, 0, MEM_RELEASE);
        }

        /// <summary>
        /// Inject shellcode into the game's main thread via SuspendThread+SetThreadContext.
        /// The shellcode must end with RET. We build a tiny trampoline that:
        ///   1. Calls our shellcode
        ///   2. JMPs back to original EIP
        /// </summary>
        private bool RunInMainThread(IntPtr scAddr)
        {
            if (_mainThreadId == 0) return false;
            const uint THREAD_ALL_ACCESS = 0x1F03FF;
            IntPtr hThread = OpenThread(THREAD_ALL_ACCESS, false, _mainThreadId);
            if (hThread == IntPtr.Zero) return false;

            try
            {
                SuspendThread(hThread);
                var ctx = new CONTEXT { ContextFlags = 0x10007 };
                ctx.Dr = new uint[6];
                ctx.FloatSave = new byte[112];
                ctx.ExtendedRegisters = new byte[512];
                if (!GetThreadContext(hThread, ref ctx)) { ResumeThread(hThread); return false; }

                uint origEip = ctx.Eip;

                // Build trampoline: PUSHAD; CALL scAddr-relative; POPAD; JMP origEip
                // PUSHAD=60, CALL rel32=E8 xx xx xx xx, POPAD=61, JMP rel32=E9 xx xx xx xx
                IntPtr tramp = VirtualAllocEx(_hProcess, IntPtr.Zero, 15, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
                if (tramp == IntPtr.Zero) { ResumeThread(hThread); return false; }

                uint trampAddr = (uint)tramp.ToInt64();
                int callRel = (int)((uint)scAddr.ToInt64() - (trampAddr + 6));
                int jmpRel = (int)(origEip - (trampAddr + 12));

                byte[] tr = {
                    0x60,                                    // PUSHAD
                    0xE8,                                    // CALL
                    (byte)(callRel), (byte)(callRel>>8), (byte)(callRel>>16), (byte)(callRel>>24),
                    0x83, 0xC4, 0x04,                        // ADD ESP,4 (fix stack after CALL which pushes ret addr... actually no)
                    0x61,                                    // POPAD  
                    0xE9,                                    // JMP
                    (byte)(jmpRel), (byte)(jmpRel>>8), (byte)(jmpRel>>16), (byte)(jmpRel>>24),
                };
                // Fix: CALL pushes return addr on stack, so ESP would be off. Use CALL+RET pattern instead.
                // Better: just use inline code with no CALL
                // Trampoline: PUSHAD; [inline sc copy]; POPAD; JMP origEip
                // But sc can be large. Alternative: set EIP to sc, sc ends with JMP back.
                // Simplest: patch first bytes of sc to end with JMP back, then redirect EIP.

                // Actually simplest: just redirect EIP to sc, and append JMP back to sc end
                // Read sc bytes, append JMP to origEip
                int scLen = 0;
                byte[] scBytes = new byte[256];
                MemoryHelper.ReadProcessMemory(_hProcess, scAddr, scBytes, 256, out scLen);
                // Find RET (0xC3) at end
                int retOff = -1;
                for (int i = scLen - 1; i >= 0; i--) { if (scBytes[i] == 0xC3) { retOff = i; break; } }
                if (retOff < 0) { VirtualFreeEx(_hProcess, tramp, 0, MEM_RELEASE); ResumeThread(hThread); return false; }

                // Replace RET with JMP origEip
                uint scU = (uint)scAddr.ToInt64();
                int jmpRel2 = (int)(origEip - (scU + retOff + 5));
                byte[] patch = { 0xE9, (byte)jmpRel2, (byte)(jmpRel2 >> 8), (byte)(jmpRel2 >> 16), (byte)(jmpRel2 >> 24) };
                MemoryHelper.WriteProcessMemory(_hProcess, IntPtr.Add(scAddr, retOff), patch, 5, out _);

                ctx.Eip = scU;
                SetThreadContext(hThread, ref ctx);
                ResumeThread(hThread);
                Thread.Sleep(100); // wait for execution
                VirtualFreeEx(_hProcess, tramp, 0, MEM_RELEASE);
                return true;
            }
            finally { CloseHandle(hThread); }
        }
    }
}
