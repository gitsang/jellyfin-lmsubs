using System.Collections.Concurrent;
using Jellyfin.Plugin.LmSubs.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using Jellyfin.Data.Events;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LmSubs.Services;

/// <summary>
/// Watches playback sessions. When a user selects an LM subtitle track (reported by
/// every Jellyfin client via PlaybackStart / PlaybackProgress), this kicks off the
/// ASR + translation pipeline prioritized at the current playback position — the
/// "select the empty subtitle → it starts filling itself" trigger, with no web
/// modification. Also installs the LM tracks lazily on first playback of any video.
/// </summary>
public sealed class LmPlaybackMonitor : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IItemRepository _itemRepo;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly SubtitleService _service;
    private readonly ILogger<LmPlaybackMonitor> _logger;

    // live mode: re-trigger when the position jumps (seek) or after a stale period
    private readonly ConcurrentDictionary<Guid, (double Position, DateTime TriggeredUtc)> _liveTriggers = new();

    public LmPlaybackMonitor(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        IItemRepository itemRepo,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        SubtitleService service,
        ILogger<LmPlaybackMonitor> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _itemRepo = itemRepo;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _service = service;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _providerManager.RefreshCompleted += OnRefreshCompleted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _providerManager.RefreshCompleted -= OnRefreshCompleted;
        return Task.CompletedTask;
    }

    private void OnRefreshCompleted(object? sender, GenericEventArgs<BaseItem> e)
    {
        // Library scans rebuild MediaStreams from disk and wipe our injected tracks;
        // put them back after every refresh.
        try
        {
            if (e.Argument is Video video && Plugin.Instance != null)
            {
                LmTrackInstaller.EnsureTracks(video, _itemRepo, _service.Store, Plugin.Instance.Configuration.TargetLanguage, _logger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LMSubs: re-install after refresh failed for {Item}", e.Argument?.Name);
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Handle(e.Item, e.PlaybackPositionTicks, null);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => Handle(e.Item, e.PlaybackPositionTicks, null);

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason is UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress or UserDataSaveReason.UpdateUserData)
        {
            Handle(e.Item, e.UserData?.PlaybackPositionTicks, e.UserData?.SubtitleStreamIndex);
        }
    }

    private void Handle(BaseItem? item, long? positionTicks, int? subtitleStreamIndex)
    {
        try
        {
            if (item is not Video video || Plugin.Instance == null)
            {
                return;
            }

            var config = Plugin.Instance.Configuration;

            // Lazily install LM tracks on first playback so every video gets them
            // without a full library rescan.
            LmTrackInstaller.EnsureTracks(video, _itemRepo, _service.Store, config.TargetLanguage, _logger);

            if (!LmTrackInstaller.TryIdentify(_itemRepo, video.Id, subtitleStreamIndex, out var live))
            {
                return;
            }

            var position = positionTicks.HasValue ? TimeSpan.FromTicks(positionTicks.Value).TotalSeconds : 0;
            _service.ReportPlaybackPosition(video.Id, position);

            if (!live)
            {
                // cached mode: resume/complete only what's missing; no-op when done
                _service.EnsureRunning(video.Id, position, force: false);
                return;
            }

            // live mode: trigger on selection, on seeks (>20s jump), and periodically
            // while playing so the file keeps extending ahead of the position.
            var now = DateTime.UtcNow;
            var shouldTrigger = true;
            if (_liveTriggers.TryGetValue(video.Id, out var last))
            {
                var jumped = Math.Abs(position - last.Position) > 20;
                var stale = (now - last.TriggeredUtc).TotalSeconds > 60;
                shouldTrigger = jumped || stale;
            }

            if (shouldTrigger)
            {
                _liveTriggers[video.Id] = (position, now);
                _service.EnsureRunning(video.Id, position, force: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LMSubs: playback monitor failed for {Item}", item?.Name);
        }
    }
}
