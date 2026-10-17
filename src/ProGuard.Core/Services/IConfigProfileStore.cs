using System.Collections.Generic;
using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>The saved config profiles. Names are unique, ignoring case.</summary>
public interface IConfigProfileStore
{
    IReadOnlyList<ConfigProfile> GetProfiles();
    ConfigProfile? GetProfile(int id);

    /// <summary>Stores <paramref name="profile"/> under its name, replacing a profile with the same name. Returns the stored row.</summary>
    ConfigProfile Save(ConfigProfile profile);

    /// <summary>False when there is no such profile.</summary>
    bool Rename(int id, string name);

    /// <summary>False when there is no such profile.</summary>
    bool Delete(int id);
}
