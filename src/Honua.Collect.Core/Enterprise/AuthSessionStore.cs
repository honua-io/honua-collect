// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Enterprise;

public interface IAuthSessionStore
{
    AuthSession? Current { get; }

    string? FallbackApiKey { get; }

    void Set(AuthSession? session);

    event EventHandler? Changed;
}

public sealed class AuthSessionStore : IAuthSessionStore
{
    private readonly object _gate = new();
    private AuthSession? _session;

    public AuthSessionStore(string? fallbackApiKey = null) => FallbackApiKey = fallbackApiKey;

    public AuthSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    public string? FallbackApiKey { get; }

    public void Set(AuthSession? session)
    {
        lock (_gate)
        {
            _session = session;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
}
