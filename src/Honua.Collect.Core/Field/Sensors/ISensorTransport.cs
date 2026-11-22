// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public interface ISensorTransport : IAsyncDisposable
{
    SensorConnectionState State { get; }

    event EventHandler<SensorConnectionState>? StateChanged;

    event EventHandler<SensorReading>? ReadingReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
