/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

/// <summary>
/// In-app guided tour for first-launch users. Walks the canonical
/// <see cref="MarqueeRoutes.Default"/> stops in order, driving Blazor's
/// <see cref="NavigationManager"/>. The companion
/// <c>Components/Shared/WelcomeTourOverlay.razor</c> subscribes to
/// <see cref="StateChanged"/> and renders the narration card.
/// <para>
/// Auto-advance is on by default with a configurable hold time; user can
/// toggle to manual via the overlay, and use Prev/Next/Skip controls.
/// </para>
/// </summary>
public sealed class WelcomeTourService : IDisposable
{
    private readonly NavigationManager _nav;
    private readonly ILogger<WelcomeTourService> _logger;

    private CancellationTokenSource? _autoAdvanceCts;
    private int _currentIndex = -1;       // -1 = inactive
    private bool _autoAdvance = true;
    private int _holdMs = 8000;           // generous default — read-and-look pace, not GIF-capture pace

    public WelcomeTourService(NavigationManager nav, ILogger<WelcomeTourService> logger)
    {
        _nav = nav;
        _logger = logger;
    }

    /// <summary>Fires whenever the active stop, paused-state, or active flag changes.</summary>
    public event Action? StateChanged;

    public bool IsActive => _currentIndex >= 0;
    public int CurrentIndex => _currentIndex;
    public int TotalStops => MarqueeRoutes.Default.Count;
    public MarqueeStop? CurrentStop =>
        _currentIndex >= 0 && _currentIndex < MarqueeRoutes.Default.Count
            ? MarqueeRoutes.Default[_currentIndex]
            : null;
    public bool AutoAdvance => _autoAdvance;
    public int HoldMs => _holdMs;

    public void Start(int holdMs = 8000, bool autoAdvance = true)
    {
        _holdMs = Math.Max(1000, holdMs);
        _autoAdvance = autoAdvance;
        _currentIndex = 0;
        _logger.LogInformation("WelcomeTour: starting, holdMs={Hold}, autoAdvance={Auto}", _holdMs, _autoAdvance);
        GoToStop(_currentIndex);
    }

    public void Stop()
    {
        if (!IsActive) return;
        _logger.LogInformation("WelcomeTour: stopped at stop {Index}", _currentIndex);
        CancelAutoAdvance();
        _currentIndex = -1;
        StateChanged?.Invoke();
    }

    public void Next()
    {
        if (!IsActive) return;
        if (_currentIndex >= MarqueeRoutes.Default.Count - 1)
        {
            Stop();   // last stop — finishing the tour
            return;
        }
        GoToStop(_currentIndex + 1);
    }

    public void Previous()
    {
        if (!IsActive || _currentIndex <= 0) return;
        GoToStop(_currentIndex - 1);
    }

    public void ToggleAutoAdvance()
    {
        _autoAdvance = !_autoAdvance;
        if (IsActive)
        {
            if (_autoAdvance) RestartAutoAdvance();
            else CancelAutoAdvance();
        }
        StateChanged?.Invoke();
    }

    private void GoToStop(int index)
    {
        _currentIndex = index;
        var stop = MarqueeRoutes.Default[index];
        // NavigateTo with forceLoad: false keeps the SPA circuit alive.
        _nav.NavigateTo(stop.Route);
        StateChanged?.Invoke();
        if (_autoAdvance) RestartAutoAdvance();
    }

    private void RestartAutoAdvance()
    {
        CancelAutoAdvance();
        _autoAdvanceCts = new CancellationTokenSource();
        var token = _autoAdvanceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_holdMs, token);
                if (!token.IsCancellationRequested && IsActive)
                {
                    // Marshal back via a 0-delay continuation; the StateChanged
                    // handlers run on Blazor's sync context already.
                    Next();
                }
            }
            catch (OperationCanceledException) { /* expected on Stop / manual nav */ }
        });
    }

    private void CancelAutoAdvance()
    {
        _autoAdvanceCts?.Cancel();
        _autoAdvanceCts?.Dispose();
        _autoAdvanceCts = null;
    }

    public void Dispose()
    {
        CancelAutoAdvance();
    }
}
