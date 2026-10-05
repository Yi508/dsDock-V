using System;
using System.Runtime.InteropServices;

namespace DsDock.Platform;

/// <summary>
/// Processes launched from a confined (low integrity) shell inherit that integrity level, and
/// Windows then refuses some cross integrity operations (tray icon, synthetic input, reparenting
/// into explorer's window tree). Recording this makes a failed run diagnosable instead of
/// looking like an application bug.
/// </summary>
internal static class ProcessIntegrity
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

    /// <summary>
    /// 父进程名 + PID。当进程"莫名"是 Low 时，这一行能指出是谁以低完整性把它拉起来的
    /// （例如从被沙箱化的宿主、或从受限终端启动）。
    /// </summary>
    public static string DescribeParent()
    {
        try
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(0x00000002 /*TH32CS_SNAPPROCESS*/, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return "unknown";

            try
            {
                uint me = (uint)Environment.ProcessId;
                uint parent = 0;
                var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32ProcessID == me) { parent = entry.th32ParentProcessID; break; }
                    } while (Process32NextW(snapshot, ref entry));
                }

                string parentName = "";
                if (parent != 0)
                {
                    var again = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
                    if (Process32FirstW(snapshot, ref again))
                    {
                        do
                        {
                            if (again.th32ProcessID == parent) { parentName = again.szExeFile; break; }
                        } while (Process32NextW(snapshot, ref again));
                    }
                }
                return $"{parentName} (pid={parent})";
            }
            finally { CloseHandle(snapshot); }
        }
        catch (Exception ex)
        {
            return "unknown: " + ex.Message;
        }
    }

    /// <summary>e.g. "Medium (0x2000)"</summary>
    public static string Describe()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
            return "unknown";

        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
            if (size <= 0) return "unknown";

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                    return "unknown";

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                IntPtr sid = label.Label.Sid;
                if (sid == IntPtr.Zero) return "unknown";

                byte count = Marshal.ReadByte(sid, 1);
                int rid = count > 0 ? Marshal.ReadInt32(sid, 8 + (count - 1) * 4) : -1;
                string name = rid switch
                {
                    0x0000 => "Untrusted",
                    0x1000 => "Low",
                    0x2000 => "Medium",
                    0x2100 => "MediumPlus",
                    0x3000 => "High",
                    0x4000 => "System",
                    _ => "Unknown",
                };
                return $"{name} (0x{rid:X4})";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
