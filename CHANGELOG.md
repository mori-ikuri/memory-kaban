# Changelog

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
