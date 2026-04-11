using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Turns the player by synthesizing right-click-drag mouse input.
    ///
    /// Why mouse instead of memory writes: the game's authoritative rotation
    /// state lives behind the input handler. Writing the player display
    /// matrix only changes what YOU see — other players (server view) see
    /// nothing. Sending real mouse input goes through the game's normal turn
    /// path → input handler → network → everyone sees the rotation.
    ///
    /// Auto-calibrates pixels-per-radian on first use by:
    ///   1. Reading current yaw from the player matrix
    ///   2. Sending a known mouse-X delta during a right-click-hold
    ///   3. Reading new yaw, computing the ratio
    /// </summary>
    public class TurnHelper
    {
        // ── Win32 ─────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public int type; public MOUSEINPUT mi; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
            public int padA, padB;
        }

        const int INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        // Window messages
        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const int MK_RBUTTON = 0x0002;
        const int VK_W = 0x57;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        // ── State ─────────────────────────────────────────────────────────────
        private readonly IntPtr _hProcess;
        private readonly IntPtr _gameHwnd;
        private float _pixelsPerRadian = 0f;     // 0 = uncalibrated

        public TurnHelper(IntPtr hProcess, IntPtr gameHwnd)
        {
            _hProcess = hProcess;
            _gameHwnd = gameHwnd;
        }

        /// <summary>
        /// Read current player yaw from the display matrix at playerObj+0x10/+0x1C.
        /// </summary>
        public float ReadPlayerYaw(int playerObj)
        {
            if (playerObj == 0) return float.NaN;
            var pObj = new IntPtr((uint)playerObj);
            float c = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(pObj, 0x10));
            float s = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(pObj, 0x1C));
            return (float)Math.Atan2(s, c);
        }

        /// <summary>
        /// Turn the player so it faces (tx, tz) from (px, pz).
        /// Auto-calibrates on first use, then closed-loop corrects until
        /// the error is below ~3°.
        /// </summary>
        /// <summary>
        /// Turn to face (tx, ty) from (px, py). Current yaw is measured by
        /// tapping forward and observing the position delta — the display
        /// matrix yaw is in a different coordinate frame than world X/Y.
        /// Needs a delegate to read player position.
        /// </summary>
        public string FaceTarget(Func<(float x, float y, float z)> readPos,
                                 float tx, float ty)
        {
            float curYaw = MeasureYaw(readPos);
            if (float.IsNaN(curYaw))
                return "[!] Auto-run produced no motion. Key blocked or path obstructed?";

            var (px, py, _) = readPos();
            float dx = tx - px;
            float dy = ty - py;
            float targetYaw = (float)Math.Atan2(dx, dy);

            float delta = targetYaw - curYaw;
            while (delta > Math.PI) delta -= 2f * (float)Math.PI;
            while (delta < -Math.PI) delta += 2f * (float)Math.PI;

            if (_pixelsPerRadian == 0f)
            {
                const int TestPx = 200;
                DragTurn(TestPx);
                Thread.Sleep(150);
                float newYaw = MeasureYaw(readPos);
                if (float.IsNaN(newYaw))
                    return "[!] Cal: second measurement failed";
                float yawChange = newYaw - curYaw;
                while (yawChange > Math.PI) yawChange -= 2f * (float)Math.PI;
                while (yawChange < -Math.PI) yawChange += 2f * (float)Math.PI;
                if (Math.Abs(yawChange) < 0.05f)
                    return $"[!] Cal failed: drag={TestPx}px gave yawΔ={yawChange:F2}";
                _pixelsPerRadian = TestPx / yawChange;
                curYaw = newYaw;
                var (cx, cy, _) = readPos();
                dx = tx - cx; dy = ty - cy;
                targetYaw = (float)Math.Atan2(dx, dy);
                delta = targetYaw - curYaw;
                while (delta > Math.PI) delta -= 2f * (float)Math.PI;
                while (delta < -Math.PI) delta += 2f * (float)Math.PI;
            }

            int pixels = (int)Math.Round(delta * _pixelsPerRadian);
            DragTurn(pixels);
            return $"cur={curYaw:F2} tgt={targetYaw:F2} Δ={delta:F2} ({pixels}px) cal={_pixelsPerRadian:F0}";
        }

        const int VK_OEM_3 = 0xC0;   // backtick `

        /// <summary>Toggle auto-run by pressing the backtick key.</summary>
        private void ToggleAutoRun()
        {
            if (_gameHwnd == IntPtr.Zero) return;
            PostMessage(_gameHwnd, WM_KEYDOWN, (IntPtr)VK_OEM_3, (IntPtr)0x00290001);
            Thread.Sleep(30);
            PostMessage(_gameHwnd, WM_KEYUP, (IntPtr)VK_OEM_3, unchecked((IntPtr)0xC0290001));
        }

        /// <summary>
        /// Measure world yaw by toggling auto-run on, sampling two positions,
        /// then toggling it off. Returns NaN if motion too small.
        /// </summary>
        private float MeasureYaw(Func<(float x, float y, float z)> readPos)
        {
            ToggleAutoRun();
            Thread.Sleep(400);
            var (x1, y1, _) = readPos();
            Thread.Sleep(250);
            var (x2, y2, _) = readPos();
            ToggleAutoRun();
            Thread.Sleep(100);

            float dx = x2 - x1;
            float dy = y2 - y1;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag < 2f) return float.NaN;
            return (float)Math.Atan2(dx, dy);
        }


        /// <summary>
        /// Issue a right-click-drag with the given X delta in pixels by
        /// PostMessage'ing WM_RBUTTONDOWN/WM_MOUSEMOVE/WM_RBUTTONUP to the
        /// game window. Works in background — no focus stealing.
        ///
        /// WM_MOUSEMOVE lParam is client coords (LO=x, HI=y). We start the
        /// drag at the window's center, walk the cursor by `dxPixels`, then
        /// release. The game reads the delta between successive WM_MOUSEMOVE
        /// events the same way it reads a real drag.
        /// </summary>
        public void DragTurn(int dxPixels)
        {
            if (_gameHwnd == IntPtr.Zero) return;

            // Start at the client area center
            GetClientRect(_gameHwnd, out var rc);
            int cx = (rc.Right - rc.Left) / 2;
            int cy = (rc.Bottom - rc.Top) / 2;

            // Clamp end-x so it stays inside the window
            int endX = cx + dxPixels;
            if (endX < 4) endX = 4;
            if (endX > rc.Right - 4) endX = rc.Right - 4;
            int actualDx = endX - cx;

            // Right button down at center
            PostMessage(_gameHwnd, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, MakeLParam(cx, cy));
            Thread.Sleep(30);

            // Walk in ~6px steps with a longer per-step delay so the game's
            // camera-follow loop samples each move. Big jumps get treated as
            // teleports and the camera doesn't track them.
            int steps = Math.Max(10, Math.Abs(actualDx) / 6);
            for (int i = 1; i <= steps; i++)
            {
                int x = cx + (int)((long)actualDx * i / steps);
                PostMessage(_gameHwnd, WM_MOUSEMOVE, (IntPtr)MK_RBUTTON, MakeLParam(x, cy));
                Thread.Sleep(12);
            }

            Thread.Sleep(30);
            PostMessage(_gameHwnd, WM_RBUTTONUP, IntPtr.Zero, MakeLParam(endX, cy));
            Thread.Sleep(20);
        }

        private static IntPtr MakeLParam(int x, int y)
        {
            return (IntPtr)((y << 16) | (x & 0xFFFF));
        }

        public void ResetCalibration() => _pixelsPerRadian = 0f;

        private static void Send(int dx, int dy, uint flags)
        {
            var inp = new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
        }
    }
}
