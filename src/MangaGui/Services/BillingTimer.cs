using MangaGui.Models;

namespace MangaGui.Services;

/// <summary>
/// Snapshot computed from wall-clock difference (UtcNow - StartedAt), immune to OS sleep/hibernation drift.
/// </summary>
public sealed record BillingSnapshot(
    bool IsRunning,
    TimeSpan Elapsed,
    int? PlannedMinutes,
    bool AutoStop,
    bool StartTimeUnknown,
    double EstimatedCu,
    double ProgressRatio,
    TimeSpan? Remaining,
    BillingSeverity Severity,
    DateTimeOffset? DeadlineUtc = null,
    double CuPerHour = BillingConstants.L4CuPerHour);

/// <summary>
/// Tracks Colab GPU billing using a configurable hourly CU rate (defaulting to L4) and wall-clock timestamps,
/// triggering 5-minute snooze warnings and time-limit expiry notifications.
/// </summary>
public sealed class BillingTimer
{
    public const double L4CuPerHour = BillingConstants.L4CuPerHour;
    public static readonly TimeSpan WarningLeadTime = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();

    private bool _isRunning;
    private DateTimeOffset _startedAt;
    private int? _plannedMinutes;
    private bool _autoStop = true;
    private bool _startTimeUnknown;
    private double _cuPerHour = BillingConstants.L4CuPerHour;
    private bool _warningFired;
    private bool _expiryFired;
    private DateTimeOffset _expiryNotBeforeUtc = DateTimeOffset.MinValue;

    public event EventHandler<BillingSnapshot>? FiveMinuteWarningTriggered;
    public event EventHandler<BillingSnapshot>? TimeLimitReached;

    public double CurrentCuPerHour
    {
        get { lock (_sync) return _cuPerHour; }
    }

    public BillingTimer(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void Start(
        DateTimeOffset startedAt,
        int? plannedMinutes,
        bool autoStop,
        bool startTimeUnknown = false,
        double? cuPerHour = null)
    {
        lock (_sync)
        {
            _isRunning = true;
            _startedAt = startedAt;
            _plannedMinutes = plannedMinutes;
            _autoStop = autoStop;
            _startTimeUnknown = startTimeUnknown;
            _cuPerHour = cuPerHour is > 0 ? cuPerHour.Value : BillingConstants.L4CuPerHour;
            _warningFired = false;
            _expiryFired = false;
            _expiryNotBeforeUtc = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Updates the live hourly CU rate (e.g. after reading real consumptionRateHourly from Colab).
    /// </summary>
    public void UpdateCuPerHour(double cuPerHour)
    {
        if (cuPerHour <= 0 || double.IsNaN(cuPerHour) || double.IsInfinity(cuPerHour))
        {
            return;
        }

        lock (_sync)
        {
            _cuPerHour = cuPerHour;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _isRunning = false;
            _plannedMinutes = null;
            _cuPerHour = BillingConstants.L4CuPerHour;
            _warningFired = false;
            _expiryFired = false;
            _expiryNotBeforeUtc = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Re-arms the expiry notification so <see cref="TimeLimitReached"/> fires again once
    /// <paramref name="delay"/> has passed, if the session is still over its limit by then.
    /// </summary>
    public void SnoozeExpiry(TimeSpan delay)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                return;
            }

            _expiryFired = false;
            _expiryNotBeforeUtc = _utcNow() + delay;
        }
    }

    /// <summary>
    /// Extends the planned runtime by extraMinutes (default 30 min) from whichever is later:
    /// the existing deadline or current elapsed time.
    /// </summary>
    public SessionRecord? Extend(int extraMinutes = 30)
    {
        lock (_sync)
        {
            if (!_isRunning)
            {
                return null;
            }

            var elapsedMinutes = Math.Max(0, (_utcNow() - _startedAt).TotalMinutes);
            var currentLimit = _plannedMinutes ?? (int)Math.Ceiling(elapsedMinutes);
            var baseMinutes = Math.Max(currentLimit, (int)Math.Ceiling(elapsedMinutes));
            _plannedMinutes = baseMinutes + extraMinutes;
            _warningFired = false;
            _expiryFired = false;
            _expiryNotBeforeUtc = DateTimeOffset.MinValue;

            return CreateRecordUnsafe();
        }
    }

    public SessionRecord? GetCurrentRecord()
    {
        lock (_sync)
        {
            return _isRunning ? CreateRecordUnsafe() : null;
        }
    }

    public BillingSnapshot Evaluate(bool fireEvents = true)
    {
        BillingSnapshot snapshot;
        bool shouldWarn = false;
        bool shouldExpire = false;

        lock (_sync)
        {
            if (!_isRunning)
            {
                return new BillingSnapshot(
                    IsRunning: false,
                    Elapsed: TimeSpan.Zero,
                    PlannedMinutes: null,
                    AutoStop: true,
                    StartTimeUnknown: false,
                    EstimatedCu: 0,
                    ProgressRatio: 0,
                    Remaining: null,
                    Severity: BillingSeverity.Normal,
                    DeadlineUtc: null,
                    CuPerHour: _cuPerHour);
            }

            var elapsed = _utcNow() - _startedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            var cu = elapsed.TotalHours * _cuPerHour;
            TimeSpan? remaining = null;
            DateTimeOffset? deadlineUtc = null;
            double progress = 0;
            BillingSeverity severity = BillingSeverity.Normal;

            if (_plannedMinutes is int planned && planned > 0)
            {
                var plannedSpan = TimeSpan.FromMinutes(planned);
                deadlineUtc = _startedAt + plannedSpan;
                remaining = plannedSpan - elapsed;
                progress = Math.Clamp(elapsed.TotalSeconds / plannedSpan.TotalSeconds, 0, 1);

                if (remaining <= TimeSpan.Zero)
                {
                    severity = BillingSeverity.Danger;
                    if (fireEvents && !_expiryFired && _utcNow() >= _expiryNotBeforeUtc)
                    {
                        _expiryFired = true;
                        shouldExpire = true;
                    }
                }
                else if (remaining <= WarningLeadTime)
                {
                    severity = BillingSeverity.Warning;
                    if (fireEvents && !_warningFired)
                    {
                        _warningFired = true;
                        shouldWarn = true;
                    }
                }
            }
            else
            {
                // Unlimited mode: turn amber after 1h, danger crimson after 2h.
                if (elapsed >= TimeSpan.FromHours(2))
                {
                    severity = BillingSeverity.Danger;
                }
                else if (elapsed >= TimeSpan.FromHours(1))
                {
                    severity = BillingSeverity.Warning;
                }
            }

            snapshot = new BillingSnapshot(
                IsRunning: true,
                Elapsed: elapsed,
                PlannedMinutes: _plannedMinutes,
                AutoStop: _autoStop,
                StartTimeUnknown: _startTimeUnknown,
                EstimatedCu: cu,
                ProgressRatio: progress,
                Remaining: remaining,
                Severity: severity,
                DeadlineUtc: deadlineUtc,
                CuPerHour: _cuPerHour);
        }

        if (shouldWarn)
        {
            FiveMinuteWarningTriggered?.Invoke(this, snapshot);
        }

        if (shouldExpire)
        {
            TimeLimitReached?.Invoke(this, snapshot);
        }

        return snapshot;
    }

    private SessionRecord CreateRecordUnsafe() => new()
    {
        SessionName = VmScripts.SessionName,
        StartedAt = _startedAt,
        PlannedMinutes = _plannedMinutes,
        AutoStop = _autoStop,
        StartTimeUnknown = _startTimeUnknown,
        BillingCuPerHour = _cuPerHour
    };
}
