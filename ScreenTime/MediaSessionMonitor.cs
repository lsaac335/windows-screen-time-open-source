using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Windows.Media.Control;

namespace ScreenTime;

/// <summary>Playback state and application identity only; no titles, covers or timeline polling.</summary>
internal sealed class MediaSessionMonitor : IMediaSessionMonitor
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Action<HashSet<string>> _changed;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();
    private bool _enabled, _disposed, _queued;
    private int _generation;
    public string Status { get; private set; } = "off";
    public MediaSessionMonitor(Action<HashSet<string>> changed) => _changed = changed;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IMediaSessionMonitor Create(Action<HashSet<string>> changed) => new MediaSessionMonitor(changed);
    public async void SetEnabled(bool enabled)
    {
        int generation = ++_generation;
        Detach(); _enabled = enabled;
        _changed(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Status = enabled ? "initializing" : "off";
        if (!enabled || _disposed) return;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_enabled || generation != _generation) return;
                _manager = manager;
                _manager.SessionsChanged += SessionsChanged;
                Rebuild();
            });
        }
        catch (Exception) { if (!_disposed && generation == _generation) Status = "unavailable"; }
    }
    private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => QueueRefresh();
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || !_enabled || _queued) return;
            _queued = true;
            _dispatcher.BeginInvoke(new Action(() => { _queued = false; if (!_disposed && _enabled) Rebuild(); }), DispatcherPriority.Background);
        }));
    }
    private void Rebuild()
    {
        try
        {
            foreach (var session in _sessions) session.PlaybackInfoChanged -= PlaybackChanged;
            _sessions.Clear();
            if (_manager == null) return;
            var playing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in _manager.GetSessions().Take(32))
            {
                _sessions.Add(session); session.PlaybackInfoChanged += PlaybackChanged;
                if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    playing.Add(session.SourceAppUserModelId);
            }
            Status = "ready; sessions=" + _sessions.Count + "; playing=" + playing.Count;
            _changed(playing);
        }
        catch (Exception) { Status = "unavailable"; _changed(new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
    }
    private void Detach()
    {
        if (_manager != null) _manager.SessionsChanged -= SessionsChanged;
        foreach (var session in _sessions) session.PlaybackInfoChanged -= PlaybackChanged;
        _sessions.Clear(); _manager = null;
    }
    public void Dispose() { _disposed = true; _enabled = false; ++_generation; Detach(); }
}
