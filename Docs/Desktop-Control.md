# Desktop Control

DevProjex exposes local semantic control for an already running Desktop instance.
It is not a remote API and does not open a TCP port.

## Commands

```shell
devprojex ui list
devprojex ui list --format json
devprojex ui status
devprojex ui activate
devprojex ui preview open --view tree-content
devprojex ui preview close
devprojex ui preview set-view content
devprojex ui tree set-format markdown
devprojex ui filter set "service"
devprojex ui filter clear
devprojex ui search set "Program"
devprojex ui search next
devprojex ui search previous
devprojex ui search clear
```

Targetable commands accept `--instance ID`, `--project PATH`, and
`--timeout DURATION`.

`ui list --format json` writes a versioned
`devprojex-ui-instances` document. State returned by targetable actions is the
versioned Desktop protocol state described below; it uses stable English keys
and contains no localized identifiers or terminal styling.

## Target Selection

1. An explicit instance ID selects that instance.
2. An explicit canonical project path selects its unique instance.
3. With one running instance, that instance is selected.
4. Multiple possible instances produce exit code `5` and a list of candidates.

`open --new-window` deliberately starts another Desktop process. Other
`devprojex open` requests reuse a suitable instance when possible. `--wait`
returns only after the requested project and state have been applied.

## Transport and Access

- Windows: Named Pipes restricted to the current user.
- Linux/macOS: Unix domain sockets with mode `0600`.

Each Desktop process registers protocol version, instance ID, PID, process start
time, project path, activity time, transport, and endpoint in the per-user data
directory. Registrations are removed at shutdown; stale entries are pruned only
after process identity checks.

No TCP listener, daemon, service, network access, shell expansion, or command
execution is involved.

## Protocol

Request:

```json
{
  "protocolVersion": 1,
  "requestId": "...",
  "instanceId": "...",
  "action": "preview.open",
  "payload": {
    "view": "tree-content"
  }
}
```

Response:

```json
{
  "protocolVersion": 1,
  "requestId": "...",
  "ok": true,
  "state": {},
  "error": null
}
```

Messages are size-bounded and validated. Unknown versions, actions, payloads, and
targets fail with stable codes.

## Desktop Semantics

Requests represent user intent, not control names or methods. Avalonia operations
run through the UI dispatcher. Success is returned after state is applied, not
after a request is merely queued.

Preview open/close and clear operations are idempotent. Commands that can safely
wait during project loading are queued. A destructive project switch is rejected
while a modal picker is active, and all commands are rejected while the instance
is shutting down.
