// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Forms.Localization;

public interface ILocaleStore
{
    Task SetActiveLanguageAsync(string formId, string language, CancellationToken ct = default);

    Task<string?> GetActiveLanguageAsync(string formId, CancellationToken ct = default);
}
