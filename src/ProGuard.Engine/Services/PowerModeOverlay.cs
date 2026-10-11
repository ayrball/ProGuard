using System;
using ProGuard.Core.Helpers;

namespace ProGuard.Engine.Services;

/// <summary>The Windows power mode (the power-mode slider), for <see cref="PowerManager"/>. Tests replace it.</summary>
public interface IPowerModeOverlay
{
    /// <summary>The mode in force, or null where Windows has no power-mode slider.</summary>
    Guid? Read();

    bool Write(Guid mode);
}

internal sealed class Win32PowerModeOverlay : IPowerModeOverlay
{
    public Guid? Read() => PowerModes.ReadActual();
    public bool Write(Guid mode) => PowerModes.Write(mode);
}

/// <summary>No slider: for a <see cref="PowerManager"/> on a faked plan API, which must not move the real one.</summary>
internal sealed class NoPowerModeOverlay : IPowerModeOverlay
{
    public Guid? Read() => null;
    public bool Write(Guid mode) => false;
}
