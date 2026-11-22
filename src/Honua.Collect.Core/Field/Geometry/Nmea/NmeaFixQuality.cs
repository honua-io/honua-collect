// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Geometry.Nmea;

public enum NmeaFixQuality
{
    None = 0,

    Autonomous = 1,

    Differential = 2,

    PpsFix = 3,

    RtkFixed = 4,

    RtkFloat = 5,

    Estimated = 6,

    Manual = 7,

    Simulation = 8,
}
