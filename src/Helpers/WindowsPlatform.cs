namespace Loupedeck.AntigravityControllerPlugin.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Threading;

    // Windows counterparts of the macOS tools the plugin uses (ps, lsof, open -a, osascript keystrokes, sqlite3)
    [SupportedOSPlatform("windows")]
    internal static class WindowsPlatform
    {
        // Command lines never change for a running process, so only new pids are read
        private static readonly Dictionary<Int32, String> CommandLineCache = new();
        private static readonly Object CacheLock = new();

        // ---------------------------------------------------------------- Processes (ps)

        // "pid commandline" lines of the running Antigravity language servers, like `ps -ax -o pid,command`
        public static String ListLanguageServers()
        {
            var running = new HashSet<Int32>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.ProcessName.StartsWith("language_server", StringComparison.OrdinalIgnoreCase))
                    {
                        running.Add(p.Id);
                    }
                }
            }

            lock (CacheLock)
            {
                foreach (var gone in CommandLineCache.Keys.Where(pid => !running.Contains(pid)).ToList())
                {
                    CommandLineCache.Remove(gone);
                }

                foreach (var pid in running.Where(pid => !CommandLineCache.ContainsKey(pid)))
                {
                    var commandLine = GetCommandLine(pid);
                    if (commandLine != null)
                    {
                        CommandLineCache[pid] = commandLine;
                    }
                }

                return String.Join('\n', CommandLineCache
                    .Where(kv => running.Contains(kv.Key))
                    .Select(kv => $"{kv.Key} {kv.Value}"));
            }
        }

        private const UInt32 ProcessQueryLimitedInformation = 0x1000;
        private const Int32 ProcessCommandLineInformation = 60;

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public UInt16 Length;
            public UInt16 MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(UInt32 access, Boolean inherit, Int32 pid);

        [DllImport("kernel32.dll")]
        private static extern Boolean CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern Int32 NtQueryInformationProcess(IntPtr process, Int32 infoClass, IntPtr info, Int32 size, out Int32 returnLength);

        // Command line of another process of the same user (Windows 8.1 and later)
        private static String GetCommandLine(Int32 pid)
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out var size);
                if (size <= 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out _) != 0)
                    {
                        return null;
                    }
                    var str = Marshal.PtrToStructure<UnicodeString>(buffer);
                    return Marshal.PtrToStringUni(str.Buffer, str.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // ---------------------------------------------------------------- Listening ports (lsof)

        private const Int32 AfInet = 2;
        private const Int32 TcpTableOwnerPidListener = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public UInt32 State;
            public UInt32 LocalAddr;
            public UInt32 LocalPort;
            public UInt32 RemoteAddr;
            public UInt32 RemotePort;
            public UInt32 OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern UInt32 GetExtendedTcpTable(IntPtr pTcpTable, ref Int32 dwOutBufLen, Boolean sort,
            Int32 ipVersion, Int32 tblClass, UInt32 reserved);

        // IPv4 TCP ports the process listens on
        public static List<Int32> GetListeningPorts(Int32 pid)
        {
            var ports = new List<Int32>();
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
                {
                    return ports;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + 4;
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++, rowPtr += rowSize)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                    if (row.OwningPid == pid)
                    {
                        // Port is in network byte order in the low 16 bits
                        ports.Add((Int32)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF)));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return ports;
        }

        // ---------------------------------------------------------------- Windows (open -a, lsappinfo)

        private const Int32 SwRestore = 9;
        private const Byte VkMenu = 0x12;
        private const UInt32 KeyEventFKeyUp = 0x0002;

        [DllImport("user32.dll")]
        private static extern Boolean SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern Boolean ShowWindow(IntPtr hWnd, Int32 nCmdShow);

        [DllImport("user32.dll")]
        private static extern Boolean IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(Byte bVk, Byte bScan, UInt32 dwFlags, UIntPtr dwExtraInfo);

        // appName is both the process name and the install folder, e.g. %LOCALAPPDATA%\Programs\Antigravity IDE\Antigravity IDE.exe
        public static Boolean HasOpenWindow(String appName) => FindMainWindow(appName) != IntPtr.Zero;

        // Brings the app's main window to the front, or starts the app when it is not running
        public static void ActivateApp(String appName)
        {
            var hWnd = FindMainWindow(appName);
            if (hWnd == IntPtr.Zero)
            {
                StartApp(appName);
                return;
            }

            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SwRestore);
            }

            // Windows only lets the process that received the last input change the foreground window;
            // a synthetic Alt press lifts that restriction for the Logi Plugin Service
            keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event(VkMenu, 0, KeyEventFKeyUp, UIntPtr.Zero);
            SetForegroundWindow(hWnd);
        }

        private static IntPtr FindMainWindow(String appName)
        {
            foreach (var p in Process.GetProcessesByName(appName))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        return p.MainWindowHandle;
                    }
                }
            }
            return IntPtr.Zero;
        }

        private static void StartApp(String appName)
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", appName, appName + ".exe");
            if (!File.Exists(exe))
            {
                PluginLog.Warning($"[Windows] {appName} is not running and {exe} was not found");
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true })?.Dispose();
        }

        // ---------------------------------------------------------------- Keystrokes (osascript keystroke)

        private const UInt32 InputKeyboard = 1;
        private const UInt16 VkControl = 0x11;
        private const UInt16 VkShift = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct KeybdInput
        {
            public UInt16 Vk;
            public UInt16 Scan;
            public UInt32 Flags;
            public UInt32 Time;
            public IntPtr ExtraInfo;
        }

        // INPUT is a union; the mouse variant is the largest member and sets the struct size
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)] public UInt32 Type;
            [FieldOffset(8)] public KeybdInput Ki;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern UInt32 SendInput(UInt32 nInputs, Input[] pInputs, Int32 cbSize);

        // Activates the app, waits for it to take focus and sends Ctrl+Shift+<key>
        public static void SendCtrlShiftShortcut(String appName, Char key)
        {
            ActivateApp(appName);
            Thread.Sleep(400);

            var vk = (UInt16)Char.ToUpperInvariant(key);
            var inputs = new[]
            {
                KeyInput(VkControl, false), KeyInput(VkShift, false), KeyInput(vk, false),
                KeyInput(vk, true), KeyInput(VkShift, true), KeyInput(VkControl, true)
            };
            SendInput((UInt32)inputs.Length, inputs, Marshal.SizeOf<Input>());
        }

        private static Input KeyInput(UInt16 vk, Boolean up) =>
            new() { Type = InputKeyboard, Ki = new KeybdInput { Vk = vk, Flags = up ? KeyEventFKeyUp : 0 } };

        // ---------------------------------------------------------------- SQLite (sqlite3 CLI)

        // winsqlite3.dll ships with Windows 10 and later, so no SQLite library has to be bundled
        private const String WinSqlite = "winsqlite3.dll";
        private const Int32 SqliteOk = 0;
        private const Int32 SqliteRow = 100;
        private const Int32 SqliteOpenReadOnly = 0x00000001;

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern Int32 sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] String filename, out IntPtr db, Int32 flags, IntPtr vfs);

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern Int32 sqlite3_close(IntPtr db);

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern Int32 sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] String sql, Int32 nByte, out IntPtr stmt, IntPtr tail);

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern Int32 sqlite3_step(IntPtr stmt);

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern Int32 sqlite3_column_count(IntPtr stmt);

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, Int32 col);

        [DllImport(WinSqlite, CallingConvention = CallingConvention.StdCall)]
        private static extern Int32 sqlite3_finalize(IntPtr stmt);

        // Rows of a read-only query, every column as text
        public static List<String[]> QuerySqlite(String path, String sql)
        {
            var rows = new List<String[]>();
            if (sqlite3_open_v2(path, out var db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk)
            {
                sqlite3_close(db);
                return rows;
            }

            try
            {
                if (sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != SqliteOk)
                {
                    return rows;
                }

                try
                {
                    var columns = sqlite3_column_count(stmt);
                    while (sqlite3_step(stmt) == SqliteRow)
                    {
                        var row = new String[columns];
                        for (var i = 0; i < columns; i++)
                        {
                            row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, i)) ?? "";
                        }
                        rows.Add(row);
                    }
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close(db);
            }

            return rows;
        }
    }
}
