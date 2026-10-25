using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MediaHub.Models;
using MediaHub.Services.Interfaces;
using MediaHub.Services.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace MediaHub.Services.Downloaders;

public sealed class YouTubeDownloader : IDownloader
{
    public string PlatformName => "YouTube";

    private readonly HttpClient _http;
    private readonly YoutubeClient _client;

    public YouTubeDownloader(HttpClient httpClient)
    {
        _http = httpClient;
        _client = new YoutubeClient(httpClient);
    }

    public bool CanHandle(string url) =>
        UrlHelpers.UrlBelongsTo(url, "youtube.com", "youtu.be");

    /// <summary>
    /// A YouTube URL is a playlist when it carries a "list=" query parameter
    /// (playlists, mixes, watch-with-list, youtu.be short links).
    /// </summary>
    public bool IsPlaylistUrl(string url) => TryGetPlaylistId(url, out _);

    /// <summary>
    /// Resolves the playlist id from the URL and enumerates its videos in
    /// play order, each as its own watch URL so the shared single-video
    /// download path can be reused for every item.
    /// </summary>
    public async Task<IReadOnlyList<PlaylistItem>> GetPlaylistItemsAsync(string url, CancellationToken ct = default)
    {
        if (!TryGetPlaylistId(url, out string? playlistId) || playlistId is null)
            return [];

        List<PlaylistItem> items = new List<PlaylistItem>();
        await foreach (var video in _client.Playlists.GetVideosAsync(playlistId, ct))
            items.Add(new PlaylistItem($"https://www.youtube.com/watch?v={video.Id.Value}", video.Title));

        return items;
    }

    /// <summary>
    /// The playlist title, used as the name of the subfolder the videos are
    /// saved into.
    /// </summary>
    public async Task<string?> GetPlaylistTitleAsync(string url, CancellationToken ct = default)
    {
        if (!TryGetPlaylistId(url, out string? playlistId) || playlistId is null)
            return null;

        YoutubeExplode.Playlists.Playlist playlist = await _client.Playlists.GetAsync(playlistId, ct);
        return playlist.Title;
    }

