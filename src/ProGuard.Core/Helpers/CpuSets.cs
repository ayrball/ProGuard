using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using ProGuard.Core.Native;

namespace ProGuard.Core.Helpers;

/// <summary>One entry of GetSystemCpuSetInformation: a logical processor as the CPU Sets API names it.</summary>
/// <param name="Id">The CPU Set ID that SetProcessDefaultCpuSets takes (not the logical processor number).</param>
/// <param name="EfficiencyClass">Higher is faster: on hybrid CPUs P-cores have a higher class than E-cores.</param>
public readonly record struct CpuSetInfo(
    uint Id,
    ushort Group,
    byte LogicalProcessorIndex,
    byte CoreIndex,
    byte LastLevelCacheIndex,
    byte NumaNodeIndex,
    byte EfficiencyClass,
    bool Parked,
    bool Allocated);

/// <summary>Enumerates the system's CPU Sets and converts between ID lists and the text stored on a rule.</summary>
public static class CpuSets
{
    private const int CpuSetInformationType = 0; // CPU_SET_INFORMATION_TYPE.CpuSetInformation

    /// <summary>All CPU Sets on the machine. Throws <see cref="Win32Exception"/> if Windows refuses.</summary>
    public static IReadOnlyList<CpuSetInfo> GetSystemCpuSets()
    {
        ProcessTuningNative.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!ProcessTuningNative.GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, (int)length);
            return Parse(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Parses a SYSTEM_CPU_SET_INFORMATION buffer. Records are variable-size (each starts with its own Size), so this
    /// walks them by Size rather than assuming the 32-byte layout of today's Windows.
    /// </summary>
    public static IReadOnlyList<CpuSetInfo> Parse(ReadOnlySpan<byte> buffer)
    {
        var result = new List<CpuSetInfo>();
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var record = buffer[offset..];
            int size = BitConverter.ToInt32(record);
            if (size < 8 || size > record.Length) break;

            if (BitConverter.ToInt32(record[4..]) == CpuSetInformationType && size >= 20)
            {
                byte flags = record[19];
                result.Add(new CpuSetInfo(
                    Id: BitConverter.ToUInt32(record[8..]),
                    Group: BitConverter.ToUInt16(record[12..]),
                    LogicalProcessorIndex: record[14],
                    CoreIndex: record[15],
                    LastLevelCacheIndex: record[16],
                    NumaNodeIndex: record[17],
                    EfficiencyClass: record[18],
                    Parked: (flags & 0x1) != 0,
                    Allocated: (flags & 0x2) != 0));
            }

            offset += size;
        }
        return result;
    }

    /// <summary>
    /// Parses the comma/space separated IDs stored in <c>ProcessRule.CpuSetIds</c>. Empty text is a valid, empty list
    /// (meaning "clear the CPU Sets"). Returns false on anything that isn't a list of unsigned integers.
    /// </summary>
    public static bool TryParseIds(string? text, out IReadOnlyList<uint> ids)
    {
        var list = new List<uint>();
        ids = list;
        if (text == null) return false;

        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
            if (!list.Contains(id)) list.Add(id);
        }
        return true;
    }

    public static string FormatIds(IEnumerable<uint> ids) =>
        string.Join(",", ids.OrderBy(i => i).Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
