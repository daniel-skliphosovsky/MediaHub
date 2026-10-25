using System.Text;
using System.Text.Json;

namespace MediaHub.Services.Downloaders;

internal readonly record struct YouTubeProgressiveStream(string Url, string QualityLabel);

/// <summary>
/// Reads the progressive (video+audio) MP4 stream from YouTube's Android
/// player response. YoutubeExplode's primary clients now return only
/// adaptive streams (separate video and audio), while the Android client
/// still exposes the muxed format, which this app downloads without ffmpeg.
/// </summary>
internal static class YouTubePlayback
{
    public static async Task<YouTubeProgressiveStream?> GetProgressiveStreamAsync(
        HttpClient http, string videoId, CancellationToken ct = default)
    {
        string payload = $$"""
            {
              "videoId": {{JsonSerializer.Serialize(videoId)}},
              "contentCheckOk": true,
              "racyCheckOk": true,
              "context": {
                "client": {
                  "clientName": "ANDROID",
                  "clientVersion": "21.26.364",
                  "androidSdkVersion": 30,
                  "osName": "Android",
                  "osVersion": "11",
                  "hl": "en",
                  "gl": "US",
                  "utcOffsetMinutes": 0
                }
              }
            }
            """;

        using HttpRequestMessage request = new(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("streamingData", out JsonElement streamingData) ||
            !streamingData.TryGetProperty("formats", out JsonElement formats) ||
            formats.ValueKind != JsonValueKind.Array)
            return null;

        YouTubeProgressiveStream? best = null;
        int bestQuality = -1;

        foreach (JsonElement format in formats.EnumerateArray())
        {
            if (!format.TryGetProperty("url", out JsonElement urlElement) ||
                urlElement.ValueKind != JsonValueKind.String)
                continue;

            string mimeType = format.TryGetProperty("mimeType", out JsonElement mimeElement)
                ? mimeElement.GetString() ?? string.Empty
                : string.Empty;
            if (!mimeType.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
                continue;

            string qualityLabel = format.TryGetProperty("qualityLabel", out JsonElement labelElement)
                ? labelElement.GetString() ?? string.Empty
                : string.Empty;

            int quality = ParseQuality(qualityLabel);
            if (quality <= bestQuality)
                continue;

            bestQuality = quality;
            best = new YouTubeProgressiveStream(urlElement.GetString()!, qualityLabel);
        }

        return best;
    }

    private static int ParseQuality(string label)
    {
        int length = 0;
        while (length < label.Length && char.IsDigit(label[length]))
            length++;

        return length > 0 && int.TryParse(label[..length], out int quality) ? quality : 0;
    }
}
