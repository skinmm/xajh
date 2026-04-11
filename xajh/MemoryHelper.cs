using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace xajh
{
    public static class MemoryHelper
    {
        // ── Win32 imports ──────────────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
           uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
            uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter,
            uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        // ── Access flags ───────────────────────────────────────────────────────
        public const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        public const uint MEM_COMMIT = 0x1000;
        public const uint PAGE_READWRITE = 0x04;
        public const uint PAGE_WRITECOPY = 0x08;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;
        public const uint PAGE_EXECUTE_WRITECOPY = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        // ── Generic read ───────────────────────────────────────────────────────
        public static T Read<T>(IntPtr hProcess, IntPtr address) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buf = new byte[size];
            ReadProcessMemory(hProcess, address, buf, size, out _);
            GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            T result = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            handle.Free();
            return result;
        }

        public static int ReadInt32(IntPtr hProcess, IntPtr addr) => Read<int>(hProcess, addr);
        public static float ReadFloat(IntPtr hProcess, IntPtr addr) => Read<float>(hProcess, addr);
        public static long ReadInt64(IntPtr hProcess, IntPtr addr) => Read<long>(hProcess, addr);
        public static short ReadInt16(IntPtr hProcess, IntPtr addr) => Read<short>(hProcess, addr);
        public static bool WriteInt16(IntPtr hProcess, IntPtr addr, short val) => Write(hProcess, addr, val);

        // ── Generic write ──────────────────────────────────────────────────────
        public static bool Write<T>(IntPtr hProcess, IntPtr address, T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buf = new byte[size];
            GCHandle handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            handle.Free();
            return WriteProcessMemory(hProcess, address, buf, size, out _);
        }

        public static bool WriteInt32(IntPtr hProcess, IntPtr addr, int val) => Write(hProcess, addr, val);
        public static bool WriteFloat(IntPtr hProcess, IntPtr addr, float val) => Write(hProcess, addr, val);

        // ── Pointer chain resolution ───────────────────────────────────────────
        /// <summary>Resolves a base address + offset chain (multi-level pointer).</summary>
        public static IntPtr ResolvePointer(IntPtr hProcess, IntPtr baseAddr, int[] offsets)
        {
            IntPtr current = baseAddr;
            foreach (int offset in offsets)
            {
                current = (IntPtr)ReadInt32(hProcess, current); // 64-bit pointer
                if (current == IntPtr.Zero) return IntPtr.Zero;
                current = IntPtr.Add(current, offset);
            }
            return current;
        }

        // ── AOB (Array-of-Bytes) scanner ───────────────────────────────────────
        /// <summary>
        /// Scans all readable memory regions for a byte pattern.
        /// Use '??' as wildcard in pattern string, e.g. "A1 ?? ?? 00 B9"
        /// </summary>
        public static List<IntPtr> AobScan(IntPtr hProcess, string pattern, IProgress<string> progress = null)
        {
            var results = new List<IntPtr>();
            byte?[] pat = ParsePattern(pattern);

            IntPtr address = IntPtr.Zero;
            int scannedMB = 0;

            while (true)
            {
                if (!VirtualQueryEx(hProcess, address, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
                    break;

                bool readable = mbi.State == MEM_COMMIT &&
                                (mbi.Protect == PAGE_READWRITE ||
                                 mbi.Protect == PAGE_WRITECOPY ||
                                 mbi.Protect == PAGE_EXECUTE_READWRITE ||
                                 mbi.Protect == PAGE_EXECUTE_WRITECOPY);

                if (readable)
                {
                    long regionSize = mbi.RegionSize.ToInt64();
                    byte[] buf = new byte[regionSize];
                    ReadProcessMemory(hProcess, mbi.BaseAddress, buf, (int)regionSize, out int bytesRead);

                    for (int i = 0; i <= bytesRead - pat.Length; i++)
                    {
                        if (MatchPattern(buf, i, pat))
                            results.Add(IntPtr.Add(mbi.BaseAddress, i));
                    }

                    scannedMB += (int)(regionSize / 1048576);
                    progress?.Report($"Scanned ~{scannedMB} MB | Found: {results.Count}");
                }

                // Advance to next region
                long next = address.ToInt64() + mbi.RegionSize.ToInt64();
                if (next < 0 || next >= long.MaxValue) break;
                address = new IntPtr(next);
            }

            return results;
        }

        private static byte?[] ParsePattern(string pattern)
        {
            string[] tokens = pattern.Trim().Split(' ');
            var result = new byte?[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                result[i] = tokens[i] == "??" ? (byte?)null : Convert.ToByte(tokens[i], 16);
            return result;
        }

        private static bool MatchPattern(byte[] data, int offset, byte?[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i].HasValue && data[offset + i] != pattern[i].Value)
                    return false;
            }
            return true;
        }

        // ── Value scanner (find by value) ──────────────────────────────────────
        /// <summary>Scan all writable memory for a specific integer value (e.g., HP = 100).</summary>
        public static List<IntPtr> ScanForValue(IntPtr hProcess, int value)
        {
            var results = new List<IntPtr>();
            byte[] target = BitConverter.GetBytes(value);
            IntPtr address = IntPtr.Zero;

            while (true)
            {
                if (!VirtualQueryEx(hProcess, address, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
                    break;

                bool writable = mbi.State == MEM_COMMIT &&
                                (mbi.Protect == PAGE_READWRITE ||
                                 mbi.Protect == PAGE_EXECUTE_READWRITE);

                if (writable)
                {
                    long regionSize = mbi.RegionSize.ToInt64();
                    byte[] buf = new byte[regionSize];
                    ReadProcessMemory(hProcess, mbi.BaseAddress, buf, (int)regionSize, out int bytesRead);

                    for (int i = 0; i <= bytesRead - 4; i++)
                    {
                        if (buf[i] == target[0] && buf[i + 1] == target[1] &&
                            buf[i + 2] == target[2] && buf[i + 3] == target[3])
                        {
                            results.Add(IntPtr.Add(mbi.BaseAddress, i));
                        }
                    }
                }

                long next = address.ToInt64() + mbi.RegionSize.ToInt64();
                if (next <= 0 || next >= long.MaxValue) break;
                address = new IntPtr(next);
            }

            return results;
        }

        /// <summary>Narrow down previous scan results to those still holding the target value.</summary>
        public static List<IntPtr> FilterByValue(IntPtr hProcess, List<IntPtr> candidates, int value)
        {
            var results = new List<IntPtr>();
            foreach (var addr in candidates)
            {
                if (ReadInt32(hProcess, addr) == value)
                    results.Add(addr);
            }
            return results;
        }

        /// <summary>Scan all writable memory for a float value with tolerance.</summary>
        public static List<IntPtr> ScanForFloat(IntPtr hProcess, float value, float tolerance = 0.01f)
        {
            var results = new List<IntPtr>();
            IntPtr address = IntPtr.Zero;

            while (true)
            {
                if (!VirtualQueryEx(hProcess, address, out var mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
                    break;

                bool writable = mbi.State == MEM_COMMIT &&
                                (mbi.Protect == PAGE_READWRITE ||
                                 mbi.Protect == PAGE_EXECUTE_READWRITE);

                if (writable)
                {
                    long regionSize = mbi.RegionSize.ToInt64();
                    byte[] buf = new byte[regionSize];
                    ReadProcessMemory(hProcess, mbi.BaseAddress, buf, (int)regionSize, out int bytesRead);

                    for (int i = 0; i <= bytesRead - 4; i += 4)
                    {
                        float fv = BitConverter.ToSingle(buf, i);
                        if (Math.Abs(fv - value) <= tolerance)
                            results.Add(IntPtr.Add(mbi.BaseAddress, i));
                    }
                }

                long next = address.ToInt64() + mbi.RegionSize.ToInt64();
                if (next <= 0 || next >= long.MaxValue) break;
                address = new IntPtr(next);
            }

            return results;
        }

        /// <summary>Filter candidates to those still matching a float value.</summary>
        public static List<IntPtr> FilterByFloat(IntPtr hProcess, List<IntPtr> candidates, float value, float tolerance = 0.01f)
        {
            var results = new List<IntPtr>();
            foreach (var addr in candidates)
            {
                float val = ReadFloat(hProcess, addr);
                if (Math.Abs(val - value) <= tolerance)
                    results.Add(addr);
            }
            return results;
        }
    }
}
