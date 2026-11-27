# Installed iOS upgrade against Honua Server on Device Farm

Use an authorized AWS Device Farm project with the custom macOS Tahoe iPhone simulator lane in us-west-2. The host and the simulator share loopback. The test starts its own PostgreSQL and Server there, and removes the preinstalled copy of Field from the disposable simulator before installing the historical build. It stops local services through the existing published-iOS gate and detaches the PostgreSQL disk image on exit.

Download `aws-ios-simulator-apps` from [the pinned build](https://github.com/honua-io/honua-field/actions/runs/45980545095). The workflow builds baseline `6e3db0b0c48c63d1bdb6fd1d7f00c2030dc33c46` and candidate `c6551c28995ed4f15cd9db92ec5df95938ae0da4`, then records each archive SHA-256. Supply the checksummed self-contained macOS arm64 Server archive built from `ea127fc0916f2bce70596deb7e4f11784ce9f7d1`. Its top-level directory must be `published-server/` and contain the executable `Honua.Server`. The archive and the seed must come from the same revision.

```bash
scripts/e2e/aws-ios-live/stage.sh /tmp/field-aws-live /path/to/app-artifact \
  /path/to/honua-server /path/to/server.tar.gz /path/to/server.sha256
cd /tmp/field-aws-live
HONUA_DEVICEFARM_PROJECT_FILE=/private/path/to/project-arn python3 schedule.py
```

The uploader prints the run ARN and stores it in `runarn`. Device Farm's artifact list contains the existing gate report under `published-ios`. Check `input-digests.txt` and the gate's result, final feature readback, and attachment hash. The gate forms a real outbox record in the old build, updates the installed app in place, publishes a newer workbook version, and sends the queued record under its original version. Passing requires the expected server-side record and its attachment bytes exactly once. The worker key and database credential are generated for the disposable fixture.
