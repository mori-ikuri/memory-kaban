# memory-kaban

memory-kaban keeps your AI conversations locally, word for word, without summarizing them.

*Kaban* is Japanese for 鞄 (bag). The command is still named `bag`.

## Why

Move to a new chat room, and the AI loses the previous conversation. Summaries
leave words out. memory-kaban stores the conversation text in a local SQLite
database, so an AI can use MCP to retrieve the relevant messages when it needs
just a few lines of context.

## Quick start

These PowerShell examples require the .NET 10 SDK and a Claude or ChatGPT export ZIP.
Run the commands from the repository root; replace the example paths with your own.

1. Build and test:

   ```powershell
   dotnet restore bag.slnx --locked-mode
   dotnet build bag.slnx --configuration Release --no-restore
   dotnet test bag.slnx --configuration Release --no-build --no-restore
   ```

2. Import your ZIP into SQLite. The database's parent directory must already exist:

   ```powershell
   dotnet ./src/Bag/bin/Release/net10.0/bag.dll import 'D:\archive\claude.zip' --db 'D:\archive\bag.db'
   ```

3. Find the original words, with nearby messages for context:

   ```powershell
   dotnet ./src/Bag/bin/Release/net10.0/bag.dll find --db 'D:\archive\bag.db' --text '鞄' --context 2 --limit 5
   ```

