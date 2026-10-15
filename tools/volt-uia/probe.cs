// probe.exe <pid> : prints priority, affinity, I/O priority, memory priority, EcoQoS, CPU sets, priority boost.
using System; using System.Runtime.InteropServices; using System.Diagnostics;
class Probe {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("kernel32.dll")] static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr p, out UIntPtr s);
  [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
  [DllImport("kernel32.dll")] static extern bool GetProcessPriorityBoost(IntPtr h, out bool d);
  [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, ref MEMPRI p, int size);
  [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, ref THROTTLE p, int size);
  [DllImport("kernel32.dll")] static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint req);
  [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref int v, int len, out int ret);
  [DllImport("gdi32.dll")] static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int c);
  struct MEMPRI { public uint P; }
  struct THROTTLE { public uint Version, ControlMask, StateMask; }
  static void Main(string[] a) {
    int pid = int.Parse(a[0]);
    IntPtr h = OpenProcess(0x1000 | 0x0400, false, pid); // QUERY_LIMITED | QUERY
    if (h == IntPtr.Zero) h = OpenProcess(0x1000, false, pid);
    if (h == IntPtr.Zero) { Console.WriteLine("pid=" + pid + " open failed " + Marshal.GetLastWin32Error()); return; }
    UIntPtr pm, sm; GetProcessAffinityMask(h, out pm, out sm);
    uint pc = GetPriorityClass(h);
    string pcs = pc == 0x40 ? "Idle" : pc == 0x4000 ? "BelowNormal" : pc == 0x20 ? "Normal" : pc == 0x8000 ? "AboveNormal" : pc == 0x80 ? "High" : pc == 0x100 ? "RealTime" : pc.ToString("X");
    int io = -1, ret; int st = NtQueryInformationProcess(h, 33, ref io, 4, out ret);
    var mp = new MEMPRI(); GetProcessInformation(h, 0, ref mp, 4);
    var th = new THROTTLE { Version = 1 }; GetProcessInformation(h, 4, ref th, 12);
    bool boostDisabled; GetProcessPriorityBoost(h, out boostDisabled);
    uint req; var ids = new uint[64]; string sets = "";
    if (GetProcessDefaultCpuSets(h, ids, 64, out req)) for (int i = 0; i < req; i++) sets += ids[i] + " ";
    int gpu; int gs = D3DKMTGetProcessSchedulingPriorityClass(h, out gpu);
    Console.WriteLine(string.Format("pid={0} priority={1} affinity=0x{2:X} io={3}{4} mem={5} ecoqos=0x{6:X}/0x{7:X} boostDisabled={8} cpusets=[{9}] gpu={10}",
      pid, pcs, (ulong)pm, io, st != 0 ? "(st=0x" + st.ToString("X") + ")" : "", mp.P, th.ControlMask, th.StateMask, boostDisabled, sets.Trim(), gs == 0 ? gpu.ToString() : "err0x" + gs.ToString("X")));
  }
}
