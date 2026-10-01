# Agent journal

The journal shows what project context local MCP clients received without storing
file bodies, detected values, or the text of search queries and symbol selectors.
It stores bounded execution parameters and counters instead. Every remaining
string value is checked by the same secret and private-data detectors before it
is queued. It is an on-device history shared by Desktop,
Terminal Workspace, and the CLI. In Desktop, open **MCP → Journal…** to inspect
it. The command is
available before a project is opened. In that state the window shows sessions
from all projects. With a project open, **Current project only** is selected by
default and can be cleared to show the complete journal.

The journal is stored in the `agent-journal` folder of the per-user DevProjex state
directory: `%LOCALAPPDATA%\DevProjex` on Windows, and `$XDG_STATE_HOME/DevProjex` on
Linux and macOS, or `~/.local/state/DevProjex` when `XDG_STATE_HOME` is not set. The
live-session registry (`live-sessions`) and the Desktop Agent activity preference
(`agent-activity-view.json`) live in the same directory. Desktop, Terminal Workspace,
the CLI, and the MCP server all resolve it the same way, so they always read and write
the same records.

The upper table lists sessions by start time, client and version, live or
standard mode, project roots, calls, result characters, estimated tokens,
delivered files, masked values, and duration. A live session is marked and its
row is refreshed while calls arrive. Active Standard sessions update their call
rows as well; only Live sessions drive the Agent activity tree markers. A
session left without a closing record is shown as inactive with an unknown end
time. Its duration is a lower bound ending at the last retained event, never at
the current time. The lower table lists the selected
session's calls, including their arguments, selection revision, duration,
result size, delivered files, masking counters, notices, and stable error code.
“Delivered” means that some of the file's content was actually returned to the
client: its whole text, a line range, a page of a stored result, search match lines,
or a declaration body. Naming a file in a listing, as `get_tree` does, or measuring
it, as `analyze` does, delivers nothing, and preparing or retaining a stored pack
does not count as delivery either. `related_files` quotes the reference text of each
relation it lists, so it delivers the file that text comes from: the seed for a listed
dependency and each listed dependent. Dependency targets, and a seed with no listed
relations, are only named. A `read_pack` call records the paths represented
by its returned page. A call counts each delivered file once; the session total adds
the calls together, so a file returned by three calls counts three times, while the
receipt's delivered-path table lists each file once with its number of calls. Masked
values are counted the same way: only masks inside returned content count, so a call
that returns no file content reports none. With
multiple roots, path identity includes both the root number and relative path.
Notices such as reading outside the selection are plain text facts, not warning
colors.

**Export…** writes the common receipt representation as Markdown or JSON.
**Clear** asks for confirmation and clears completed sessions for the current
project when the filter is selected, or completed sessions from the complete
journal when it is not. A session that also served other projects is kept when
one project is cleared, so their history is not lost. Active Standard and Live sessions are preserved by both
manual clearing and retention. **Reset data** applies the same protection while
clearing saved local project data.

If an active journal file disappears, the writer recreates its session header
and marks the next event as recovered and incomplete. The retained totals are
recomputed from the records that remain; the missing count is reported as a
lower bound when the exact count is unknowable. A failed session start can be
retried up to three times, and a later successful start reports calls that
could not be recorded before recovery. Temporary write failures are retried. If
a call still cannot be written, session totals exclude it and the receipt and
readers show `History is incomplete: N events could not be recorded.` Markdown
receipts retain the notices attached to each call. Journal
calls enter a non-blocking queue capped at 1,000 events. When a stalled store
fills it, the oldest pending calls are dropped and the next successful event
reports them through the same incomplete-history notice.

Session lists read the bounded header and closing summary of completed files.
Readers following an active session retain their byte position, counters and
last call, and read only newly appended complete JSONL records. UI change bursts
are combined before presentation. If the journal file is truncated or recreated,
the reader safely restarts at the new header and recognizes the recovery event.
Export and clear failures are shown in the journal window and do not close it.

Terminal Workspace uses `mcp log`, `mcp log last`, and `mcp log export <path>
[markdown|json] [last|session <id>]`. The direct CLI exposes the same records:

```shell
devprojex mcp log /path/to/project --last
devprojex mcp log /path/to/project --last --format markdown
devprojex mcp log /path/to/project --last --format json
```

Token counts are estimates derived from returned character counts, not tokenizer
output. In Terminal Workspace an empty journal says how to connect with
`:mcp connect codex` or `:mcp connect claude-code`. An empty journal means that
no matching local MCP session has written a record yet; connect a client and
complete a tool call before looking for it.

## Agent activity

**View → Agent activity** is off by default and is stored as a view preference.
It controls only the delivery markers in the Desktop tree and adds no text to the
status bar. When it is enabled for an open project with a live session, files
delivered during the latest live session receive a `✦` marker in the tree. Its
tooltip reports how many calls delivered that path, for example "Agent received
3 times". A `get_tree` or `analyze` call delivers no files and therefore adds no
marker; a `related_files` call marks only the files whose reference text it quoted.
The marker never changes filters or checkboxes. The trace is cleared when
a newer live session starts, when the live session ends, when Agent activity is
turned off, and when the project is reopened; calls made after reopening create a
new trace.

Standard-mode sessions remain visible in Journal, but they do not add the Live
context suffix to the main window title and do not drive Agent activity.
