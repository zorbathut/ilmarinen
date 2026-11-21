# Compatibility register

Code kept only so the server can still talk to workers on older worker protocol versions (`WorkerProtocol`; see CLAUDE.md, "Protocol compatibility"). Each entry lives under the version that retires it: once `WorkerProtocol.MinSupportedVersion` reaches that version, no worker that needs the code can connect, so it goes, along with the test that sends it an older worker's message.

`CompatRegisterTests` fails once the minimum reaches a heading here, naming what to delete. Delete the code, its test and the entry, then the heading once it's empty.

An entry names the code and says what it does for older workers:

```
## Until v3

- `WorkerHub.ReportOutcome`: treats a missing `DiskUse` as unknown, for workers before v3. Test: `DiskUseTests.AnOutcomeWithoutDiskUse_IsStored`.
```

There are no entries yet: v1 is the first version.
