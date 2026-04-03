// DllInjector.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace xajh
{
    public static class DllInjector
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, uint nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
            uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter,
            uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        const uint MEM_COMMIT_RESERVE = 0x3000;
        const uint PAGE_READWRITE = 0x04;
        const uint INFINITE = 0xFFFFFFFF;

        /// <summary>
        /// Injects the DLL at dllPath into the target process.
        /// Returns true on success.
        /// </summary>
        public static bool Inject(IntPtr hProcess, string dllPath)
        {
            dllPath = Path.GetFullPath(dllPath);
            if (!File.Exists(dllPath))
            {
                Console.WriteLine($"[!] DLL not found: {dllPath}");
                return false;
            }

            byte[] pathBytes = Encoding.ASCII.GetBytes(dllPath + "\0");

            // 1. Allocate memory in the target process for the DLL path
            IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero,
                (uint)pathBytes.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero)
            {
                Console.WriteLine($"[!] VirtualAllocEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // 2. Write the DLL path into the target process
            if (!WriteProcessMemory(hProcess, remoteMem, pathBytes,
                (uint)pathBytes.Length, out _))
            {
                Console.WriteLine($"[!] WriteProcessMemory failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // 3. Get LoadLibraryA address (same in all 32-bit processes)
            IntPtr loadLibAddr = GetProcAddress(
                GetModuleHandle("kernel32.dll"), "LoadLibraryA");
            if (loadLibAddr == IntPtr.Zero)
            {
                Console.WriteLine("[!] Could not find LoadLibraryA");
                return false;
            }

            // 4. Create a remote thread in the game that calls LoadLibraryA(dllPath)
            IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                loadLibAddr, remoteMem, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                Console.WriteLine($"[!] CreateRemoteThread failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // 5. Wait for injection to complete
            WaitForSingleObject(hThread, INFINITE);
            CloseHandle(hThread);

            Console.WriteLine($"[+] Injected: {Path.GetFileName(dllPath)}");
            return true;
        }
    }
}