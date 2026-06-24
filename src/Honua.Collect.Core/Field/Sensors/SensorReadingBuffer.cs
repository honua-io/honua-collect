namespace Honua.Collect.Core.Field.Sensors;

/// <summary>What a <see cref="SensorReadingBuffer"/> does when a full window overflows.</summary>
public enum BufferOverflowPolicy
{
    /// <summary>Drop the oldest reading to make room for the newest (rolling window).</summary>
    DropOldest = 0,

    /// <summary>Drop the incoming reading and keep the existing window (backpressure / reject).</summary>
    DropNewest = 1,
}

/// <summary>
/// A bounded, thread-safe ingestion buffer for one sensor channel (BACKLOG I3).
/// It keeps the latest value and a rolling window of the most recent readings;
/// when the window is full it applies a <see cref="BufferOverflowPolicy"/> so a
/// fast device can never grow memory without bound. This is the real verifiable
/// value of the IoT-streaming work: the radio just feeds <see cref="Add"/>.
/// </summary>
public sealed class SensorReadingBuffer
{
    private readonly object _gate = new();
    private readonly Queue<SensorReading> _window;
    private readonly int _capacity;
    private SensorReading? _latest;

    /// <summary>Creates a buffer with a fixed window capacity.</summary>
    /// <param name="capacity">Maximum readings retained in the rolling window (&gt; 0).</param>
    /// <param name="overflowPolicy">Behaviour when a full window receives another reading.</param>
    public SensorReadingBuffer(int capacity, BufferOverflowPolicy overflowPolicy = BufferOverflowPolicy.DropOldest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        OverflowPolicy = overflowPolicy;
        _window = new Queue<SensorReading>(capacity);
    }

    /// <summary>Maximum number of readings retained in the rolling window.</summary>
    public int Capacity => _capacity;

    /// <summary>The overflow policy applied when the window is full.</summary>
    public BufferOverflowPolicy OverflowPolicy { get; }

    /// <summary>Number of readings currently in the window.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _window.Count;
            }
        }
    }

    /// <summary>
    /// Total readings dropped due to overflow since construction (under
    /// <see cref="BufferOverflowPolicy.DropOldest"/> this counts evicted oldest
    /// readings; under <see cref="BufferOverflowPolicy.DropNewest"/>, rejected
    /// incoming readings).
    /// </summary>
    public long DroppedCount { get; private set; }

    /// <summary>The most recent reading added, or <see langword="null"/> if none.</summary>
    public SensorReading? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>
    /// Adds a reading. Always updates <see cref="Latest"/> when accepted into the
    /// window; under backpressure (<see cref="BufferOverflowPolicy.DropNewest"/>
    /// on a full window) the reading is rejected and neither the window nor the
    /// latest value changes.
    /// </summary>
    /// <param name="reading">The reading to ingest.</param>
    /// <returns><see langword="true"/> if accepted into the window.</returns>
    public bool Add(SensorReading reading)
    {
        lock (_gate)
        {
            if (_window.Count >= _capacity)
            {
                if (OverflowPolicy == BufferOverflowPolicy.DropNewest)
                {
                    DroppedCount++;
                    return false;
                }

                _window.Dequeue();
                DroppedCount++;
            }

            _window.Enqueue(reading);
            _latest = reading;
            return true;
        }
    }

    /// <summary>Takes a stable snapshot of the current window in oldest-to-newest order.</summary>
    /// <returns>The buffered readings.</returns>
    public IReadOnlyList<SensorReading> Snapshot()
    {
        lock (_gate)
        {
            return _window.ToArray();
        }
    }

    /// <summary>Clears the window and the latest value (drop counters are retained).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _window.Clear();
            _latest = null;
        }
    }
}
