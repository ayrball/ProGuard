using System.Text.Json;

namespace ProGuard.UI.Services;

/// <summary>
/// Copies a wire DTO, so an editor can change a copy and send it back with every field it doesn't show left exactly
/// as the engine sent it.
/// </summary>
internal static class DtoCopy
{
    public static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
