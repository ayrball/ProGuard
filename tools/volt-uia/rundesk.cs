// rundesk.exe <desktopName> <commandLine...>
// Runs a command on a separate (invisible) window-station desktop, creating the desktop if needed, waits for it and
// relays its stdout/stderr. Nothing appears on the user's desktop. With "-nowait" as the first argument after the
// desktop name it starts the process and prints its PID without waiting.
using System; using System.Runtime.InteropServices; using System.Text; using System.IO; using System.Threading;
class RunDesk {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr CreateDesktop(string name, IntPtr dev, IntPtr dm, int flags, uint access, IntPtr sa);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct SI { public int cb; public string r, desk, title; public int x, y, w, h, cx, cy, fill, flags; public short show, r2; public IntPtr r3, hin, hout, herr; }
  [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr hp, ht; public int pid, tid; }
  [StructLayout(LayoutKind.Sequential)] struct SA { public int n; public IntPtr d; public bool inherit; }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref SI si, out PI pi);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool CreatePipe(out IntPtr r, out IntPtr w, ref SA sa, int size);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetHandleInformation(IntPtr h, int mask, int flags);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
  static int Main(string[] a) {
    string desk = a[0]; int i = 1; bool nowait = false;
    if (a.Length > 1 && a[1] == "-nowait") { nowait = true; i = 2; }
    var cmd = new StringBuilder();
    for (; i < a.Length; i++) { var s = a[i]; cmd.Append(i > (nowait ? 2 : 1) ? " " : "").Append(s.Contains(" ") && !s.StartsWith("\"") ? "\"" + s + "\"" : s); }
    if (CreateDesktop(desk, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /*GENERIC_ALL*/, IntPtr.Zero) == IntPtr.Zero) { Console.Error.WriteLine("CreateDesktop failed " + Marshal.GetLastWin32Error()); return 2; }
    var si = new SI(); si.cb = Marshal.SizeOf(si); si.desk = "WinSta0\\" + desk;
    IntPtr rd = IntPtr.Zero, wr = IntPtr.Zero;
    if (!nowait) {
      var sa = new SA { n = Marshal.SizeOf(typeof(SA)), inherit = true };
      CreatePipe(out rd, out wr, ref sa, 0); SetHandleInformation(rd, 1, 0);
      si.flags = 0x100; si.hout = wr; si.herr = wr; si.hin = IntPtr.Zero;
    }
    PI pi;
    if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, !nowait, 0x08000000 /*CREATE_NO_WINDOW*/, IntPtr.Zero, null, ref si, out pi)) { Console.Error.WriteLine("CreateProcess failed " + Marshal.GetLastWin32Error()); return 3; }
    if (nowait) { Console.WriteLine(pi.pid); Thread.Sleep(8000); return 0; } // keep the desktop alive until the child has attached to it
    CloseHandle(wr);
    using (var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(rd, true), FileAccess.Read)) using (var sr = new StreamReader(fs)) {
      string line; while ((line = sr.ReadLine()) != null) Console.WriteLine(line);
    }
    WaitForSingleObject(pi.hp, 0xFFFFFFFF); uint code; GetExitCodeProcess(pi.hp, out code); return (int)code;
  }
}
