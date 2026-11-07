# Changelog

## Unreleased

- Accept ChatGPT mapping nodes with omitted `children` arrays by deriving child
  links from their parents. Preserve original node JSON, branch identities, and
  current-node selection; keep rejecting explicit invalid child-array types and
  leave contradictory or broken graphs unresolved. Add synthetic coverage for
  split exports, branch retrieval, malformed graphs, rollback, and reimport.
- Allow ChatGPT JSON documents up to 128 nesting levels, retaining the existing
  64-level limit for Claude and atomic rejection beyond either bound.
- Validate one real numbered ChatGPT export against a database copy, keeping all
  real conversation data outside the repository and all test fixtures synthetic.
- Keep application version 1.1.0 and database schema 2. Existing supported input
  retains its interpretation and version hashes.

## 1.1.0 (2026-10-27)

- Add a Windows application-only upgrade/rollback script and Japanese procedure.
  Stage the prepared v1.1 payload, preserve v1.0 before activation, retain both
  versions on rollback, and support a mutation-free `-WhatIf` preview. The
  procedure covers restarting Claude Desktop and reading the existing database.
- Support ChatGPT `conversations-N.json` parts inside one ZIP, in numeric order
  under one source identity and transaction. Reject mixed layouts, duplicate or
  gapped indexes, inconsistent directories, and duplicate rooms across parts.
  Add synthetic tests for late-part rollback, reconstruction, and ZIP immutability.
- Document trial import into a separate new database before using a real ChatGPT
  export. Existing installation/database paths are not changed by preparing files.
- Add read-only MCP `find_heads` for 80-Unicode-scalar previews and `get_messages`
  for batched exact saved-version retrieval. Keep the existing `find` envelope
  and behavior unchanged.
- Recheck seals on both retrieval steps, omit sealed and unknown keys alike, and
  preserve exact bodies, source provenance, and retained versions.
- Import ChatGPT conversation-tree ZIPs with explicit `--format chatgpt`, retaining
  all branches and typed auxiliary nodes. Derive mainline membership from
  `current_node`; preserve broken or unselected graphs without guessing a branch.
- Keep non-utterance and tool data outside searchable bodies, apply type/name-only
  tool redaction, and retain original graph identities and numeric timestamps.
- Distinguish ChatGPT speaker labels through provider-scoped configuration while
  keeping original roles in storage. Add `seal` / `unseal --provider chatgpt`.
- Add synthetic importer, retrieval, and mixed-provider regression coverage.

Database schema remains version 2. Existing v1.0 databases remain readable. The
same-source importer-version check remains in force: rebuild into a new database
from the original ZIPs when reimporting sources recorded under v1.0. Preserve seals
and speaker configuration. At this release point, ChatGPT verification used synthetic exports only; see
Unreleased for subsequent validation. Non-tree formats, nested ZIPs, and multi-ZIP
archives are not supported. The local
follow-up retains version 1.1.0 and schema 2; single-file message interpretation
and hashes remain unchanged.

## 1.0.0 (2026-10-26)

- Import Claude export ZIPs into a local SQLite archive while retaining message
  versions, branches, source identities, and metadata separately from message bodies.
- Find exact message bodies with combined time, room, speaker, and substring filters,
  with parent-linked context and human-readable or structured JSON output.
- Seal rooms or individual messages across all saved versions in a separate
  identifier-only sidecar. Reads exclude sealed content by default.
- Expose `list_rooms` and `find` through a read-only MCP server over standard input
  and output. MCP search responses contain at most 50 messages, including context.
- Use .NET 10, C# 14, stable System.CommandLine, and the official C# MCP SDK, with
  locked dependencies and application version metadata generated from project settings.
- Use standard .NET build output locations. Optional local build settings remain
  outside version control.
- Preserve original sender values in the database and apply configurable speaker
  labels only at the input and output boundaries.
- Emit ordinary Unicode characters directly in CLI and MCP JSON output.
- Add synthetic-data tests and CI definitions for Windows and Ubuntu.

Database schema version 2 replaces the earlier storage of display names with original
sender values. Earlier schema version 1 databases are rejected without modification.
Rebuild from the original export ZIPs into a new database, preserving the old database
and its separate seals file. Import continues to reject the same source when its
recorded importer version differs.
