using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LenovoBacklight
{
    /// <summary>
    /// Low-level interface to the Lenovo ACPI Virtual Power Controller (AcpiVpc.sys) driver.
    /// Driver symlink: \\.\EnergyDrv
    /// IOCTL: 0x83102144 (IOCTL_KBLC_CONTROL)
    /// </summary>
    public static class Kbl
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref uint lpInBuffer,
            uint nInBufferSize,
            ref uint lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        // IOCTL used by Lenovo Vantage (IdeaNotebookAddin) for Keyboard Backlight Level Control
        private const uint IOCTL_KBLC = 0x83102144;

        // SubFunction IDs
        private const uint CMD_GET_STATUS = 0x22;
        private const uint CMD_SET_LEVEL_BASE = 0x23;

        /// <summary>
        /// Sets the keyboard backlight level.
        /// 0 = Off, 1 = Low, 2 = High
        /// Command format: (level &lt;&lt; 16) | 0x23
        /// </summary>
        public static bool SetLevel(uint level)
        {
            IntPtr h = CreateFile(@"\\.\EnergyDrv", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h == IntPtr.Zero || h.ToInt64() == -1) return false;
            try
            {
                uint cmd = (level << 16) | CMD_SET_LEVEL_BASE;
                uint outVal = 0;
                uint bytesRet = 0;
                bool ok = DeviceIoControl(h, IOCTL_KBLC, ref cmd, 4, ref outVal, 4, out bytesRet, IntPtr.Zero);
                return ok && (outVal & 1) == 1;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// Reads the current keyboard backlight level.
        /// Returns 0 (Off), 1 (Low), 2 (High), or -1 on error.
        /// </summary>
        public static int GetLevel()
        {
            IntPtr h = CreateFile(@"\\.\EnergyDrv", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h == IntPtr.Zero || h.ToInt64() == -1) return -1;
            try
            {
                uint cmd = CMD_GET_STATUS;
                uint outVal = 0;
                uint bytesRet = 0;
                bool ok = DeviceIoControl(h, IOCTL_KBLC, ref cmd, 4, ref outVal, 4, out bytesRet, IntPtr.Zero);
                if (!ok || (outVal & 1) == 0) return -1;
                return (int)((outVal >> 1) & 0x7FFF);
            }
            catch
            {
                return -1;
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }

    /// <summary>
    /// Invisible window that listens to Windows power and display notifications in real-time.
    /// </summary>
    public class WakeMonitorForm : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        private const int NOTIFY_FOR_THIS_SESSION = 0;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;

        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        private const int WTS_SESSION_LOGON = 0x5;
        private const int WTS_SESSION_UNLOCK = 0x8;

        private static Guid GUID_CONSOLE_DISPLAY_STATE = new Guid(0x6fe69556, 0x704a, 0x47a0, 0x8f, 0x24, 0xc2, 0x8d, 0x93, 0x6f, 0xda, 0x47);
        private static Guid GUID_SESSION_DISPLAY_STATUS = new Guid(0x2b847714, 0x67ef, 0x4529, 0x98, 0x96, 0x66, 0x46, 0x51, 0x74, 0x2f, 0x26);

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        private IntPtr hConsoleDisplay = IntPtr.Zero;
        private IntPtr hSessionDisplay = IntPtr.Zero;
        private uint targetLevel = 2; // Default High
        private DateTime lastApplyTime = DateTime.MinValue;
        private readonly object lockObj = new object();

        public WakeMonitorForm(uint level)
        {
            targetLevel = level;
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Opacity = 0;
            this.Load += OnLoad;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            this.Visible = false;
            hConsoleDisplay = RegisterPowerSettingNotification(this.Handle, ref GUID_CONSOLE_DISPLAY_STATE, 0);
            hSessionDisplay = RegisterPowerSettingNotification(this.Handle, ref GUID_SESSION_DISPLAY_STATUS, 0);
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_THIS_SESSION);
            Log("WakeMonitor daemon active. Target level: " + targetLevel);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (hConsoleDisplay != IntPtr.Zero) UnregisterPowerSettingNotification(hConsoleDisplay);
            if (hSessionDisplay != IntPtr.Zero) UnregisterPowerSettingNotification(hSessionDisplay);
            WTSUnRegisterSessionNotification(this.Handle);
            base.OnFormClosing(e);
        }

        private void ApplyBacklight(string triggerReason)
        {
            lock (lockObj)
            {
                if ((DateTime.Now - lastApplyTime).TotalMilliseconds < 1500)
                {
                    return; // debounce within 1.5 seconds
                }
                lastApplyTime = DateTime.Now;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Log(string.Format("Wake event triggered ({0}). Setting backlight to level {1}...", triggerReason, targetLevel));

                    // First attempt immediately
                    Kbl.SetLevel(targetLevel);

                    // Re-apply at 400ms and 1000ms to ensure Embedded Controller (EC) accepts it after full resume
                    Thread.Sleep(400);
                    Kbl.SetLevel(targetLevel);
                    Thread.Sleep(600);
                    Kbl.SetLevel(targetLevel);

                    int cur = Kbl.GetLevel();
                    Log("Backlight verified at level: " + cur);
                }
                catch (Exception ex)
                {
                    Log("Error in ApplyBacklight: " + ex.Message);
                }
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST)
            {
                int wParam = m.WParam.ToInt32();
                if (wParam == PBT_APMRESUMEAUTOMATIC || wParam == PBT_APMRESUMESUSPEND)
                {
                    ApplyBacklight("System Resume 0x" + wParam.ToString("X"));
                }
                else if (wParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    POWERBROADCAST_SETTING setting = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(POWERBROADCAST_SETTING));
                    if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE || setting.PowerSetting == GUID_SESSION_DISPLAY_STATUS)
                    {
                        if (setting.Data == 1) // Display turned ON
                        {
                            ApplyBacklight("Display On (state=" + setting.Data + ")");
                        }
                    }
                }
            }
            else if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int wParam = m.WParam.ToInt32();
                if (wParam == WTS_SESSION_UNLOCK || wParam == WTS_SESSION_LOGON)
                {
                    ApplyBacklight("Session Unlock/Logon (0x" + wParam.ToString("X") + ")");
                }
            }

            base.WndProc(ref m);
        }

        public static void Log(string msg)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, msg);
            Console.WriteLine(line);
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LenovoBacklight");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "service.log"), line + Environment.NewLine);
            }
            catch { }
        }
    }

    class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int ATTACH_PARENT_PROCESS = -1;
        const int STD_OUTPUT_HANDLE = -11;
        const int STD_ERROR_HANDLE = -12;
        const int SW_HIDE = 0;

        static void RebindConsole()
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                IntPtr stdOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (stdOut != IntPtr.Zero && stdOut.ToInt64() != -1)
                {
                    Microsoft.Win32.SafeHandles.SafeFileHandle sfh = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOut, false);
                    FileStream fs = new FileStream(sfh, FileAccess.Write);
                    StreamWriter sw = new StreamWriter(fs, System.Text.Encoding.Default);
                    sw.AutoFlush = true;
                    Console.SetOut(sw);
                    Console.SetError(sw);
                }
            }
        }

        static void Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            uint level = 2; // Default: High (level 2)

            if (cmd != "daemon")
            {
                RebindConsole();
            }

            if (args.Length > 1)
            {
                uint.TryParse(args[1], out level);
                if (level > 2) level = 2;
            }

            switch (cmd)
            {
                case "on":
                case "high":
                    Kbl.SetLevel(2);
                    Console.WriteLine("\n[LenovoBacklight] Keyboard backlight set to HIGH (level 2). Current: " + Kbl.GetLevel());
                    break;

                case "low":
                    Kbl.SetLevel(1);
                    Console.WriteLine("\n[LenovoBacklight] Keyboard backlight set to LOW (level 1). Current: " + Kbl.GetLevel());
                    break;

                case "off":
                    Kbl.SetLevel(0);
                    Console.WriteLine("\n[LenovoBacklight] Keyboard backlight set to OFF (level 0). Current: " + Kbl.GetLevel());
                    break;

                case "status":
                    int cur = Kbl.GetLevel();
                    string desc = cur == 0 ? "Off" : (cur == 1 ? "Low" : (cur == 2 ? "High" : "Unknown"));
                    Console.WriteLine("\n[LenovoBacklight] Status:");
                    Console.WriteLine("  Hardware Backlight Level: {0} ({1})", cur, desc);

                    bool daemonRunning = IsDaemonRunning();
                    Console.WriteLine("  Background Wake Daemon:   {0}", daemonRunning ? "Running" : "Stopped");

                    string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LenovoBacklight");
                    string logFile = Path.Combine(appDir, "service.log");
                    if (File.Exists(logFile))
                    {
                        Console.WriteLine("  Log File:                 {0}", logFile);
                    }
                    break;

                case "toggle":
                    int c = Kbl.GetLevel();
                    uint next = c == 0 ? 1u : (c == 1 ? 2u : 0u);
                    Kbl.SetLevel(next);
                    Console.WriteLine("\n[LenovoBacklight] Keyboard backlight toggled to: " + Kbl.GetLevel());
                    break;

                case "wake":
                    WakeMonitorForm.Log("External wake trigger received! Setting level " + level);
                    Kbl.SetLevel(level);
                    Thread.Sleep(400);
                    Kbl.SetLevel(level);
                    Thread.Sleep(600);
                    Kbl.SetLevel(level);
                    WakeMonitorForm.Log("Backlight verified at: " + Kbl.GetLevel());
                    break;

                case "daemon":
                    IntPtr hWnd = GetConsoleWindow();
                    if (hWnd != IntPtr.Zero)
                    {
                        ShowWindow(hWnd, SW_HIDE);
                    }
                    Application.Run(new WakeMonitorForm(level));
                    break;

                case "install":
                    Install(level);
                    break;

                case "uninstall":
                    Uninstall();
                    break;

                default:
                    Console.WriteLine("\nLenovoBacklight - Lenovo Keyboard Backlight Controller on Wake");
                    Console.WriteLine("Commands:");
                    Console.WriteLine("  on | high        Turn backlight ON (High, level 2)");
                    Console.WriteLine("  low              Turn backlight to LOW (level 1)");
                    Console.WriteLine("  off              Turn backlight OFF (level 0)");
                    Console.WriteLine("  status           Check current backlight level and daemon status");
                    Console.WriteLine("  toggle           Cycle Off -> Low -> High -> Off");
                    Console.WriteLine("  install [level]  Install auto-start daemon & wake task (default level: 2)");
                    Console.WriteLine("  uninstall        Remove daemon & scheduled tasks");
                    Console.WriteLine("  daemon [level]   Run wake listener daemon in background");
                    break;
            }
        }

        static bool IsDaemonRunning()
        {
            Process current = Process.GetCurrentProcess();
            Process[] procs = Process.GetProcessesByName("LenovoBacklight");
            foreach (var p in procs)
            {
                if (p.Id != current.Id) return true;
            }
            return false;
        }

        static void Install(uint level)
        {
            Console.WriteLine("\n[LenovoBacklight] Installing auto-wake service...");
            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LenovoBacklight");
            if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);

            string currentExe = Assembly.GetExecutingAssembly().Location;
            string targetExe = Path.Combine(appDir, "LenovoBacklight.exe");

            try
            {
                // Kill existing daemon if running
                StopDaemon();

                if (!string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(currentExe, targetExe, true);
                    Console.WriteLine("  Copied binary to: " + targetExe);
                }

                // 1. Add to Startup Registry (HKCU Run)
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    rk.SetValue("LenovoBacklightDaemon", "\"" + targetExe + "\" daemon " + level);
                    Console.WriteLine("  Configured Windows Logon Startup (HKCU Run)");
                }

                // 2. Configure Scheduled Task for Event 507 & 107
                ConfigureScheduledTask(targetExe, level);

                // 3. Start daemon now
                ProcessStartInfo psi = new ProcessStartInfo(targetExe, "daemon " + level)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
                Console.WriteLine("  Started background wake daemon successfully!");

                // Turn on backlight now
                Kbl.SetLevel(level);
                Console.WriteLine("  Keyboard backlight turned ON (level " + level + ").");
                Console.WriteLine("\n[SUCCESS] LenovoBacklight is fully installed! It will automatically turn on the keyboard backlight whenever your laptop wakes up from sleep or the display turns on.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Installation error: " + ex.Message);
            }
        }

        static void StopDaemon()
        {
            Process current = Process.GetCurrentProcess();
            Process[] procs = Process.GetProcessesByName("LenovoBacklight");
            foreach (var p in procs)
            {
                if (p.Id != current.Id)
                {
                    try { p.Kill(); p.WaitForExit(1000); } catch { }
                }
            }
        }

        static void Uninstall()
        {
            Console.WriteLine("\n[LenovoBacklight] Uninstalling...");
            try
            {
                StopDaemon();
                Console.WriteLine("  Stopped background daemon.");

                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk.GetValue("LenovoBacklightDaemon") != null)
                    {
                        rk.DeleteValue("LenovoBacklightDaemon", false);
                        Console.WriteLine("  Removed Windows Startup entry.");
                    }
                }

                Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /TN \"LenovoBacklightWake\" /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                }).WaitForExit();
                Console.WriteLine("  Removed scheduled wake task.");

                Console.WriteLine("\n[SUCCESS] LenovoBacklight has been uninstalled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Uninstall error: " + ex.Message);
            }
        }

        static void ConfigureScheduledTask(string targetExe, uint level)
        {
            try
            {
                // Write XML task definition to enable running on battery and AC power
                string xmlPath = Path.Combine(Path.GetTempPath(), "LenovoBacklightTask.xml");

                string xmlContent = string.Format(@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Auto restore Lenovo keyboard backlight on sleep resume</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id=""0"" Path=""System""&gt;&lt;Select Path=""System""&gt;*[System[(EventID=507 or EventID=107) and Provider[@Name='Microsoft-Windows-Kernel-Power']]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{0}</Command>
      <Arguments>wake {1}</Arguments>
    </Exec>
  </Actions>
</Task>", targetExe, level);

                File.WriteAllText(xmlPath, xmlContent, System.Text.Encoding.Unicode);

                Process p = Process.Start(new ProcessStartInfo("schtasks.exe", string.Format("/Create /TN \"LenovoBacklightWake\" /XML \"{0}\" /F", xmlPath))
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p.WaitForExit();
                try { File.Delete(xmlPath); } catch { }
                Console.WriteLine("  Configured Windows Scheduled Task for sleep wake (Battery & AC enabled)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Warning: Failed to create scheduled task fallback: " + ex.Message);
            }
        }
    }
}