    private static bool TryGetPlaylistId(string url, out string? playlistId)
    {
        playlistId = null;

        string value = string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            !Uri.TryCreate("https://" + value, UriKind.Absolute, out uri))
            return false;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("list", StringComparison.OrdinalIgnoreCase) && kv[1].Length > 0)
            {
                playlistId = kv[1];
                return true;
            }
        }

        return false;
    }

    public async Task<MediaPreview?> GetPreviewAsync(string url, CancellationToken ct = default)
    {
        YoutubeExplode.Videos.Video video = await _client.Videos.GetAsync(url, ct);

        string? quality = null;

        try
        {
            // Stream resolution is a nice-to-have; if it fails we still have
            // the rest of the metadata, so it must not break the preview.
            YouTubeProgressiveStream? progressive =
                await YouTubePlayback.GetProgressiveStreamAsync(_http, video.Id, ct);

            quality = progressive is { } stream
                ? $"{stream.QualityLabel} MP4"
                : Loc.Get(LocKeys.QualityAudio);
        }
        catch (Exception ex)
        {
            // Stream lookup is best-effort: a failure must not break the
            // preview, but it should be visible in debug builds.
            Debug.WriteLine($"Failed to fetch streams: {ex}");
        }

        return new MediaPreview
        {
            Title = video.Title,
            Author = video.Author.ChannelTitle,
            Description = video.Description,
            Duration = video.Duration,
            QualityText = quality
        };
    }

    public async Task<IReadOnlyList<ResourceDetail>> GetDetailsAsync(string url, CancellationToken ct = default)
    {
        YoutubeExplode.Videos.Video video = await _client.Videos.GetAsync(url, ct);
        List<ResourceDetail> details = new List<ResourceDetail>();
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsTitle), video.Title);
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsAuthor), video.Author.ChannelTitle);
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsDuration),
            video.Duration is { } duration ? MediaPreview.FormatDuration(duration) : null);
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsUploaded), video.UploadDate.ToString("d"));
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsViews),
            video.Engagement.ViewCount > 0 ? video.Engagement.ViewCount.ToString("N0") : null);
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsLikes),
            video.Engagement.LikeCount > 0 ? video.Engagement.LikeCount.ToString("N0") : null);
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsDescription), video.Description);
        ResourceDetail.AddIfPresent(details, Loc.Get(LocKeys.DetailsKeywords),
            video.Keywords is { Count: > 0 } keywords ? string.Join(", ", keywords) : null);
        return details;
    }

    public async Task<DownloadResult> DownloadAsync(
        string url, string outputPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        string? filePath = null;
        try
        {
            progress?.Report(new DownloadProgress(0, null, 0, Loc.Get(LocKeys.ProgressFetching)));

            YoutubeExplode.Videos.Video video = await _client.Videos.GetAsync(url, ct);

            string name = string.IsNullOrWhiteSpace(video.Title)
                ? video.Id
                : $"{video.Title} - {video.Author.ChannelTitle}";
            string sanitized = SanitizeFileName(name);
            if (string.IsNullOrWhiteSpace(sanitized))
                sanitized = video.Id;

            // YoutubeExplode's current clients expose only adaptive streams
            // (separate video and audio), so the muxed MP4 is read from the
            // Android player response. It is the only source of a ready-to-play
            // file without ffmpeg muxing.
            YouTubeProgressiveStream? progressive =
                await YouTubePlayback.GetProgressiveStreamAsync(_http, video.Id, ct);
            if (progressive is { } best)
            {
                filePath = Path.Combine(outputPath, $"{sanitized}.mp4");
                await DownloadProgressiveAsync(best.Url, filePath, video.Title, progress, ct);
                return new DownloadResult(true, filePath, null);
            }

            // No progressive stream (e.g. an audio-only video): fall back to
            // the adaptive streams of the regular manifest.
            StreamManifest manifest = await _client.Videos.Streams.GetManifestAsync(video.Id, ct);
            IStreamInfo? streamInfo = manifest.GetMuxedStreams()
                .Where(s => s.Container == Container.Mp4)
                .MaxBy(s => s.VideoQuality) as IStreamInfo
                ?? manifest.GetAudioOnlyStreams().FirstOrDefault();

            if (streamInfo == null)
                return new DownloadResult(false, null, Loc.Get(LocKeys.ErrNoStream));

            filePath = Path.Combine(outputPath, $"{sanitized}.{streamInfo.Container.Name}");
            Progress<double> fileProgress = new Progress<double>(p =>
                progress?.Report(new DownloadProgress(0, null, p, Loc.Get(LocKeys.ProgressDownloadingFile, video.Title))));

            await _client.Videos.Streams.DownloadAsync(streamInfo, filePath, fileProgress, ct);
            return new DownloadResult(true, filePath, null);
        }
        catch (OperationCanceledException ex)
        {
            TryDeleteFile(filePath);
            if (ct.IsCancellationRequested)
                return new DownloadResult(false, null, Loc.Get(LocKeys.ErrCancelled));

            // A timeout surfaces as TaskCanceledException, not a user
            // cancellation: report it as a network problem.
            AppLogger.Log(ex);
            return new DownloadResult(false, null, Loc.Get(LocKeys.ErrNetwork));
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            // A network error mid-download leaves a partial file behind; clean it up.
            TryDeleteFile(filePath);
            AppLogger.Log(ex);
            return new DownloadResult(false, null, Loc.Get(LocKeys.ErrNetwork));
        }
        catch (Exception ex)
        {
            // A disk error or any other unexpected failure mid-download leaves
            // a partial file behind; clean it up and show a generic message.
            TryDeleteFile(filePath);
            AppLogger.Log(ex);
            return new DownloadResult(false, null, Loc.Get(LocKeys.ErrPlatformUnavailable, PlatformName));
        }
    }

    private async Task DownloadProgressiveAsync(
        string streamUrl, string filePath, string title,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream fileStream = File.Create(filePath);

        byte[] buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            progress?.Report(new DownloadProgress(totalRead, totalBytes,
                totalBytes > 0 ? (double)totalRead / totalBytes : null,
                Loc.Get(LocKeys.ProgressDownloadingFile, title)));
        }
    }

    private static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or WebException or SocketException ||
        ex.InnerException is HttpRequestException or WebException or SocketException;

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is null)
                return;

            if (Directory.Exists(path))
                Directory.Delete(path, true);
            else
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeFileName(string name)
    {
        // Strip characters invalid on Windows and macOS, plus control
        // characters, then trim and clamp the length so the file name stays
        // valid on both platforms.
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        System.Text.StringBuilder builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        const int maxLength = 120;
        string sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized[..maxLength].TrimEnd('.', ' ');
    }
}
