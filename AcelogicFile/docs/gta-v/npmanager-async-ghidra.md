# Gen5 NpManager async-request lifecycle evidence

## Evidence identity

- Ghidra project: `sharpemu_npmanager_dev`
- Program: `libSceNpManager.sprx`
- SHA-256: `2c192ec03cf2b3a8acc24c2aefd98ef8232c2bdbdf2f678deb980397ed48cb72`
- Analysis mode: KawaiiDRA, `save:false`
- Evidence source: this firmware binary only

## Export identities and ABI

| NID | Export | Address | ABI |
|---|---|---:|---|
| `eiqMCt9UshI` | `sceNpCreateAsyncRequest` | `0x171d0` | `int32(const Parameter *parameter)` |
| `S7QTn72PrDw` | `sceNpDeleteRequest` | `0x17220` | `int32(int32 requestId)` |
| `OzKvTvg3ZYU` | `sceNpAbortRequest` | `0x17250` | `int32(int32 requestId)` |
| `uqcPJLWL08M` | `sceNpPollAsync` | `0x17480` | `int32(int32 requestId, int32 *result)` |
| `KfGZg2y73oM` | `sceNpCheckNpReachability` | `0x18660` | `int32(int32 requestId, int32 userId)` |
| `hw5KNqAAels` | `sceNpRegisterNpReachabilityStateCallback` | `0x163a0` | `int32(callback, void *userData)` |

The create parameter is exactly 0x18 bytes. Firmware reads:

- `+0x00`: `uint64 size`, must equal `0x18`.
- `+0x08`: 64-bit value copied into request `+0x68`; the async thread-create call consumes it as the affinity-like argument.
- `+0x10`: low 32 bits copied into request `+0x60`; the async thread-create call consumes it as the priority-like argument.

The priority/affinity names are inferred from their positions in the thread-create call; their widths and flow are direct evidence.

## Exact return behavior and precedence

### Create

1. SDK/request subsystem not initialized: `0x80550002`.
2. Null parameter: `0x80550003`.
3. `parameter->size != 0x18`: `0x80550011`.
4. 0x90-byte request allocation failure: `0x80550005`.
5. Registry capacity 32 or no ID below/equal to `0x2fffffff`: `0x80550013`.
6. Success: a positive request ID.

IDs are kept in ascending order. Allocation scans from a sentinel whose ID is zero and chooses the smallest missing positive ID. The hard maximum is `0x2fffffff`; at most 32 live requests are accepted.

### Abort

1. Not initialized: `0x80550002`.
2. `requestId <= 0`: `0x80550003`.
3. ID absent from the registry: `0x80550014`.
4. Found: sets abort-requested at record `+0x10`, invokes every nonzero backend cancellation handle, ignores cancellation return values, and returns zero.

Public abort passes zero as the helper's second argument, so it does not set the separate shutdown-abort flag at `+0x14`. Abort does not itself change completion state or the stored result.

### Delete

1. Not initialized: `0x80550002`.
2. `requestId <= 0`: `0x80550003`.
3. ID absent: `0x80550014`.
4. Found: retains the record, calls the same abort helper, unlinks it under the registry lock, marks it deleted, decrements the live count without going below zero, and destroys it only after references drain.
5. If no worker handle exists, returns zero. If one exists, returns the result of the worker-wait/join-like imported helper called with `(workerHandle, 0)`.

The current SharpEmu unconditional-success delete body is therefore not firmware-equivalent.

### Poll

1. Not initialized: `0x80550002`; this wins over a null output pointer.
2. Null output pointer: `0x80550003`.
3. `requestId <= 0`: `0x80550003`.
4. ID absent: `0x80550014`.
5. Request kind at `+0x5c` is zero: `0x80550015`.
6. State zero (created but no operation started): an imported environment/version query is made. A successful query returning at most `0x01ffffff` produces `0x80550015`; a failed query or a value above that threshold produces `1`.
7. State one (running), or any nonzero state other than two: returns `1`.
8. State two (complete): reads the stored result at `+0x58`, optionally translates selected service errors using the queried environment/version, writes exactly one 32-bit result to the guest output, and returns zero.

The guest output is not written on nonzero poll returns. If the version query fails on the complete path, firmware writes the raw stored result and still returns zero.

## Request record and state machine

The allocated record is 0x90 bytes. Fields relevant to these exports are:

| Offset | Evidence-backed role |
|---:|---|
| `0x08` | registry-owned reference count |
| `0x0c` | deleted/unlinked flag |
| `0x10` | public abort requested |
| `0x14` | shutdown/forced abort requested |
| `0x18` | an operation has already been assigned |
| `0x1c` | positive request ID |
| `0x34,0x38,0x3c,0x40` | mutually independent backend cancellation handles |
| `0x48` | pointer to the shared request-state mutex |
| `0x50` | worker-thread handle |
| `0x58` | completed operation result |
| `0x5c` | request kind; create-async stores `1` |
| `0x60` | 32-bit thread priority-like parameter |
| `0x68` | 64-bit thread affinity-like parameter |
| `0x70` | backend job descriptor |
| `0x78` | state: `0` unstarted, `1` running, `2` complete |
| `0x80` | next registry entry |
| `0x88` | previous registry entry |

Observed transitions:

