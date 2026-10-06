# KLink-dotnet selective merge audit

Source repository: `https://github.com/Xuewu-awa/KLink-dotnet`

Inspected commit: `c5b71210183145c9ef989fb202c9ab8f1c573190`

Inspection date: 2026-10-05

## Decision

No `KLink.Bot` source was copied into this project. The current project contains
newer local work (dynamic card-id aliases, RNG/cursor tracing, replay auditing,
and the recent trigger fixes), while the inspected checkout is an older snapshot
of the same Bot implementation. Replacing files from it would discard those
changes.

## Useful findings

### Server option shape

`KLink.Server/Http/ApiHandler.cs` shows the wire shape used by the launcher/server:

- `cards_blacklist` is an array of objects with `card_type` and `end_date`.
- `server_options` is serialized as a JSON string.
- `reserve_changes` is an array in `ServerOptions`.
- `draft_card_limits` contains `blacklist` and `whitelist` string arrays.

The checked-in server implementation returns an empty `reserve_changes` array and
contains one dated blacklist entry (`card_unit_8th_cavalry_regiment`). These are
server-version data, not reliable offline defaults for the rule kernel, so they
were not added to `CardPoolTable`.

### Card pool implementation

`KLink-dotnet/KLink.Bot/Cards/CardPoolTable.cs` confirms the same three-stage
candidate filtering model already used here: card set, reserved status, then
server blacklist. It does not provide a missing live blacklist/reserve table.

### Deck-code table

The dotnet server asset `KLink.Server/Assets/kards-server/deck_code_ids.json`
contains 1299 entries, while the current live table contains 2499 keys. Of the
overlapping mappings, 992 conflict. The current table must remain authoritative
because it is generated from the newer online pak table. The dotnet table is
therefore reference data only, not a merge source.

### Desktop application

`KLink.App` is a WPF launcher/server-management application. It is outside this
repository's BotSim/rule-kernel scope and was not imported. Its server/proxy
code can be revisited separately if a launcher or local-server integration is
requested.

### Build status of the inspected checkout

After restoring packages, `dotnet build KLink.sln -c Release` still fails in the
inspected checkout because `KLink.Server` references a missing `KLink.Server.Data`
namespace and `AppDatabase` type. This confirms that the checkout is not a
self-contained drop-in server source tree; its server files were not copied.

## Follow-up requirement

To resolve the remaining random candidate-pool discrepancy, capture a real
`server_options` response (including current blacklist/reserve state) or a full
candidate-list observation. Hard-coding the inspected server snapshot would
make replay behavior date- and server-version dependent.
