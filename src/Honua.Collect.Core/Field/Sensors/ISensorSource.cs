// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public interface ISensorSource
{
    string SensorId { get; }

    SensorType Type { get; }

    SensorConnectionState State { get; }

    event EventHandler<SensorConnectionState>? StateChanged;

    event EventHandler<SensorReading>? ReadingReceived;

    IReadOnlyCollection<string> Channels { get; }

    SensorReading? Latest(string channel);

    IReadOnlyList<SensorReading> Window(string channel);
}
