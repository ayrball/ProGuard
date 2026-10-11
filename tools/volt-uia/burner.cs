// burner.exe <threads> <busySeconds> <totalSeconds> [marker]
// Spins <threads> threads for <busySeconds>, then idles until <totalSeconds> (0 = forever).
using System; using System.Threading;
class Burner {
  static void Main(string[] a) {
    int threads = a.Length > 0 ? int.Parse(a[0]) : 1;
    double busy = a.Length > 1 ? double.Parse(a[1]) : 5;
    double total = a.Length > 2 ? double.Parse(a[2]) : 60;
    var end = DateTime.UtcNow.AddSeconds(busy);
    for (int i = 0; i < threads; i++) new Thread(() => { while (DateTime.UtcNow < end) { } }) { IsBackground = true }.Start();
    if (total <= 0) Thread.Sleep(Timeout.Infinite);
    Thread.Sleep(TimeSpan.FromSeconds(total));
  }
}
