using System;
using System.Collections.Generic;

namespace xajh
{
    class PlayerReader
    {
        enum XySourceMode
        {
            Auto,
            Global,
            Object
        }

        // Primary path: read player object matrix-translation candidates and
        // choose the most plausible world position each tick.
        // Fallback path: use global mirror address discovered by [O] scanner.
        const long MgrOffset = 0x9D4518;
        const int ListOffset = 0x08;
        const int PlayerObjOffset = 0x4C;
        static readonly int[] PosCandidateOffsets = { 0x34, 0x64, 0x94, 0xC4 };
        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);
        static bool _globalPosLocked;

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;
        int _preferredPosOffset = 0x94;
        bool _hasLocReference;
        float _locRefX, _locRefY;
        XySourceMode _xySourceMode = XySourceMode.Auto;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public static void SetGlobalPosAddr(IntPtr addr, bool locked = true)
        {
            GlobalPosAddr = addr;
            _globalPosLocked = locked;
        }

        public static void SetGlobalPosLocked(bool locked) => _globalPosLocked = locked;

        public static bool IsGlobalPosLocked() => _globalPosLocked;

        public string UpdateLocationReference(float locX, float locY)
        {
            _hasLocReference = true;
            _locRefX = locX;
            _locRefY = locY;

            bool haveGlobal = TryReadGlobalMirror(out float gx, out float gy);
            double dGlobal = double.MaxValue;
            if (haveGlobal)
                dGlobal = Math.Sqrt(Math.Pow(gx - locX, 2) + Math.Pow(gy - locY, 2));

            if (!TryGetPlayerObject(out IntPtr pObj))
            {
                if (haveGlobal)
                {
                    _xySourceMode = XySourceMode.Global;
                    return $"Reference saved: using GLOBAL XY (d={dGlobal:F1})";
                }
                return "Reference saved (player object unavailable now)";
            }

            int bestOffset = _preferredPosOffset;
            double bestDist = double.MaxValue;
            foreach (int off in PosCandidateOffsets)
            {
                float cx = MemoryHelper.ReadFloat(_h, IntPtr.Add(pObj, off));
                float cy = MemoryHelper.ReadFloat(_h, IntPtr.Add(pObj, off + 4));
                float cz = MemoryHelper.ReadFloat(_h, IntPtr.Add(pObj, off + 8));
                if (!IsCoordinatePlausible(cx, cy, cz)) continue;
                double d = Math.Sqrt(Math.Pow(cx - locX, 2) + Math.Pow(cy - locY, 2));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestOffset = off;
                }
            }

            if (bestDist == double.MaxValue)
            {
                if (haveGlobal)
                {
                    _xySourceMode = XySourceMode.Global;
                    return $"Reference saved: using GLOBAL XY (d={dGlobal:F1}), no valid object offsets";
                }
                return "Reference saved (no valid object offsets now)";
            }

            _preferredPosOffset = bestOffset;
            // Prefer GLOBAL when available unless object offset is clearly closer
            // to /loc by a meaningful margin.
            if (!haveGlobal || bestDist + 20f < dGlobal)
            {
                _xySourceMode = XySourceMode.Object;
                return $"Reference saved: using OBJECT+0x{bestOffset:X} (d={bestDist:F1})";
            }