1. `sceNpCreateAsyncRequest`: allocates `state=0`, `started=0`, `kind=1`, `result=0`.
2. An async operation export retains the record, rejects a second assignment when `+0x18 != 0`, sets `+0x18=1`, installs a job descriptor at `+0x70`, sets `state=1`, and creates a worker with a 0x8000-byte stack.
3. Worker entry `0x17680` dispatches by job type, executes the backend operation, then calls `0x1cb10`, which atomically stores the result at `+0x58` and sets `state=2`.
4. Worker releases its retained record and signals the SDK async event.
5. Abort sets `+0x10` and cancels live backend handles. Backend workers call `0x1c900`; either abort flag makes that helper return `0x80550012`. A worker then completes normally with that error stored as its result.
6. Delete requests abort, removes the public ID immediately, defers destruction until references drain, and waits/joins a worker handle if present.

An abort of an unstarted request succeeds but does not synthesize completion: the state remains zero. A subsequent poll therefore follows the state-zero rule above.

### Reachability operation ownership

`sceNpCheckNpReachability` is one of the operation exports in transition 2. It
checks the request subsystem first, rejects request ID zero or user ID `-1`,
looks up the request in the same registry, marks the operation assigned,
installs job kind 2 with the user ID, and launches the common worker.

This ownership boundary matters in HLE. If create and poll use SharpEmu's local
registry while reachability alone is routed to the guest provider, the provider
receives an HLE-owned request ID that is absent from its private registry. The
operation fails before its worker starts. SharpEmu therefore keeps all three
calls in one HLE registry. The local reachability worker completes with result
zero; this reports completion of the check, while
`sceNpGetNpReachabilityState` remains the source for the offline reachability
value.

### Reachability callback lifecycle

`sceNpRegisterNpReachabilityStateCallback` calls `0x1b390` before validating
the callback pointer. That helper locks the PRX SDK-init state at `DAT_62a50`
and reports initialized only when `DAT_62a58 > 0`. It does not consult the
separate allocator created by `fHGhS3uP52k`.

SharpEmu therefore gates registration and dispatch on the PRX-owned async/SDK
lifecycle represented by `NpManagerAsyncRequests.IsInitialized`. Manager-global
initialize and terminate do not create, clear, or invalidate this callback slot;
the HLE reset/PRX teardown path does.

## Synchronization

- SDK initialization is checked under the SDK-init lock (`DAT_62a50` / state `DAT_62a58`). The request list is created during PRX start, not by an online backend response.
- Registry mutation and reference counts use the list mutex at `DAT_62b40`, named `SceNpSdkReqList` by firmware.
- State/result/abort/cancellation fields use the mutex at `DAT_62b48`, named `SceNpSdkReq`.
- Despite being stored in each record at `+0x48`, `DAT_62b48` is one shared mutex pointer for all records, not one newly allocated mutex per request.
- Lookup increments a record reference before dropping the list lock. Delete unlinks and marks deleted under the list lock; the last release performs destruction.
- Worker join/wait occurs after registry/state locks have been released.

## Backend cancellation evidence

Abort passes each live integer handle to a distinct cancellation route while holding the shared request-state mutex:

- `+0x34` -> import wrapper at `0xf0e0`
- `+0x38` -> import at `0x29ee0`
- `+0x3c` -> wrapper at `0x09d0`
- `+0x40` -> local wrapper at `0x24720`

Representative backend operation `0x17da0` creates a handle, installs it at `+0x34`, checks abort via `0x1c900` before the network/service call, checks again afterward, overwrites the backend result with `0x80550012` when aborted, clears the handle, then destroys the backend object. Other operation families use the other handle slots.

## Offline implementation decision

These four exports can be implemented truthfully without a real online backend because creation, lookup, abort intent, deletion, ref-counting, and polling are local SDK machinery. The implementation must not invent a completed online result:

- Create should return and register a real local handle.
- Poll of a newly created request must report the evidenced unstarted outcome, not success.
- Abort should set local cancellation intent and return the evidenced validation errors; with no attached HLE job there is simply no backend handle to cancel.
- Delete should invalidate the handle and coordinate any attached local worker; it must not remain an unconditional-success stub.
- Offline async operation handlers must explicitly mark the request running and complete it with their evidence-backed offline result. `sceNpCheckNpReachability` now owns such a local operation; the four lifecycle exports alone still do not fabricate completion.

The remaining integration question is which SharpEmu HLE event should model firmware PRX-start/stop initialization. Firmware's request registry is initialized during module start; it is separate from `fHGhS3uP52k`'s manager allocator state at `0x14950`. Tying request validity only to `_managerAllocatorAddress` would therefore conflate two firmware lifecycles.

## Recommended ownership and tests

- One source owner for `src/SharpEmu.Libs/Np/NpManagerExports.cs` because it already contains the delete export and current NpManager lifecycle gate.
- Put the registry/state implementation in a new internal file such as `src/SharpEmu.Libs/Np/NpManagerAsyncRequests.cs` to reduce conflicts with unrelated NpManager exports.
- Put focused ABI/state tests in a new `tests/SharpEmu.Libs.Tests/Np/NpManagerAsyncRequestExportsTests.cs`.
- Required tests: init/error precedence; null/bad-size create; 32-entry cap; smallest-gap ID reuse; max-ID guard; invalid/unknown abort/delete/poll; poll output untouched while unstarted/running; completed output write; abort-before-start; abort-during-local-worker; delete-after-abort; delete-vs-worker race; and reset/termination cleanup.
- This binary establishes Gen5 behavior. Existing Gen4 registration of `S7QTn72PrDw` is outside this evidence packet.
