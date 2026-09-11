namespace Emberloom.Api;

/// <summary>
/// Deep-lane calls per minute, measured from the calls this machine actually served.
/// <para>
/// The Contemplation readout used to take this from the M0 benchmark, and the benchmark's call
/// shapes were smaller than a live reflection batch. On the laptop it said 4.8 calls a minute;
/// live reflections at M4 averaged ~54 s each, about 1.1. A readout four times too generous
/// promises thinking the machine cannot deliver, which is the one thing it exists not to do.
/// </para>
/// <para>
/// A rolling window rather than a lifetime average, so the figure follows the machine as it is now:
/// a model that got warm, a remote endpoint that got busy.
/// </para>
/// </summary>
public sealed class ThroughputMeter
{
    private readonly Queue<double> _seconds = new();
    private readonly object _gate = new();
    private readonly int _window;
    private readonly int _minimumSamples;

    public ThroughputMeter(int window = 12, int minimumSamples = 3)
    {
        _window = window;
        _minimumSamples = minimumSamples;
    }

    public void Record(TimeSpan duration)
    {
        lock (_gate)
        {
            _seconds.Enqueue(duration.TotalSeconds);

            while (_seconds.Count > _window)
            {
                _seconds.Dequeue();
            }
        }
    }

    /// <summary>Measured calls per minute, or null until enough calls have been served to say.</summary>
    public double? CallsPerMinute
    {
        get
        {
            lock (_gate)
            {
                if (_seconds.Count < _minimumSamples)
                {
                    return null;
                }

                var average = _seconds.Average();
                return average > 0 ? 60.0 / average : null;
            }
        }
    }
}
