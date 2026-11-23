using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AirPlayTray;

public sealed record OwnToneOutput(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("selected")] bool Selected,
    [property: JsonPropertyName("volume")] int Volume,
    [property: JsonPropertyName("offset_ms")] int OffsetMs,
    [property: JsonPropertyName("requires_auth")] bool RequiresAuth,
    [property: JsonPropertyName("needs_auth_key")] bool NeedsAuthKey);

/// <summary>
/// Talks to OwnTone's JSON API, which WSL's mirrored networking exposes on localhost.
/// </summary>
public sealed class OwnToneClient : IDisposable
{
    private sealed record OutputsResponse([property: JsonPropertyName("outputs")] List<OwnToneOutput> Outputs);
    private sealed record PlayerResponse([property: JsonPropertyName("state")] string State);

    // Not "localhost": OwnTone only listens on IPv4, and trying ::1 first eats the timeout
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:3689/api/"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    public async Task<IReadOnlyList<OwnToneOutput>> GetOutputsAsync()
    {
        var response = await _http.GetFromJsonAsync<OutputsResponse>("outputs");
        return response?.Outputs ?? [];
    }

    public async Task<bool> IsReadyAsync()
    {
        try
        {
            await GetOutputsAsync();
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns false when the device refused, which for a device that needs pairing
    /// means it is now showing a PIN.
    /// </summary>
    public Task<bool> SetSelectedAsync(string outputId, bool selected) =>
        PutOutputAsync(outputId, new { selected });

    public Task<bool> SetVolumeAsync(string outputId, int volume) =>
        PutOutputAsync(outputId, new { volume });

    public Task<bool> SetOffsetAsync(string outputId, int offsetMs) =>
        PutOutputAsync(outputId, new { offset_ms = offsetMs });

    public Task<bool> SendPinAsync(string outputId, string pin) =>
        PutOutputAsync(outputId, new { pin });

    /// <summary>
    /// Plays on exactly these devices; with none given, stops every device.
    /// </summary>
    public async Task SelectOnlyAsync(params string[] outputIds)
    {
        using var response = await _http.PutAsJsonAsync("outputs/set", new { outputs = outputIds });
    }

    public async Task<bool> IsPlayingAsync()
    {
        var player = await _http.GetFromJsonAsync<PlayerResponse>("player");
        return player?.State == "play";
    }

    /// <summary>
    /// Replaces OwnTone's queue with the pipe and starts playing it.
    /// </summary>
    public async Task PlayPipeAsync(string pipePath)
    {
        var expression = Uri.EscapeDataString($"path is \"{pipePath}\"");
        using var response = await _http.PostAsync($"queue/items/add?expression={expression}&clear=true&playback=start", null);
    }

    public async Task SetMasterVolumeAsync(int volume)
    {
        using var response = await _http.PutAsync($"player/volume?volume={volume}", null);
    }

    private async Task<bool> PutOutputAsync(string outputId, object body)
    {
        using var response = await _http.PutAsJsonAsync($"outputs/{outputId}", body);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}
