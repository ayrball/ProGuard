using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>The saved config profiles, in the engine's database (table ConfigProfiles, created by <see cref="SchemaUpgrader"/>).</summary>
public sealed class ConfigProfileStore : IConfigProfileStore
{
    private readonly IServiceProvider _services;
    private readonly object _sync = new(); // a save checks the name and writes; two saves of one name must not both insert

    public ConfigProfileStore(IServiceProvider services) => _services = services;

    private T Use<T>(Func<ProGuardDbContext, T> work)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        db.EnsureSchema();
        return work(db);
    }

    public IReadOnlyList<ConfigProfile> GetProfiles() =>
        Use(db => db.ConfigProfiles.AsNoTracking().ToList()
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());

    public ConfigProfile? GetProfile(int id) => Use(db => db.ConfigProfiles.AsNoTracking().FirstOrDefault(p => p.Id == id));

    public ConfigProfile Save(ConfigProfile profile)
    {
        lock (_sync)
        {
            return Use(db =>
            {
                var existing = db.ConfigProfiles.AsEnumerable().FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    profile.Id = existing.Id;
                    db.Entry(existing).CurrentValues.SetValues(profile);
                }
                else
                {
                    profile.Id = 0;
                    db.ConfigProfiles.Add(profile);
                }
                db.SaveChanges();
                return profile;
            });
        }
    }

    public bool Rename(int id, string name)
    {
        lock (_sync)
        {
            return Use(db =>
            {
                var profile = db.ConfigProfiles.Find(id);
                if (profile == null) return false;
                profile.Name = name;
                db.SaveChanges();
                return true;
            });
        }
    }

    public bool Delete(int id)
    {
        lock (_sync)
        {
            return Use(db =>
            {
                var profile = db.ConfigProfiles.Find(id);
                if (profile == null) return false;
                db.ConfigProfiles.Remove(profile);
                db.SaveChanges();
                return true;
            });
        }
    }
}
