namespace SophonDownloader.Services;

public readonly record struct UnifiedTransferMetricsSnapshot(
    double SpeedBytesPerSecond, TimeSpan? Eta,
    long AvailableBytes,
    long RemainingBytes);

public sealed class UnifiedTransferMetrics
{
    private const double SpeedSmoothingFactor = 0.20;
    private const double EtaSmoothingFactor = 0.25;
    private const long PublishIntervalMilliseconds = 250;
    private const long SpeedWindowMilliseconds = 5000;
    private const long ResetAfterIdleMilliseconds = 3500;

    private readonly object _lock = new();
    private readonly Queue<(long Time, long Bytes)> _samples = new();

    private long _totalBytes;
    private long _lastPublishTime;
    private double _smoothedSpeed;
    private double? _smoothedEtaSeconds;
    private long _lastTransferredBytes;
    private long _lastMovementTime;

    public void Reset(long totalBytes)
    {
        lock (_lock)
        {
            _samples.Clear();
            _totalBytes = Math.Max(0, totalBytes);
            _lastPublishTime = Environment.TickCount64;
            _smoothedSpeed = 0;
            _smoothedEtaSeconds = null;
            _lastTransferredBytes = 0;
            _lastMovementTime = _lastPublishTime;
        }
    }

    public void SetTotalBytes(long totalBytes)
    {
        lock (_lock) _totalBytes = Math.Max(0, totalBytes);
    }

    public UnifiedTransferMetricsSnapshot Update(
        long availableBytes,
        long transferredBytes,
        bool force = false)
    {
        long now = Environment.TickCount64;

        lock (_lock)
        {
            availableBytes = Math.Clamp(availableBytes, 0, _totalBytes > 0 ? _totalBytes : long.MaxValue);
            transferredBytes = Math.Max(0, transferredBytes);

            bool shouldSample = force || now - _lastPublishTime >= PublishIntervalMilliseconds;
            if (shouldSample)
            {
                _samples.Enqueue((now, transferredBytes));
                while (_samples.Count > 1 && now - _samples.Peek().Time > SpeedWindowMilliseconds)
                    _samples.Dequeue();

                if (transferredBytes > _lastTransferredBytes)
                    _lastMovementTime = now;

                _lastTransferredBytes = transferredBytes;
                _lastPublishTime = now;

                if (_samples.Count >= 2)
                {
                    var oldest = _samples.Peek();
                    long elapsed = now - oldest.Time;
                    long delta = transferredBytes - oldest.Bytes;

                    if (elapsed >= 1000 && delta > 0)
                    {
                        double rawSpeed = delta * 1000d / elapsed;
                        if (IsFinitePositive(rawSpeed))
                        {
                            _smoothedSpeed = _smoothedSpeed <= 0
                                ? rawSpeed
                                : _smoothedSpeed * (1d - SpeedSmoothingFactor) + rawSpeed * SpeedSmoothingFactor;
                        }
                    }
                }
            }

            if (now - _lastMovementTime >= ResetAfterIdleMilliseconds)
                _smoothedSpeed = 0;

            long remainingBytes = _totalBytes > 0
                ? Math.Max(0, _totalBytes - availableBytes)
                : 0;

            TimeSpan? eta = CalculateEta(remainingBytes);

            return new UnifiedTransferMetricsSnapshot(
                Math.Max(0, _smoothedSpeed),
                eta, availableBytes, remainingBytes);
        }
    }

    private TimeSpan? CalculateEta(long remainingBytes)
    {
        if (remainingBytes <= 0)
        {
            _smoothedEtaSeconds = 0;
            return TimeSpan.Zero;
        }

        if (_smoothedSpeed <= 0 || !IsFinitePositive(_smoothedSpeed))
        {
            _smoothedEtaSeconds = null;
            return null;
        }

        double rawEtaSeconds = remainingBytes / _smoothedSpeed;
        if (!IsFinitePositive(rawEtaSeconds))
            return null;

        _smoothedEtaSeconds = !_smoothedEtaSeconds.HasValue
            ? rawEtaSeconds
            : _smoothedEtaSeconds.Value * (1d - EtaSmoothingFactor) + rawEtaSeconds * EtaSmoothingFactor;

        double clamped = Math.Min(_smoothedEtaSeconds.Value, TimeSpan.MaxValue.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, clamped));
    }

    private static bool IsFinitePositive(double value) =>
        value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}
