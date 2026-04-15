using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Sends /loc to the game chat and reads the response from memory
    /// to obtain the authoritative player position.
    /// </summary>
    class LocCommand
    {
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const uint WM_CHAR = 0x0102;
        const int VK_RETURN = 0x0D;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        readonly IntPtr _hProcess;
        readonly IntPtr _moduleBase;
        readonly Func<IntPtr> _getHwnd;

        // Cached scan results for the chat log buffer location.
        long _chatBufAddr = 0;

        // Last known /loc result.
        float _locX = float.NaN, _locY = float.NaN, _locZ = float.NaN;
        long _locTimestamp = 0;
        bool _hasLoc = false;

        // Throttle: don't spam /loc faster than this interval.
        const int LocCooldownMs = 3000;
        long _nextLocAllowedTicks = 0;

        public LocCommand(IntPtr hProcess, IntPtr moduleBase, Func<IntPtr> getHwnd)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
            _getHwnd = getHwnd;
        }

        public bool HasLoc => _hasLoc;
        public float LocX => _locX;
        public float LocY => _locY;
        public float LocZ => _locZ;
        public long LocAge => _hasLoc ? Environment.TickCount64 - _locTimestamp : long.MaxValue;

        /// <summary>
        /// Force-send /loc immediately, bypassing the cooldown.
        /// Use only when all other position sources have failed.
        /// </summary>
        public bool ForceRead()
        {
            _nextLocAllowedTicks = 0;
            return SendAndRead();
        }

        /// <summary>
        /// Send /loc to the game and try to read the response.
        /// Returns true if a valid position was parsed.
        /// </summary>
        public bool SendAndRead()
        {
            long now = Environment.TickCount64;
            if (now < _nextLocAllowedTicks)
                return _hasLoc;
            _nextLocAllowedTicks = now + LocCooldownMs;

            IntPtr hwnd = _getHwnd();
            if (hwnd == IntPtr.Zero) return false;

            // Take a "before" snapshot of writable memory to diff after.
            var beforeStrings = ScanForCoordStrings();

            SetForegroundWindow(hwnd);
            Thread.Sleep(80);

            // Open chat (Enter), type /loc, send (Enter).
            SendKey(hwnd, VK_RETURN);
            Thread.Sleep(150);
            TypeString(hwnd, "/loc");
            Thread.Sleep(100);
            SendKey(hwnd, VK_RETURN);
            Thread.Sleep(500);

            // Read response from memory.
            var afterStrings = ScanForCoordStrings();

            // Find new coordinate strings that appeared after sending /loc.
            var beforeTexts = new HashSet<string>();
            foreach (var b in beforeStrings)
                beforeTexts.Add(b.text);
            foreach (var s in afterStrings)
            {
                if (!beforeTexts.Contains(s.text))
                {
                    if (TryParseLocResponse(s.text, out float x, out float y, out float z))
                    {
                        _locX = x; _locY = y; _locZ = z;
                        _locTimestamp = Environment.TickCount64;
                        _hasLoc = true;
                        _chatBufAddr = s.addr;
                        Console.WriteLine($"  [LOC] Parsed: ({x:F1}, {y:F1}, {z:F1}) from \"{s.text}\"");
                        return true;
                    }
                }
            }

            // Fallback: try reading from the previously known chat buffer location.
            if (_chatBufAddr != 0)
            {
                string cached = ReadStringAt(_chatBufAddr, 512);
                if (!string.IsNullOrEmpty(cached) && TryParseLocResponse(cached, out float cx, out float cy, out float cz))
                {
                    _locX = cx; _locY = cy; _locZ = cz;
                    _locTimestamp = Environment.TickCount64;
                    _hasLoc = true;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Try to read from a previously found /loc buffer without re-sending.
        /// Useful for periodic refresh.
        /// </summary>
        public bool TryRefreshFromMemory()
        {
            if (_chatBufAddr == 0) return false;
            string text = ReadStringAt(_chatBufAddr, 512);
            if (string.IsNullOrEmpty(text)) return false;
            if (TryParseLocResponse(text, out float x, out float y, out float z))
            {
                if (_hasLoc && Math.Abs(x - _locX) < 0.01f && Math.Abs(y - _locY) < 0.01f)
                    return true;
                _locX = x; _locY = y; _locZ = z;
                _locTimestamp = Environment.TickCount64;
                _hasLoc = true;
                return true;
            }
            return false;
        }

        static bool TryParseLocResponse(string text, out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;

            // Common /loc response formats:
            //   "X:1234.5 Y:5678.9 Z:42.0"
            //   "坐标: 1234.5, 5678.9, 42.0"
            //   "loc: (1234.5, 5678.9, 42.0)"
            //   "1234.5 5678.9 42.0"
            //   "(1234.5,5678.9,42.0)"

            // Pattern: X:float Y:float Z:float (or x: y: z:)
            var m = Regex.Match(text, @"[Xx]\s*[:=]\s*(-?[\d.]+)\s+[Yy]\s*[:=]\s*(-?[\d.]+)\s+[Zz]\s*[:=]\s*(-?[\d.]+)");
            if (m.Success &&
                float.TryParse(m.Groups[1].Value, out x) &&
                float.TryParse(m.Groups[2].Value, out y) &&
                float.TryParse(m.Groups[3].Value, out z))
                return IsPlausible(x, y, z);

            // Pattern: three comma/space-separated floats, optionally in parens
            m = Regex.Match(text, @"(-?[\d.]+)\s*[,\s]\s*(-?[\d.]+)\s*[,\s]\s*(-?[\d.]+)");
            if (m.Success &&
                float.TryParse(m.Groups[1].Value, out x) &&
                float.TryParse(m.Groups[2].Value, out y) &&
                float.TryParse(m.Groups[3].Value, out z))
                return IsPlausible(x, y, z);

            return false;
        }

        static bool IsPlausible(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f || Math.Abs(z) > 1_000_000f) return false;
            if (Math.Abs(x) < 1f && Math.Abs(y) < 1f) return false;
            return true;
        }

        HashSet<(long addr, string text)> ScanForCoordStrings()
        {
            var results = new HashSet<(long addr, string text)>();
            IntPtr cursor = IntPtr.Zero;

            while (true)
            {
                if (!MemoryHelper.VirtualQueryEx(
                    _hProcess, cursor, out var mbi,
                    (uint)Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>()))
                    break;

                long regionBase = mbi.BaseAddress.ToInt64();
                long regionSize = mbi.RegionSize.ToInt64();
                bool writable = mbi.State == MemoryHelper.MEM_COMMIT &&
                    (mbi.Protect == MemoryHelper.PAGE_READWRITE ||
                     mbi.Protect == MemoryHelper.PAGE_WRITECOPY ||
                     mbi.Protect == MemoryHelper.PAGE_EXECUTE_READWRITE ||
                     mbi.Protect == MemoryHelper.PAGE_EXECUTE_WRITECOPY);

                if (writable && regionSize > 0 && regionSize <= 64 * 1024 * 1024)
                {
                    ScanRegionForCoords(regionBase, (int)regionSize, results);
                }

                long next = regionBase + regionSize;
                if (next <= cursor.ToInt64() || next >= long.MaxValue) break;
                cursor = new IntPtr(next);
            }

            return results;
        }

        void ScanRegionForCoords(long regionBase, int regionSize, HashSet<(long addr, string text)> results)
        {
            const int ChunkSize = 0x10000;
            for (long off = 0; off < regionSize; off += ChunkSize)
            {
                int toRead = (int)Math.Min(ChunkSize, regionSize - off);
                if (toRead < 32) break;
                var buf = new byte[toRead];
                if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(regionBase + off), buf, toRead, out int read) || read < 32)
                    continue;

                // Scan for ASCII/GBK strings containing coordinate-like patterns.
                for (int i = 0; i < read - 20; i++)
                {
                    // Quick filter: look for 'l','o','c' or 'X',':' or digit sequences
                    byte b = buf[i];
                    if (b != (byte)'l' && b != (byte)'L' && b != (byte)'X' && b != (byte)'x' &&
                        b != (byte)'(' && b != 0xD7 /* 坐 GBK lead */ &&
                        !(b >= (byte)'0' && b <= (byte)'9') && b != (byte)'-')
                        continue;

                    // Try to extract a string starting here.
                    int strEnd = i;
                    while (strEnd < read && strEnd - i < 256 && buf[strEnd] >= 0x20)
                        strEnd++;
                    int strLen = strEnd - i;
                    if (strLen < 10) continue;

                    string text;
                    try
                    {
                        text = Encoding.GetEncoding("GBK").GetString(buf, i, strLen);
                    }
                    catch { continue; }

                    if (TryParseLocResponse(text, out _, out _, out _))
                        results.Add((regionBase + off + i, text));
                }
            }
        }

        string ReadStringAt(long addr, int maxLen)
        {
            var buf = new byte[maxLen];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(addr), buf, maxLen, out int read) || read < 8)
                return null;
            int end = 0;
            while (end < read && buf[end] >= 0x20)
                end++;
            if (end < 8) return null;
            try
            {
                return Encoding.GetEncoding("GBK").GetString(buf, 0, end);
            }
            catch { return null; }
        }

        void TypeString(IntPtr hwnd, string text)
        {
            foreach (char c in text)
            {
                PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(30);
            }
        }

        void SendKey(IntPtr hwnd, int vk)
        {
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            Thread.Sleep(30);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        }
    }
}
