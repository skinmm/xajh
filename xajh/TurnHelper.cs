using System;
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
        // Window messages
        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const int MK_RBUTTON = 0x0002;
        const int VK_X = 0x58;
        const int VK_F = 0x46;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        // ── State ─────────────────────────────────────────────────────────────
        private readonly IntPtr _hProcess;
        private readonly IntPtr _moduleBase;
        private readonly IntPtr _gameHwnd;
        private float _pixelsPerRadian = 0f;     // 0 = uncalibrated

        const int PlayerMgrOffset = 0x9D4518;
        const int PlayerListOffset = 0x08;
        const int PlayerObjOffset = 0x4C;

        public TurnHelper(IntPtr hProcess, IntPtr moduleBase, IntPtr gameHwnd)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
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
        /// Turn to face (tx, ty) from (px, py) WITHOUT moving the player.
        /// Uses player matrix yaw + right-drag input only.
        /// </summary>
        public string FaceTarget(Func<(float x, float y, float z)> readPos,
                                 float tx, float ty)
        {
            // Auto-aim runs on a timer while the console often has focus; many clients
            // only apply keyboard/mouse look when the game window is active.
            if (_gameHwnd != IntPtr.Zero)
                SetForegroundWindow(_gameHwnd);
            Thread.Sleep(80);

            int playerObj = GetPlayerObject();
            if (playerObj == 0)
                return "[!] Player object not found";

            float curYaw = ReadPlayerYaw(playerObj);
            if (float.IsNaN(curYaw))
                return "[!] Failed to read current yaw";

            var (px, py, _) = readPos();
            float dx = tx - px;
            float dy = ty - py;
            // Matrix yaw is opposite of world-vector yaw in this client, so
            // add PI to align "face target" with actual model orientation.
            float targetYaw = (float)Math.Atan2(dx, dy) + (float)Math.PI;

            float delta = targetYaw - curYaw;
            while (delta > Math.PI) delta -= 2f * (float)Math.PI;
            while (delta < -Math.PI) delta += 2f * (float)Math.PI;

            if (_pixelsPerRadian == 0f)
            {
                const int TestPx = 200;
                DragTurn(TestPx);
                Thread.Sleep(120);

                int playerObjAfterDrag = GetPlayerObject();
                if (playerObjAfterDrag == 0)
                    return "[!] Cal failed: player object not found after drag";

                float newYaw = ReadPlayerYaw(playerObjAfterDrag);
                if (float.IsNaN(newYaw))
                    return "[!] Cal failed: second yaw read failed";

                float yawChange = newYaw - curYaw;
                while (yawChange > Math.PI) yawChange -= 2f * (float)Math.PI;
                while (yawChange < -Math.PI) yawChange += 2f * (float)Math.PI;
                if (Math.Abs(yawChange) < 0.01f)
                    return $"[!] Cal failed: drag={TestPx}px gave yawΔ={yawChange:F2}";

                _pixelsPerRadian = TestPx / yawChange;
                curYaw = newYaw;
            }

            int pixels = 0;
            for (int i = 0; i < 2; i++)
            {
                delta = targetYaw - curYaw;
                while (delta > Math.PI) delta -= 2f * (float)Math.PI;
                while (delta < -Math.PI) delta += 2f * (float)Math.PI;

                pixels = (int)Math.Round(delta * _pixelsPerRadian);
                if (pixels == 0 && Math.Abs(delta) > 0.03f)
                    pixels = delta > 0f ? 1 : -1;

                DragTurn(pixels);
                Thread.Sleep(80);

                int pObj = GetPlayerObject();
                if (pObj == 0) break;
                float newYaw = ReadPlayerYaw(pObj);
                if (float.IsNaN(newYaw)) break;
                curYaw = newYaw;
            }

            return $"cur={curYaw:F2} tgt={targetYaw:F2} Δ={delta:F2} ({pixels}px) cal={_pixelsPerRadian:F0}";
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

        /// <summary>
        /// Target nearest NPC then start fight by sending "X" then "F".
        /// </summary>
        public bool TriggerTargetAndFight()
        {
            if (_gameHwnd == IntPtr.Zero) return false;

            // Keep behavior consistent with turn logic: ensure game is active.
            SetForegroundWindow(_gameHwnd);
            Thread.Sleep(30);
            bool targetDown = PostMessage(_gameHwnd, WM_KEYDOWN, (IntPtr)VK_X, IntPtr.Zero);
            Thread.Sleep(20);
            bool targetUp = PostMessage(_gameHwnd, WM_KEYUP, (IntPtr)VK_X, IntPtr.Zero);
            Thread.Sleep(40);
            bool fightDown = PostMessage(_gameHwnd, WM_KEYDOWN, (IntPtr)VK_F, IntPtr.Zero);
            Thread.Sleep(20);
            bool fightUp = PostMessage(_gameHwnd, WM_KEYUP, (IntPtr)VK_F, IntPtr.Zero);
            return targetDown && targetUp && fightDown && fightUp;
        }

        private int GetPlayerObject()
        {
            int mgr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, PlayerMgrOffset));
            if (mgr == 0) return 0;
            int list = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(new IntPtr((uint)mgr), PlayerListOffset));
            if (list == 0) return 0;
            return MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(new IntPtr((uint)list), PlayerObjOffset));
        }
    }
}
