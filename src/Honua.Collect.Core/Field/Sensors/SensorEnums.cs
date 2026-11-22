// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public enum SensorQuality
{
    Good = 0,

    Uncertain = 1,

    Bad = 2,
}

public enum SensorConnectionState
{
    Disconnected = 0,

    Connecting = 1,

    Connected = 2,

    Faulted = 3,
}

public enum SensorType
{
    Unknown = 0,

    Environmental = 1,

    Positioning = 2,

    Rangefinder = 3,

    Meter = 4,

    Industrial = 5,
}
