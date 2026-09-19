# Workflow regression tests

WFE.Test is an xUnit suite covering Engine, Consumer, and the shared protocol reader. It references the sibling HaleyFlow.Consumer and HaleyAbstractions.Core checkouts through the existing project layout.

## Run without a database

`dotnet test WFE.Test/WFE.Test.csproj -m:1 -nr:false`

Protocol and queue tests run normally. Database tests are explicitly skipped when HALEYFLOW_TEST_CONNECTION is absent.

## Run the complete suite

On Windows with MariaDB installed, run from the Engine repository:

`pwsh -File WFE.Test/run-integration.ps1 -MariaDbBin "path/to/MariaDB/bin"`

The script initializes a disposable MariaDB data directory under ignored .buildverify, starts a server bound to loopback on a free port with a generated password, runs the tests, and shuts that server down. It neither connects to nor migrates an existing database. Logs and TRX output stay under the generated test directory.

Alternatively set HALEYFLOW_TEST_CONNECTION to an isolated loopback MariaDB server, then run dotnet test. The account must be able to create and drop databases. Each fixture creates a uniquely named haleyflow_test_* database and drops only that database on disposal. Never point these tests at an application database.

## Coverage

- ProtocolTests: selected-rule fallback, completion isolation, gate/effect type inheritance, valid order boundaries and unordered-last behavior.
- InstanceDispatchQueueTests: phase serialization, same-phase parallel work, different instances, and recovery after task failure.
- ExecutionRegressionTests: self-loops, receipt replay, concurrent requests, all-consumer validation, hook phase barriers, per-hook any ACKs, immediate/retry parity, recovery after interrupted hook/Complete creation, import validation, and in-process consumer isolation.
- RecoveryAndImportTests: timeout failure and concurrent retries, legacy timeout markers, effect expiry after resends, failure routing, graph backfill, transaction rollback, duplicate import, and rerunnable schema upgrade.
- ConsumerOutboxTests: failure before continuation, lost response after continuation, durable result reuse, and Processed-only continuation.

Database tests execute canonical Engine and Consumer schemas and the actual upgrade script. Injected database failures are confined to each disposable fixture. Assertions check persisted outcomes, not only method calls.

These are focused regression tests. The full scenario checklist in docs/TEST_CHECKLIST.md includes additional manual and future coverage.
