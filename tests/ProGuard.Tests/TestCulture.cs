using System.Globalization;
using System.Runtime.CompilerServices;

namespace ProGuard.Tests;

/// <summary>
/// The tests check English text and English number formats, so they run in en-US whatever the PC's own language is
/// (the UI's text comes from Strings.resx in the current UI culture).
/// </summary>
internal static class TestCulture
{
#pragma warning disable CA2255 // A test assembly is the one place a module initializer should set process-wide state.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void PinEnglish()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
