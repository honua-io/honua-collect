// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public enum BufferOverflowPolicy
{
    DropOldest = 0,

    DropNewest = 1,
}

public sealed class SensorReadingBuffer
{
    private readonly object _gate = new();
    private readonly Queue<SensorReading> _window;
    private readonly int _capacity;
    private SensorReading? _latest;

    public SensorReadingBuffer(int capacity, BufferOverflowPolicy overflowPolicy = BufferOverflowPolicy.DropOldest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        OverflowPolicy = overflowPolicy;
        _window = new Queue<SensorReading>(capacity);
    }

    public int Capacity => _capacity;

    public BufferOverflowPolicy OverflowPolicy { get; }

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

    public long DroppedCount { get; private set; }

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

    public IReadOnlyList<SensorReading> Snapshot()
    {
        lock (_gate)
        {
            return _window.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _window.Clear();
            _latest = null;
        }
    }
}
