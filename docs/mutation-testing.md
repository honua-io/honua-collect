# Mutation testing (Stryker.NET)

Line/branch coverage proves a line *ran*; it does not prove a test would *fail*
if the line were wrong. Mutation testing closes that gap: [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/)
rewrites small pieces of the production code (flip a `&&` to `||`, a `>=` to `>`,
a string to empty, …) and re-runs the tests. A mutant that the tests still pass
("survived") is a place where the assertions are too weak. The **mutation score**
is `killed / (killed + survived)`.

We mutate `src/Honua.Collect.Core` and run the Core test project against it.

## Run it

The config lives in `tests/Honua.Collect.Core.Tests/stryker-config.json`, so run
from that directory:

```bash
dotnet tool install dotnet-stryker --tool-path tools   # once
cd tests/Honua.Collect.Core.Tests
../../tools/dotnet-stryker
```

The full Core run is large; for a fast, focused signal restrict the mutated set
to one folder (this is how the Sync subset below was measured):

```bash
cd tests/Honua.Collect.Core.Tests
../../tools/dotnet-stryker --mutate "**/Sync/**/*.cs"
```

An HTML report is written under `StrykerOutput/<timestamp>/reports/`.

## Current score

Focused run over `Sync/` (Stryker 4.14.2): **78.1%** mutation score
(375 mutants tested; 295 killed, ~66 survived, a handful timed out). Several
survivors are *equivalent* mutants — e.g. exponential-backoff jitter timing and
short-circuit `||`/`&&` on `JsonElement.TryGetProperty` guards — where the
mutated program is observably identical, so they cannot be killed without
asserting on non-deterministic timing.

Survivors that were *not* equivalent have been killed by strengthening the
assertions:

- `ResumableUpload` chunk-index bounds — added exact `index == ChunkCount`
  boundary tests for `ChunkAt`/`MarkUploaded`/`Resume`.
- `RecordConflictDetector` one-sided values — added tests asserting that a value
  present on only the local or only the server side is reported as a conflict.

## CI

The `mutation-test` job in `.github/workflows/ci.yml` runs Stryker on every PR.
It is **non-blocking** (`continue-on-error: true`): it informs reviewers of the
assertion strength without gating the merge, because a portion of the surviving
mutants are equivalent and a hard gate would flap.