            _xySourceMode = XySourceMode.Global;
            return $"Reference saved: using GLOBAL XY (d={dGlobal:F1}), object+0x{bestOffset:X} d={bestDist:F1}";
        }

        public void ClearLocationReference()
        {
            _hasLocReference = false;
            _xySourceMode = XySourceMode.Auto;
        }

        public void PreferGlobalSource()
        {
            _hasLocReference = false;
            _xySourceMode = XySourceMode.Global;
        }

        readonly List<IntPtr> _autoMirrorCandidates = new List<IntPtr>();
        int _nextAutoLockTick;
        int _autoScanRound;

        public bool TryAutoLockGlobalMirror(out string status)
        {
            status = null;
            if (_globalPosLocked) return true;

            int now = Environment.TickCount;
            if (now < _nextAutoLockTick) return false;

            if (!TryReadPlayerObjectPos(out float ox, out float oy, out _))
            {
                _nextAutoLockTick = now + 1200;
                return false;
            }

            if (_autoMirrorCandidates.Count == 0)
            {
                _autoScanRound++;
                status = $"[*] Auto-loc scan {_autoScanRound}: find mirror for ({ox:F0}, {oy:F0})";
                var hits = MemoryHelper.ScanForFloat(_h, ox, 5f);
                foreach (var addr in hits)
                {
                    float y = MemoryHelper.ReadFloat(_h, IntPtr.Add(addr, 4));
                    if (Math.Abs(y - oy) < 5f)
                        _autoMirrorCandidates.Add(addr);
                }
            }
            else
            {
                var keep = new List<IntPtr>();
                foreach (var addr in _autoMirrorCandidates)
                {
                    float x = MemoryHelper.ReadFloat(_h, addr);
                    float y = MemoryHelper.ReadFloat(_h, IntPtr.Add(addr, 4));
                    if (Math.Abs(x - ox) < 6f && Math.Abs(y - oy) < 6f)
                        keep.Add(addr);
                }
                _autoMirrorCandidates.Clear();
                _autoMirrorCandidates.AddRange(keep);
            }

            if (_autoMirrorCandidates.Count > 0)
            {
                SetGlobalPosAddr(_autoMirrorCandidates[0], locked: true);
                PreferGlobalSource();
                status = $"[+] Auto-locked global XY mirror @ 0x{_autoMirrorCandidates[0].ToInt64():X8} ({_autoMirrorCandidates.Count} aliases)";
                return true;
            }

            status = $"[!] Auto-loc scan {_autoScanRound} found no candidates; retrying";
            _nextAutoLockTick = now + 2000;
            return false;
        }

        public (float x, float y, float z) Get()
        {
            try
            {
                bool haveObjPos = TryReadPlayerObjectPos(out float objX, out float objY, out float objZ);
                bool haveGlobal = TryReadGlobalMirror(out float gx, out float gy);

                float x, y, z;
                if (haveGlobal && haveObjPos)
                {
                    bool useObjXY;
                    if (_xySourceMode == XySourceMode.Global)
                    {
                        useObjXY = false;
                    }
                    else if (_xySourceMode == XySourceMode.Object)
                    {
                        useObjXY = true;
                    }
                    else
                    {
                        useObjXY = false;
                        if (_hasCache)
                        {
                            double dGlobal = Math.Sqrt(Math.Pow(gx - _cx, 2) + Math.Pow(gy - _cy, 2));
                            double dObj = Math.Sqrt(Math.Pow(objX - _cx, 2) + Math.Pow(objY - _cy, 2));
                            // If global jumps wildly while object position stays smooth,
                            // trust object XY for this tick (global mirror may be stale).
                            if (dGlobal > 300f && dObj < 80f)
                                useObjXY = true;
                        }
                    }

                    x = useObjXY ? objX : gx;
                    y = useObjXY ? objY : gy;
                    z = objZ;
                }
                else if (haveGlobal)
                {
                    x = gx;
                    y = gy;
                    z = _hasCache ? _cz : 0f;
                }
                else if (haveObjPos)
                {
                    x = objX;
                    y = objY;
                    z = objZ;
                }
                else
                {
                    return Cached();
                }

                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return Cached();
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return Cached();

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        bool TryReadPlayerObjectPos(out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;
            if (!TryGetPlayerObject(out IntPtr p)) return false;
            bool haveGlobal = TryReadGlobalMirror(out float gx, out float gy);

            float bestScore = float.MinValue;
            int bestOffset = 0;
            float bx = 0f, by = 0f, bz = 0f;
            bool found = false;

            foreach (int off in PosCandidateOffsets)
            {
                float cx = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, off));
                float cy = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, off + 4));
                float cz = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, off + 8));
                if (!IsCoordinatePlausible(cx, cy, cz)) continue;

                float score = 0f;
                if ((_hasCache || haveGlobal) && off == _preferredPosOffset) score += 0.7f;

                if (_hasCache)
                {
                    double dxy = Math.Sqrt(Math.Pow(cx - _cx, 2) + Math.Pow(cy - _cy, 2));
                    if (dxy <= 3f) score += 3f;
                    else if (dxy <= 60f) score += 2f;
                    else if (dxy <= 250f) score += 1f;
                    else score -= 3f;

                    float dz = Math.Abs(cz - _cz);
                    if (dz <= 20f) score += 0.5f;
                    else if (dz > 500f) score -= 1f;
                }

                if (haveGlobal)
                {
                    double dg = Math.Sqrt(Math.Pow(cx - gx, 2) + Math.Pow(cy - gy, 2));
                    if (dg <= 3f) score += 4f;
                    else if (dg <= 30f) score += 2f;
                    else if (dg <= 200f) score += 0.5f;
                    else score -= 2f;
                }

                if (HasActiveLocReference())
                {
                    double dr = Math.Sqrt(Math.Pow(cx - _locRefX, 2) + Math.Pow(cy - _locRefY, 2));
                    if (dr <= 2f) score += 6f;
                    else if (dr <= 30f) score += 3f;
                    else if (dr <= 120f) score += 1f;
                    else score -= 2f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = off;
                    bx = cx; by = cy; bz = cz;
                    found = true;
                }
            }

            if (!found) return false;
            _preferredPosOffset = bestOffset;
            x = bx; y = by; z = bz;
            return true;
        }

        bool TryReadGlobalMirror(out float x, out float y)
        {
            if (!_globalPosLocked)
            {
                x = 0f; y = 0f;
                return false;
            }

            x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
            if (float.IsNaN(x) || float.IsNaN(y)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return false;
            return true;
        }

        bool TryGetPlayerObject(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, (int)MgrOffset));
            if (mgr == 0) return false;

            int list = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)mgr), ListOffset));
            if (list == 0) return false;

            int raw = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)list), PlayerObjOffset));
            if (raw == 0) return false;

            playerObj = new IntPtr((uint)raw);
            return true;
        }

        bool HasActiveLocReference()
        {
            return _hasLocReference;
        }

        static bool IsCoordinatePlausible(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f || Math.Abs(z) > 1_000_000f)
                return false;
            return true;
        }

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }
}