4. In Claude Desktop, open **Settings > Developer > Edit Config**. Add `bag` to
   `mcpServers`, keeping any existing servers. Set absolute paths to your built
   DLL and database:

   ```json
   {
     "mcpServers": {
       "bag": {
         "command": "dotnet",
         "args": [
           "D:\\tools\\memory-kaban\\src\\Bag\\bin\\Release\\net10.0\\bag.dll",
           "mcp",
           "--db",
           "D:\\archive\\bag.db"
         ]
       }
     }
   }
   ```

   Save, completely quit Claude Desktop, and reopen it. The `list_rooms`, `find`,
   `find_heads`, and `get_messages`
   tools should appear. See [Connect Claude Desktop](#connect-claude-desktop) for
   details, including what to do if `dotnet` is not on the client's PATH.

The DLL paths above use standard build output. If you use local build overrides,
adjust the paths as described below. Message bodies are preserved as text; see the
[supported export contract](#supported-export-contract) for text-block handling
and the treatment of tools and attachments.

## Overview

A small local archive for Claude and ChatGPT export ZIPs. It preserves message identity,
versions, branches, and provenance in SQLite. Import preserves exports; find reads
stored message bodies. Seals keep selected rooms or messages out of ordinary reads.
A read-only MCP server exposes local reads over standard input and output. The bag
application has no network listeners, outbound network requests, or telemetry.
ChatGPT imports support the `conversations.json` conversation-tree format.
Large exports split into `conversations-000.json`, `conversations-001.json`, and
subsequent numbered files in the same ZIP are also supported.

For the existing Windows installation, see the Japanese
[local upgrade and rollback procedure](docs/local-upgrade.ja.md).
It prepares v1.1, backs up v1.0 before replacing application files, and finishes
with a read-only check of the existing database. Run the script yourself only
after closing Claude Desktop; restart it to discover the new MCP tools.

## Build and run

Requires the .NET 10 SDK (LTS), with C# 14. Restore uses Microsoft.Data.Sqlite
10.0.13, stable System.CommandLine 2.0.13, and the official stable
[ModelContextProtocol.Core 2.3.0](https://www.nuget.org/packages/ModelContextProtocol.Core/2.3.0).
Dependencies are pinned in the project and package lock files. The application version is defined only in
`Directory.Build.props`; both `--version` and source ledger entries read the generated assembly metadata.

```powershell
dotnet restore bag.slnx --locked-mode
dotnet build bag.slnx --configuration Release --no-restore
dotnet test bag.slnx --configuration Release --no-build --no-restore
```

Run these commands from the repository root. Standard .NET output directories
(`bin` and `obj`) and the normal NuGet cache are used by default. The examples below
use the Release build at `src/Bag/bin/Release/net10.0/bag.dll`. Restore and SDK tools
can access package services; the no-network statement above describes the bag
application itself.

Optional machine settings belong in the ignored `Directory.Build.local.props`,
which is imported after the shared settings. For example, to keep build artifacts
in a sibling directory:

```xml
<Project>
  <PropertyGroup>
    <UseArtifactsOutput>true</UseArtifactsOutput>
    <ArtifactsPath>$(MSBuildThisFileDirectory)../bag-artifacts</ArtifactsPath>
  </PropertyGroup>
</Project>
```

`UseArtifactsOutput`, `ArtifactsPath`, and `RestorePackagesPath` are optional local
overrides. Custom output settings change the executable location; adjust commands
accordingly. No machine-specific path is required by the shared project.

The executable provides `import`, `find`, `seal`, `unseal`, and `mcp` subcommands.
System.CommandLine owns parsing, validation, and generated help (`--help` and
`<command> --help`).
Response files are disabled. Invalid arguments produce a fixed message because
the library's detailed diagnostics can echo private input values.

## Import an export

Obtain a ZIP through Claude's data export feature. The official
[export guide](https://support.claude.com/en/articles/9450526-export-your-claude-data)
describes how to request it. The accepted JSON shape is documented below as bag's
supported input contract, not a guaranteed stable export schema from Claude.
For ChatGPT, use OpenAI's [data export](https://help.openai.com/en/articles/7260999-exporting-your-chatgpt-history-and-data)
and select the provider explicitly:

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll import 'D:\archive\chatgpt.zip' --db 'D:\archive\bag.db' --format chatgpt
```

The default remains `--format claude`; there is no format guessing. Both providers
can share one database. Their room and message identities remain provider-scoped.

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll import 'D:\archive\claude.zip'
```

By default the database is `bag/bag.db` under .NET's `LocalApplicationData`
directory (`%LOCALAPPDATA%\bag\bag.db` on Windows). An explicit database path is
also supported; its parent directory must exist:

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll import 'D:\archive\claude.zip' --db 'D:\archive\bag.db' --format claude
```

The ZIP stays at its original location. The importer opens it read-only, never
extracts entries to disk, hashes the same open handle before and after reading,
and commits only after the hashes agree. The database path must differ from the
source. A database with unrelated tables is rejected before adding bag tables.

Import success prints only `rooms`, `messages`, `new_versions`, and `unresolved_rooms`
counts. For ChatGPT, `messages` counts searchable utterances; non-utterance nodes
are preserved separately as auxiliary rows. Exit codes: **0** success (including no find matches), **1** invalid
arguments, **2** operation failure. The meanings are unchanged for import.
Errors omit input text, paths, and exception details. A failed new import may leave
an empty database file, but never committed partial archive rows. An unresolved
mainline is a successful preservation of the entire graph, reported in the count.

## Speaker names

The database stores the original sender, such as `human` or `assistant`. Display
labels are applied only at input and output boundaries. Claude defaults remain
`user` for `human` and `assistant` for `assistant`. ChatGPT keeps its original
`user` / `assistant` roles in storage and displays `chatgpt-user` /
`chatgpt-assistant`, so conversations from the two providers remain distinguishable.

To change them, place `bag.config.json` beside the database:

```json
{
  "speaker_names": {
    "human": "Visitor",
    "assistant": "Guide",
    "chatgpt:user": "ChatGPT visitor",
    "chatgpt:assistant": "お姉ちゃん"
  }
}
```

Partial overrides are allowed, and additional original sender keys can be mapped.
ChatGPT configuration keys use the reserved `chatgpt:` prefix followed by the
original role; other keys retain their existing meaning. `--speaker お姉ちゃん`
and MCP `speaker: "お姉ちゃん"` then select ChatGPT assistant messages. An unconfigured
ChatGPT role is displayed with the same provider prefix. A literal Claude sender
starting with `chatgpt:` stays unchanged and is not selected by a ChatGPT label;
those keys are reserved and cannot be used to configure such a Claude sender.
An empty `speaker_names` object keeps the defaults. An unmapped original sender is
displayed unchanged. CLI `--speaker` and the MCP `speaker` enum accept configured
labels only, with exact case-sensitive matching; for this example, use `Visitor`
or `Guide`. To filter an otherwise unmapped sender, first add a mapping for it.
JSON's `speaker` field also contains the display label. Changing labels does not
rewrite the database or its version hashes.

An unmapped sender can have the same text as another sender's display label. In
that case the displayed labels alone cannot distinguish them; configure unique
labels for both original values before relying on speaker filters.

The root must be an object containing only the `speaker_names` object. Keys and
labels must be nonempty, contain a non-whitespace character, and contain no control
characters. Labels must be unique after merging with the defaults. Duplicate JSON
keys, unknown root properties, non-string labels, malformed or unreadable files,
and invalid UTF-8 produce `Invalid configuration.` UTF-8 with or without a BOM is
accepted. A missing file uses the defaults.

CLI find reads the file on each invocation. MCP reads it once at startup so its
advertised speaker enum and responses agree; restart the server after changing it.
This startup snapshot does not apply to seals, which are reloaded for every read.
Databases in the same directory share both configuration and seals.

## Find messages

These examples use the default speaker labels. With the configuration above,
replace `--speaker user` with `--speaker Visitor`.

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll find --db 'D:\archive\bag.db' --from '2026-10-26 09:00' --to '2026-10-26 18:00'
dotnet ./src/Bag/bin/Release/net10.0/bag.dll find --db 'D:\archive\bag.db' --text '鞄' --json
dotnet ./src/Bag/bin/Release/net10.0/bag.dll find --db 'D:\archive\bag.db' --speaker user --text '鞄' --context 2 --limit 20
```

Every supplied condition is combined with AND:

| Option | Meaning |
| --- | --- |
| `--from`, `--to` | Inclusive bounds on the message creation instant |
| `--room` | Literal part of a snapshot's room name, or an exact room ID |
| `--speaker` | A configured display label; defaults are `user`, `assistant`, `chatgpt-user`, `chatgpt-assistant` |
| `--text` | Literal, case-sensitive substring of the stored message body |
| `--all-branches` | Include off-mainline messages; otherwise use mainline membership |
| `--include-sealed` | Explicitly include sealed rooms and messages in the CLI only |
| `--context N` | Add up to N ancestors and N selected continuation messages; default 0 |
| `--limit N` | Maximum distinct matching versions; default 20, minimum 1 |
| `--json` | Emit the `bag-find/1` JSON envelope |

Times without an offset are Japan time, independent of the computer's timezone.
Accepted input is `yyyy-MM-dd`, `yyyy-MM-dd HH:mm`, or a date and time with
seconds and up to seven fractional digits. A `T` separator also works. Explicit
`Z` or `+HH:mm` / `-HH:mm` offsets are accepted on date-and-time values.
A date alone means midnight, including for `--to`; use a time to include later
messages on that date. Reversed ranges and invalid calendar dates are rejected.
UTC is used for comparisons. Stored timestamps and message versions are unchanged.
Stored times follow the importer's explicit-timezone interpretation, with the
runtime's 100-nanosecond comparison precision; the original string remains available.
Missing, invalid, unzoned, or unrepresentable Japan dates are unknown. Without a period filter they
sort after all dated messages; with either `--from` or `--to` they cannot match.
Human output labels them `日時不明`; JSON timestamps are null while `created_at`
retains the original value. Connected unknown-time messages can still be context.

Search covers **all retained versions**, without guessing a globally latest ZIP.
The same saved version in multiple exports appears once and keeps the matching
source SHA256 values, room names, and mainline membership. Different saved versions
of one message remain separate matches. Room-name and mainline conditions must
both hold in the same source snapshot.

Only message bodies are searched. Thinking, injections, flags, attachments, and
all other auxiliary rows are excluded. Substring matching does not tokenize text:
one-character Japanese queries work, and `%`, `_`, and quotes are literal. Text is
not normalized, case-folded, trimmed, or interpreted as a regular expression.

`--limit` is applied to distinct matches before context is added, so context can
make the output longer than the limit. Context does not need to satisfy the search
filters. Context follows **parent links**, even when timestamps are out of order or
unknown. Each neighbor comes from the same source and room as its match:

1. Before: follow the parent chain up to N messages, including branch ancestors.
2. After a mainline match: follow the marked mainline continuation up to N messages.
3. After a branch match: use the same B rule (`latest-leaf-created-at/2`) within that
   branch's descendant subtree, then follow the selected path up to N messages.
   A single path works without timestamps. With several leaves, the newest leaf's
   instant wins, with ordinal leaf-ID tie-breaking. If dates cannot decide, no
   continuation is guessed. Ancestors are still available.

Missing links and cycles stop traversal; messages are never invented or repeated.
Versions from different snapshots are never spliced into a conversation. Overlapping
output is merged by saved version, retaining source-specific links to matching versions.
The final display remains chronological, with unknown times last; context selection
uses graph order even when that differs from display order. Equal timestamps and
unknown times use provider, room ID, message ID, and version hash in ordinal order.

Human output shows Japan time, room name, speaker, provenance, and the full body.
Body text is written unchanged between separate headers and separators. CLI and
MCP JSON use `UnsafeRelaxedJsonEscaping` to write Japanese and ordinary Unicode
directly. JSON syntax, control characters, and some Unicode characters (including
some emoji) can still be escaped; decoding `body` recovers the exact stored
characters. This UTF-8 JSON is intended for JSON consumers, not embedding in HTML
or a script element.
The envelope contains `schema`, `match_count`, `message_count`, `context_truncated`,
and `messages`. `context_truncated` reports whether an MCP output cap omitted
context; ordinary CLI find has no total-output cap and reports false.
Each message contains `provider`, `room_id`, `message_id`, `version_hash`, original
`created_at`, `timestamp_utc`, `timestamp_jst`, `speaker`, `body`, `is_match`, and
`sources`. Source entries include `source_sha256`, `room_name`, `is_mainline`, and
`context_for` stable message keys. `is_match` identifies the selected matches;
additional neighbors can match the conditions but fall beyond the selected limit.
An empty result is successful: human output says `No matches.`, and JSON has an
empty `messages` array. Export filenames and database paths are not in results.

Find opens SQLite with `Mode=ReadOnly` and uses one deferred read transaction for
consistent selection and context. A missing DB is never created, and find never
creates a default directory. No import, migration, or database repair is performed.
Literal scans and timestamp conversion can inspect every saved version. Only selected
bodies are materialized. Context also loads graph metadata for each matching source
and room, without loading all bodies. A large graph, body, limit, context count, or
source count can still increase memory or output. There is no full-text index or
pagination in this version.

## Seals

`seal` and `unseal` change a separate `seals.json` beside the database. They do not
modify archive tables or import data. Keep this file when rebuilding the database:
it contains the owner's judgments and cannot be reconstructed from export ZIPs.
Use one archive directory per independent set of seals; databases in the same
directory share the sidecar. The file contains identifiers only, never bodies,
room names, or source filenames.

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll seal --db 'D:\archive\bag.db' --room 'room-id'
dotnet ./src/Bag/bin/Release/net10.0/bag.dll seal --db 'D:\archive\bag.db' --message 'message-id'
dotnet ./src/Bag/bin/Release/net10.0/bag.dll unseal --db 'D:\archive\bag.db' --room 'room-id' --message 'message-id'
```

Replace the sample identifiers before running. Room IDs are exact, and a message
ID can be supplied alone when it is unique. Add `--room` to disambiguate a message.
The CLI defaults to the `claude` provider. Add `--provider chatgpt` to `seal` or
`unseal` for ChatGPT identifiers, for example:

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll seal --db 'D:\archive\bag.db' --provider chatgpt --room 'room-id'
```

Seals use the stable
provider/room/message identity, so every saved version and source is covered.
New seals must resolve in the read-only database. Removing a seal with an explicit
room ID can work even if a rebuilt database no longer contains it. Removing a room
seal keeps any separate message seals. Repeating the same operation is harmless;
success prints only `{"changed":1}` or `{"changed":0}`.

```json
{
  "schema": "bag-seals/1",
  "seals": [
    { "provider": "claude", "room_id": "room-id" },
    { "provider": "claude", "room_id": "another-room", "message_id": "message-id" }
  ]
}
```

Find excludes sealed rooms and messages before choosing matches and applying its
limit. A sealed ancestor or continuation stops that direction of context; find
never jumps through it or reroutes a branch to avoid it. `--include-sealed` is the
explicit CLI override and restores the original context traversal as well.

Each read loads a fresh, validated snapshot under the same process-shared lock
used by `seal` and `unseal`. Reads create no file or directory. A missing sidecar
means no seals. An unreadable, malformed, unsupported, or ambiguous seal file is
an error, never permission to return everything. Even `--include-sealed` validates
the file. Use the commands for updates; do not edit it concurrently in another app.
Updates serialize, flush a complete temporary file, verify the previous bytes,
and replace the sidecar by a same-directory rename. An interrupted update can
leave a temporary file for inspection; reads use only `seals.json`.

## MCP server

Start the stdio server with an existing database:

```powershell
dotnet ./src/Bag/bin/Release/net10.0/bag.dll mcp --db 'D:\archive\bag.db'
```

An MCP client launches this process and exchanges JSON-RPC messages through its
standard input and output. Standard output contains protocol messages only.
The process exits when standard input closes or Ctrl+C cancels it. The server
uses the official C# SDK 2.3.0 with a derived stdio transport that changes only JSON
output encoding; input parsing and protocol handling remain with the SDK. There
is no HTTP server, network port, outbound request, or client callback. No MCP
client settings are installed or changed by bag. A connected AI client may send
returned archive text to its model provider according to that client's settings;
running bag locally does not make the client offline.

The server offers four tools, all marked read-only and non-destructive:

| Tool | Arguments | Result |
| --- | --- | --- |
| `list_rooms` | None; omit arguments or pass `{}` | The `bag-rooms/1` envelope |
| `find` | Optional arguments below | The `bag-find/1` envelope described above |
| `find_heads` | The `find` arguments below, except `context` | Compact previews in a `bag-heads/1` envelope |
| `get_messages` | Required `ids` array of exact saved-version keys | Full bodies in a `bag-messages/1` envelope |

`find` uses the same AND filters, Japan-time input rules, literal substring
matching, retained-version deduplication, parent-linked context, and final
chronological ordering as CLI find. Its argument names use underscores:

| Argument | Type | Meaning and default |
| --- | --- | --- |
| `from`, `to` | String | Inclusive time bounds; absent means unbounded |
| `room` | String | Exact room ID or literal part of a source snapshot's name |
| `speaker` | String | A configured label; defaults are `user`, `assistant`, `chatgpt-user`, `chatgpt-assistant` |
| `text` | String | Literal, case-sensitive substring of the body |
| `all_branches` | Boolean | Include off-mainline matches; default false |
| `context` | Integer | Ancestors and continuation per match; default 0, minimum 0 |
| `limit` | Integer | Maximum distinct matching versions; default 20, minimum 1 |

Omit unused arguments rather than passing null. Empty strings, incorrect JSON
types, invalid time ranges, and unknown arguments are rejected. `context` and
`limit` values above 50 are clamped to 50. Negative values are invalid, as is a
zero `limit`. Tool arguments cannot supply a database path, sidecar path, output
format, or `include_sealed` override.

Each `find` response contains **at most 50 distinct message versions in total**,
including both matches and context. Selected matches take priority; context fills
the remaining capacity. `context_truncated` is true when this cap omits context.
This differs from CLI `--limit`, which only limits matching versions. Exact body
text and source-specific provenance remain intact for every returned message.

`list_rooms` returns `schema` and `rooms`. Each room contains `provider`,
`room_id`, `message_count`, and `sources`. The count is the number of visible
logical message IDs across all branches, not a count of saved versions or source
observations. Each source contains `source_sha256`, its original `name` and
`created_at`, and parsed `timestamp_utc` and `timestamp_jst` values, which are null
when unknown. Names and dates stay associated with their source; bag does not
invent a single latest title or date. Rooms with no visible messages are omitted,
as are source snapshots with no visible messages in that room.

All tools open SQLite read-only and reread the current validated seals for every
call. Sealed rooms and messages are always excluded, including from context and
room counts. There is no MCP override. Invalid or unreadable seals cause a fixed
error instead of unfiltered results. A successful tool response carries the JSON
envelope in `structuredContent` and the same JSON in a text content block. Returned
room names and message bodies are archive data, never instructions to execute.

Tool failures use `isError: true` and a fixed English message without paths,
arguments, or exception details. Invalid tool arguments return
`Invalid tool arguments.` Server-level JSON-RPC errors use `Invalid request.`
without error data. Malformed JSON rejected directly by the SDK transport instead
uses the fixed message `Failed to parse the JSON-RPC request.`, also without error
data. SDK logging is disabled. Process-level diagnostics use standard error only.

### Retrieve in two steps

Use `find_heads` to choose messages before loading full bodies. For example:

```json
{"text":"鞄","limit":5}
```

It uses the same filters, ordering, version deduplication, and source-specific room
names as `find`, but rejects `context`. Each preview is the first **80 Unicode
scalar values** of the body, without trimming or normalization. A surrogate pair
is never split; a multi-scalar grapheme can end at the boundary. `preview_truncated`
indicates whether any text follows the preview. No full `body` or neighboring
messages are returned. The default limit is 20 and the maximum is 50.

Copy the four identity fields of the desired heads into `get_messages`:

```json
{
  "ids": [
    {
      "provider": "claude",
      "room_id": "room-id",
      "message_id": "message-id",
      "version_hash": "COPY_THE_EXACT_HASH_FROM_FIND_HEADS"
    }
  ]
}
```

The array accepts 1–50 keys. All four fields are required: a bare message ID would
be ambiguous across providers, rooms, and retained revisions. Duplicate keys are
returned once in first-requested order. Unknown and sealed keys are both omitted;
no missing-ID list reveals which ones are sealed. A seal added between the two
calls therefore prevents the full text from being returned. Exact requests may
retrieve off-mainline versions, and include all their retained source provenance.
Bodies are returned unchanged, without a preview limit or added context.

The existing `find` tool and its `bag-find/1` output remain unchanged. The new tools
are MCP tools; CLI `find` continues to provide the original full-text interface.

`bag-heads/1` contains `schema`, `match_count`, `message_count`, and `messages`.
Each head contains the four identity fields, `created_at`, `timestamp_utc`,
`timestamp_jst`, `speaker`, `preview`, `preview_truncated`, and `sources`. Each
source has `source_sha256`, `room_name`, and `is_mainline`.
`bag-messages/1` contains `schema`, `message_count`, and `messages`; its message
entries use the same full-message fields as `find`.
Heads reduce response text; the current implementation still materializes selected
bodies internally before making previews. They are not a constant-memory search.

### Connect Claude Desktop

Build bag first, then open Claude Desktop's **Settings > Developer > Edit Config**.
Add a `bag` entry to the existing `mcpServers` object, preserving any other servers.
Use absolute paths to the built DLL and archive; these Windows paths are examples:

```json
{
  "mcpServers": {
    "bag": {
      "command": "dotnet",
      "args": [
        "D:\\tools\\bag\\src\\Bag\\bin\\Release\\net10.0\\bag.dll",
        "mcp",
        "--db",
        "D:\\archive\\bag.db"
      ]
    }
  }
}
```

If the client cannot find `dotnet` through its environment, use the full path to
the installed `dotnet` executable as `command`. Save the configuration, completely
quit Claude Desktop, and reopen it. The connected server should offer `list_rooms`,
`find`, `find_heads`, and `get_messages`. The official
[local MCP server guide](https://modelcontextprotocol.io/docs/develop/connect-local-servers)
documents this configuration format and restart procedure. Registration is manual;
bag does not edit client configuration or start an external client.

## Failure messages

CLI failures go to standard error without input values, conversation text, paths,
stack traces, or raw exception messages. Validation errors have exit code 1;
the other messages below have exit code 2. MCP tool calls report operation errors
inside the protocol response and keep the server running.

| Fixed message | Meaning |
| --- | --- |
| `Invalid arguments. Use --help.` | Missing, unknown, repeated, or invalid command arguments |
| `ZIP not found.` | The source ZIP or its parent directory does not exist |
| `Not a valid ZIP.` | The file cannot be opened as a valid ZIP archive |
| `conversations.json not found.` | No single-file entry or supported ChatGPT numbered entry exists |
| `Multiple conversations.json entries.` | More than one matching ZIP entry exists |
| `Unsupported export format.` | Unsupported, malformed, duplicate, or invalid UTF-8 export data |
| `Incompatible database.` | Wrong application/schema, corrupt or non-SQLite file, or same-source importer-version mismatch |
| `Database is in use.` | SQLite reports a busy or locked database |
| `File access failed.` | A source or database cannot be opened/read/written, including a missing find database |
| `Source and database must differ.` | Import source and destination resolve to the same path |
| `Source changed during import.` | Source hashes differ before and after reading |
| `Invalid seal file.` | Unsupported or malformed seal data, or an unsafe sidecar location |
| `Seal file is in use.` | The seal operation lock timed out |
| `Seal file changed during update.` | An external change was detected before replacement |
| `Seal target not found.` | The identifier did not resolve to a supported archive target |
| `Seal target is ambiguous. Specify a room ID.` | A message ID needs an exact room ID |
| `Invalid configuration.` | Speaker configuration cannot be read or does not satisfy its contract |
| `Operation failed.` | Other handled operational failures |

Archive-reading operations share database validation and perform no schema repair.
Version 1.1.0 retains schema version 2 and can read v1.0 schema-2 databases without
modifying them. Earlier schema version 1 databases are
rejected without modification; rebuild into a different database from the original
ZIPs as described below. Import also rejects a source whose recorded importer
version differs from the running version.

## Supported export contract

Claude ZIPs must contain exactly one entry whose basename is `conversations.json`.
ChatGPT accepts that layout or the numbered layout below. Every selected entry
is a complete UTF-8 JSON array. Duplicate matching entries, duplicate object
properties, duplicate room IDs, duplicate message IDs within a room, invalid UTF-8,
unsupported structures, and trailing content are rejected atomically.

### Claude

Each room has a nonempty `uuid` and a `chat_messages` array. `name` and `created_at`
are retained. Each message has a nonempty `uuid` and `sender`. Parent IDs are read
from `parent_message_uuid` or `parent_uuid`; conflicting aliases are rejected.
Original timestamp strings and parent IDs are retained, including missing values.
Sender strings are preserved as their original values, including unknown senders.
Speaker-name configuration changes presentation and accepted filter labels only.
The provider (`claude`) is stored on the room independently from the sender.

Only `content` blocks with `type: "text"` contribute to the message body. Their
`text` values are joined in source order with **one LF between every pair**,
including empty blocks. No trimming, newline conversion, Unicode normalization,
or removal of empty messages occurs. Absent/null/empty content yields an empty
body. A text block with no string value is rejected. The top-level display `text`
field is discarded and never used as a fallback. Individual text blocks also
remain in the auxiliary table, preserving the original block boundaries.

Other content blocks, message fields, and room fields go to typed auxiliary rows.
Kinds use `content:<type>`, `message:<field>`, and `room:<field>`; untyped content
uses `content:unknown`. Thinking content and its hidden/truncated flags, injected
prompts, flags, attachments, and unknown future types are preserved there, outside
the body. Attachments absent from the JSON cannot be recovered from it; unrelated
ZIP entries are not extracted or imported.

Every structured `tool_use` or `tool_result` object, including those nested inside
unknown auxiliary values, is reduced to **type and name only**. Arguments, results,
call IDs, and other tool fields are never persisted. Tool results can resolve a
name from a later tool use in the same room. Unresolvable names are null; conflicting
tool-use names for one call ID are rejected. Literal message text is preserved as
text, even if the user discussed tools in it. This is structural export handling,
not text-content redaction.

### ChatGPT

This is a deliberately bounded contract, tested with synthetic exports. OpenAI's
[transfer guide](https://help.openai.com/en/articles/9106926-transfer-exported-conversations-between-chatgpt-accounts)
identifies `conversations.json`, but does not promise a stable schema. The tree
shape is informed by an author's [inferred JSON schema](https://gist.github.com/dmarx/08afeb669cdc2f974d6aca61dcce360d)
and [format notes](https://github.com/ThierryAbalea/chatgpt-conversation-export/blob/main/docs/format.md).
Flat `messages` exports, nested ZIPs, multi-ZIP archives, and live API downloads
are outside this version's supported contract. One real numbered ChatGPT export has been trial-imported into a copy of an
existing archive. Single-file and additional variants remain covered by synthetic
tests rather than real exports.

Numbered files use the exact lowercase basename `conversations-N.json`, where N
is a nonnegative ASCII integer. Zero padding is allowed: the usual shape is
`conversations-000.json`, `conversations-001.json`, and so on. The shape is also
described by an author's [split-export converter](https://github.com/yungshun317/chatgpt-conversation-converter).
All parts must be in the same directory inside one ZIP, with contiguous numeric
indexes starting at 0. They are read in numeric order, independent of ZIP-entry
order. Each file contains whole conversations; the importer does not stitch one
conversation's nodes together across files.

Single and numbered layouts cannot be mixed. Duplicate indexes (including `0`
and `000` aliases), missing intermediate indexes, differing part directories, and
noncanonical filenames beginning with `conversations` (case-insensitive) are
rejected rather than silently skipped. Unrelated entries remain untouched.
Duplicate room IDs anywhere across the selected parts are rejected, including
identical copies and rooms containing only auxiliary nodes.

All parts share **one transaction and one original-ZIP source SHA256**. A failure
in a later part rolls back earlier parts too; reimport and reconstruction retain
the same identity/version rules. Entries are read directly without extraction or
creating a merged JSON file. The original ZIP is hashed before and after the
whole import. Contiguity cannot detect a missing final part without a known total;
this version does not interpret `export_manifest.json`. Use the unmodified export
ZIP. See the [trial-import procedure](docs/local-upgrade.ja.md)
for a command that writes a new database separate from the real archive.

Each conversation must have a nonempty `id` or `conversation_id` and a `mapping`
object. If both IDs are present, they must agree. `title` is the room name;
original room fields are also retained as auxiliary data. Each mapping entry has
`parent` (string or null) and `message` (object or null). An optional `children`
array contains string node IDs. If `children` is absent, the reader derives it
from parent links after reading the whole mapping. Explicit arrays remain
authoritative and are checked for inconsistencies; explicit null or invalid
types are rejected. The retained node JSON stays unchanged, without adding the
derived arrays. ChatGPT JSON documents may nest up to 128 levels, including
the outer array and mapping containers; deeper input is rejected atomically.
Claude retains its existing 64-level limit. Mapping keys are authoritative node identifiers and become the stored
ChatGPT message IDs. Node `id` and `message.id` are retained separately; they need
not equal the mapping key.

Only visible `user` and `assistant` messages with `content_type: "text"` or
`"multimodal_text"` contribute searchable utterances. Their string `parts` are
joined in source order with one LF between every pair, including empty strings.
Text is not trimmed, normalized, or rewritten; citation glyphs remain literal.
Non-string multimodal parts and other metadata remain on the auxiliary shelf.
Null-message roots, system/custom context, visually hidden messages, tool calls
and results, analysis/thoughts, and unknown content types or roles do not become
searchable empty utterances. They remain typed auxiliary data.
Only absent/null or `final` channels and absent/null or `all` recipients are
eligible. Metadata flags `is_visually_hidden_from_conversation` and
`is_user_system_message` exclude a message when true. Empty text and visible
multimodal messages without string parts retain an empty body; their attachments
remain auxiliary. Flag and structural field types are checked strictly.

Every node's original topology and retained metadata are stored under
`node:<mapping-key>` in `room_auxiliary`. Utterance versions retain their node
metadata as well. Tool calls (assistant messages addressed to a non-`all`
recipient) and tool results retain only type and tool name in their message
payload; arguments/results are not copied into other shelves. Original ZIPs
remain the source for information intentionally excluded from SQLite.

Numeric `create_time` and `update_time` values are interpreted as Unix seconds
and converted to invariant UTC timestamps at .NET's 100-nanosecond precision.
Sub-tick fractions are truncated toward zero before adding the epoch.
The original numeric values remain in auxiliary metadata. Missing/null or
out-of-range times are unknown, never replaced by the import time.

The rule `chatgpt-current-node/1` marks the ancestor path of `current_node` in the
original mapping. The selected node can be internal or contain no utterance.
Only utterances on that path receive mainline membership. Missing/invalid
selection, missing graph references, inconsistent edges, or cycles leave the
mainline unresolved while preserving all nodes and branches. The mainline is
never guessed from timestamps. `--all-branches` can still retrieve the stored
utterances; optional branch context uses the existing continuation rule described
under Find messages.

For context traversal, an utterance's stored parent is its nearest utterance
ancestor across intervening auxiliary-only nodes; the original links remain in
the node metadata. This projection never combines exports. Seals still stop
traversal at a sealed utterance. `find`, `find_heads`, `get_messages`, and
`list_rooms` return utterances only, not auxiliary payloads.
Only null denotes a ChatGPT graph root; UUID values that are Claude root sentinels
remain ordinary node IDs in ChatGPT graphs.

## Claude mainline rule

All branches remain. Mainline membership is derived independently for each ZIP
and room, never by combining nodes from different exports. The stored rule is
`latest-leaf-created-at/2`:

1. Missing parents or any cycle leave the graph unresolved. They are not repaired.
2. A non-null `current_leaf_message_uuid` selects that leaf if valid. An invalid
   explicit selection stays unresolved. This is the only explicit-selection field
   currently recognized; other room fields are preserved as auxiliary data.
3. With one leaf, its ancestor chain is the mainline.
4. With multiple leaves, every leaf must have a parseable creation timestamp with
   an explicit timezone. The greatest instant wins. Equal instants use the first
   message ID in ordinal order. Its ancestor chain is marked, with reason
   `inferred-latest-created-at`. This does not claim to know the branch selected in
   the original UI.
5. Missing or invalid comparable timestamps leave the mainline unresolved. Empty
   rooms have reason `empty`.

The root-parent rule is defined once in `MainlineSelector.IsRoot` and used for
missing-parent checks, cycle detection, and mainline ancestor traversal. Null,
the empty string, `00000000-0000-0000-0000-000000000000`, and
`00000000-0000-4000-8000-000000000000` mean a graph root. These are exact matches;
other missing parent IDs still leave the graph unresolved. The original parent
value remains stored without conversion to null or another UUID.

Membership and its reason are recomputed even when importing the same ZIP again.

## Storage and reconstruction

| Tables | Purpose |
| --- | --- |
| `sources`, `source_names` | SHA256, byte size, original filename(s), first successful import time, importer version |
| `rooms`, `room_snapshots` | Provider/room identity, per-ZIP name/time and mainline decision |
| `messages` | One row per logical provider/room/message ID |
| `message_versions` | Immutable body, parent, original sender, dates, and a hash of the retained representation |
| `auxiliary_blocks`, `room_auxiliary` | Typed data outside the body; message blocks belong to a specific version |
| `source_messages` | Exact message version and derived mainline membership for each ZIP |

Object keys are ordered before hashing auxiliary data; array/block order and string
values remain significant. Reimporting the same retained message does not add a
version. A changed body, parent, date, or retained auxiliary value creates a new
version without overwriting the old one. Discarded display text and tool arguments
or results are deliberately excluded from version identity.

There is no inferred global "latest message" across exports. Each source points to
its own version, so importing an older ZIP cannot overwrite another source's view.
Rebuild into a **new database path** by importing the same original ZIP set. Logical
content and hashes are reproducible; local row numbers, filenames, and import
timestamps are provenance and need not be byte-identical. Keep the originals.

Schema version 2 and SQLite application ID `BAG1` identify compatible databases.
Schema 2 stores original sender values; the earlier schema 1 stored display labels
and is rejected by this version. There is no automatic migration. Preserve the old
database, import the original ZIPs into a new database, and retain `seals.json`.
For a new database in the same directory, the existing seals apply automatically.
When rebuilding in another directory, preserve a copy of `seals.json` beside the
new database before reading it, along with `bag.config.json` if used. Seal decisions
cannot be recovered from the ZIPs, and changing speaker labels does not change
seal identities.

Importer versions cannot silently mix for the same source: use a new database when
its recorded importer version differs. No migration or deletion command is
included. All SQL values are parameterized; each ZIP uses one transaction and
reuses SQL commands.

## Architecture and memory

`ImportService` owns the immutable ZIP and transaction. `IExportReader` is the
provider boundary; `ClaudeExportReader` and `ChatGptExportReader` implement it. `IImportSink` receives
rooms/messages, `SqliteStore` handles identity and persistence, and
`MainlineSelector` computes the graph rule separately.

The Claude reader makes two streaming passes over the ZIP entry, keeping room headers and
tool-name mappings so that JSON property order and forward tool references work.
It buffers one message or one room metadata value at a time, **not an entire
conversation or export**. Memory also includes all room headers/tool-name mappings
and the current room's graph. It is not constant-memory: a huge single message,
metadata value, or number of tool calls increases memory. The large-input test
below covers one synthetic shape only.

The ChatGPT reader also makes two streaming passes per selected entry, buffering one mapping node
or room metadata value at a time. It retains graph descriptors for mainline and
parent projection; memory therefore scales with graph size and the largest node.

## Verification and limits

Tests generate synthetic ZIPs and databases under `bag-tests` in the system
temporary directory. `BAG_TEST_ROOT` overrides that location. Test artifacts are
retained for inspection; tests do not delete them.
No real conversation is a fixture. Tests cover both importers, numbered ChatGPT
parts and atomic rollback across parts, two-stage retrieval,
Unicode/newlines/empty text,
branches, tool exclusion, unknown blocks, revisions, repeated imports, input
failures, source immutability, database rejection, locks, reconstruction, seals,
speaker configuration, readable JSON, and the MCP stdio protocol.

The `Category=Large` test creates one conversation with 5,120 messages and over
150 MiB of JSON, then imports it in a separate process with a measured working-set
bound. To run ordinary tests only, append `--filter 'Category!=Large'`.

The Windows updater has a separate regression harness for PowerShell 5.1 and 7:

```powershell
.\scripts\test-local-update.ps1 -Version10PublishPath "$env:TEMP\payload-v10" -Version11PublishPath "$env:TEMP\payload-v11"
```

Both prepared payload directories must be children of the current OS temporary
directory. For these file-replacement tests, the v1.0 payload may be the current
source published separately with `-p:Version=1.0.0`; that does not test historical
v1.0 application behavior. The harness creates fresh temporary installations,
always supplies `InstallPath`, and retains all artifacts. Its 12 cases cover
preview, upgrade, rollback, interrupted activation recovery, file locks, invalid
payloads, dependency paths, personal-data guards, and reparse points. It never
uses the real installed application or database.

CI definitions in `.github/workflows/ci.yml` build and run the synthetic suite on
`windows-latest` and `ubuntu-latest`. Those hosted workflow runs have not been
executed as part of this publication preparation; defining the jobs is not evidence
that both platforms passed.

Import, find, and MCP operation have been verified with **one real Claude export
(October 2026)**. Other export formats and variants remain unverified. Claude's
export structure is not treated as a stable public API. Alternate field names and ZIP layouts, very
large individual values, all attachment formats, crash/power-loss fault injection,
and non-Windows execution have not been verified locally. One real numbered ChatGPT export with parent-only mapping nodes and deeply nested
auxiliary metadata has also been verified against a copied database. Node identities,
bodies, topology, timestamps, speaker filters, repeat imports, and existing Claude
rows were compared without retaining real data in this repository. Synthetic
mapping trees cover other tested variants. There are no implicit schema
repairs or claims of reconstructing data absent from the export.

Seals are read filters, not encryption or erasure: the database and original ZIPs
still contain the data. MCP's 50-message cap bounds message count, not bytes; a
single body or many source records can still make a large response. Search scans
stored versions and has no full-text index or pagination. Supported input is
Claude and the bounded ChatGPT conversation-tree ZIP contracts described above.

## Repository

[mori-ikuri/memory-kaban](https://github.com/mori-ikuri/memory-kaban)

## License

MIT. See [LICENSE](LICENSE).
