# MasterLeague eF27

You run the club. You manage the squad, tactics, transfers, board, staff, and youth development. The app handles the rest of the league in the background. You only play your own fixtures — either directly in eFootball or by entering the final score yourself.

The important part is that your career actually carries over into the game. Transfers, squad changes, and player development are written back to the game database, so the team you manage in the app is the same team you play with in eFootball.

**The SQLite database is the single source of truth. The game files are only a projection of that state.**

After the initial world build, the app never treats the game files as authoritative. The career lives in the app database, and the game is updated from it when needed.

> Early build. The management simulation is stable, but automatic result capture from eFootball is still experimental.
>
> Modified game databases are intended for **offline play only**. Do not use an edited local database with Dream Team or any other online mode.

## Current status

| Phase | Scope | Status |
| ----- | ------------------------------------------------------------- | --------------------------------------------------------------------------------------- |
| 0 | Prove that database changes can be written back into the game | Passed 2026-08-19 ([phase 0 checklist](docs/phase0-checklist.md)) |
| 1 | Data foundation: SQLite seed and real game schemas | Schemas recovered; seed tools available in `tools/` |
| 2 | League engine (`ML.Core`) | Built and tested |
| 3 | Match result capture (`ML.Ingest`) | Built; can pre-fill score, scorers, player ratings, and match statistics |
| 4 | Writeback (`ML.Sync`) | Core writeback works; applied-state diffing is still being refined |
| 5 | Polish and final refinement | Not started |

## Documentation

The repository contains a large amount of technical documentation, so these are the best places to start:

| Document | What it covers |
| ------------------------------------------------------------------------------ | ---------------------------------------------------------- |
| [**docs/phase0-checklist.md**](docs/phase0-checklist.md) | The original proof that the game-side writeback works |
| [**docs/functional-spec.md**](docs/functional-spec.md) | Functional requirements and expected application behaviour |
| [**docs/decisions.md**](docs/decisions.md) | Important engineering decisions and changes in approach |
| [**docs/parity-matrix.md**](docs/parity-matrix.md) | Feature and implementation parity tracking |
| [**docs/ux-brief.md**](docs/ux-brief.md) | UI/UX direction for the desktop application |

The broader implementation plan is in [efootball-master-league-plan.md](efootball-master-league-plan.md).

Engineering rules, format knowledge, and repository-specific instructions are documented in [CLAUDE.md](CLAUDE.md).

## Project structure

| Path | Purpose |
| ------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/ML.Core` | The league simulation engine. Pure C#, with no I/O or package dependencies, so it can remain fully testable without eFootball installed |
| `src/ML.Data` | SQLite persistence using Dapper |
| `src/ML.Ingest` | Match-result ingestion from efootball-re's in-game stats host, including parsing, folder watching, fixture matching, and player-rating calculation |
| `src/ML.Sync` | Compares desired state against the last applied state and prepares game writeback |
| `src/ML.App` | The Avalonia desktop application. This is the main UI |
| `src/ML.Web` | Legacy Blazor UI. Frozen and deprecated; do not use it to record results |
| `tests/ML.Core.Tests` | Automated tests for the league engine |
| `tools/` | Python tooling for world creation, imports, format handling, extraction, and the proven game writeback pipeline |
| `tools/vendor/sider/` | Vendored Sider file-format and related game-format code |
| `tools/vendor/efootball-player-tool/` | Vendored CPK reader/patcher used through `tools/cpk_patch.py` |
| `tools/vendor/efootball-re/` | The in-game stats host used to export match data |
| `tools/data/` | Decoded `dt270` gameplay data, realism settings, and related patches |
| `data/` | Hand-maintained identity mappings, merges, and other data corrections applied during world creation |
| `docs/` | Format research, technical notes, specifications, reverse-engineering findings, and implementation documentation |
| `samples/` | Schema and sample data derived from real exports. Real game exports are not committed |
| `assets/` | Competition, club, and other artwork used by the application |

## Download and play

The packaged builds are available from the [Releases page](https://github.com/levordzyn/ml-football27/releases).

The release package contains the built application and its embedded Python environment. Extract it anywhere, run:

```text
Play Master League.bat
```

and create a new career.

### Your world is built from your own eFootball installation

A release does **not** ship with a pre-built world.

When you start a new career, `tools/first_run.py` builds the world from your own eFootball installation. It imports things such as:

- clubs and leagues
- players and squads
- game data from `dt200`
- club crests from the installed game files
- player thumbnails from the game

Installed mods such as EvoMod can take precedence where appropriate.

The first world build normally takes around a minute and does not modify the game installation.

If eFootball is not installed, the app can still create a world using generated badges and player avatars.

The generated world is stored in:

```text
build/game_world.db
```

Your careers live inside that database, so **keep the file if you want to keep your save**.

A release package can be produced from a checkout with:

```bash
tools/make_release.sh <version>
```

## Building and running

### Requirements

- Windows 10 or Windows 11
- .NET 8 SDK
- A newer .NET SDK is also fine as long as it can build the `net8.0` targets

For a career where you simply enter match results yourself, nothing beyond the above is required.

Build the solution with:

```bash
dotnet build
```

Run the tests with:

```bash
dotnet test
```

The repository-root `Play Master League.bat` script builds `ML.App` on first launch and then starts the application.

### Full eFootball integration

For the complete eFootball integration you also need:

- eFootball on Steam
- Python 3.11+ when running directly from a source checkout
- `Pillow`
- `numpy`
- `pycryptodome`

The packaged release already includes its own Python environment and the built stats host, so normal users do not need to install those dependencies separately.

A Rust toolchain is only required if you intend to rebuild the stats host itself from:

```text
tools/vendor/efootball-re/memprobe/host/
```

The prebuilt `dxgi.dll` is already included in the repository and release package, and the application can install it through the Settings screen.

## External files and data you must provide

Some inputs are intentionally not included in the repository because they are third-party software or game data that cannot be redistributed here.

Their locations are gitignored, so adding them locally will not modify your Git working tree.

| Item | Location | Used for |
| ------------------------------------------------- | --------------------------------------------- | -------------------------------------------------- |
| eFootball installation (Steam App ID `1665460`) | Your normal Steam installation directory | Phases 3 and 4, plus game integration |
| EvoMod | Install it according to its own documentation | Game content and installation order |
| Stats host rebuild toolchain | Optional; the built host is already supplied | Rebuilding the stats host if you want to modify it |
| Your own game exports, FM exports, and face packs | Repository root or `samples/` | Import and world-building tools |

### CPK handling

You do **not** need to install separate CPK utilities.

The project reads and patches eFootball `.cpk` archives through the vendored CPK/container code in:

```text
tools/vendor/efootball-player-tool/
```

Older tooling based on `cpkmakec.exe`, the eFootball WESYS Unzlib Tool, and `cricodecs` is no longer required.

To extract the game tables from `dt200` into a normal directory tree:

```bash
python tools/cpk_patch.py extract "<eFootball>/cpk/dt200_console_all.cpk" bins
```

The eFootball Player Editor itself is optional. It can still be useful for manually editing players or exporting CSV data.

## Offline play

A modified game database should only be used while the game is offline.

The project's offline setup uses your own `eFootball.exe` with a two-byte change that prevents the client from connecting to Konami's matchmaking host.

Specifically, the host string:

```text
pes22-game.cs.konami.net
```

is changed to:

```text
pes99-game.cs.konami.net
```

This prevents the client from reaching Konami and makes it start directly in offline mode.

No gameplay code is changed by this patch.

The modified executable is not redistributed. You patch your own copy locally while the game is closed:

```bash
python tools/exe_patch.py apply tools/data/patches/offline-no-gameplay-change.json
```

To remove the patch:

```bash
python tools/exe_patch.py remove <same spec>
```

Steam's **Verify integrity of game files** will also restore the original executable.

The first time the patch is applied, the tool keeps a byte-exact backup of the original executable.

## Operational rules

These rules are important because the game files are treated as a deployment target, not as the career database.

### Installation order

After every Konami update, use this order:

**Konami update → EvoMod → your CSV / database changes**

Do not change the order.

### Always back up before applying

Back up `Player.bin` before every writeback.

The tooling creates its own backup as well, but maintaining your own copy gives you an additional recovery path.

### Never write without a round-trip check

The tools do not write modified data unless they can first prove that the source format can be rebuilt byte-for-byte when left unchanged.

If an unmodified file cannot survive that round-trip check exactly, the write operation is stopped.

That failure is intentional.

### `PlayerAssignment.bin` is positional

`PlayerAssignment.bin` is not a normal table where `TeamID` can simply be edited.

For transfers, the implementation swaps which player occupies a record. It does **not** move records around or directly rewrite the `TeamID` field.

### Never rebuild the entire CPK

The deployment process patches only the files that actually changed.

If the modified file still fits in its existing location, it is patched in place. Otherwise, the file can be relocated and only the required TOC entry is updated.

The rest of the archive is checked byte-for-byte so unrelated data remains untouched.

## Core design principle

The most important architectural decision in the project is simple:

**SQLite is the career. The game is the projection.**

The app owns the state of:

- clubs
- squads
- transfers
- contracts
- budgets
- fixtures
- results
- player development
- season history
- and everything else that makes up the career

The game only receives the state required to make the next match playable.

This distinction matters because eFootball can update, replace, or reorganize its own files. Mods can also replace game archives. Those changes should not destroy the career itself.

The career therefore remains in SQLite and can be reapplied to a fresh game installation when necessary.

## Licence

This project is licensed under **GPL-3.0**. See [LICENSE](LICENSE).

The license choice is intentional.

`tools/vendor/sider/` contains file-format and WESYS-related code from [Efootball-Sider](https://github.com/Master-Antonio/Efootball-Sider), which is GPL-3.0. `tools/ml_apply.py` links against that code, so the project is treated as a derivative work.

See [tools/vendor/sider/VENDOR.md](tools/vendor/sider/VENDOR.md) for the relevant details.

The same applies to the vendored eFootball Player Editor container code:

[tools/vendor/efootball-player-tool/VENDOR.md](tools/vendor/efootball-player-tool/VENDOR.md)

and the efootball-re stats host:

[tools/vendor/efootball-re/VENDOR.md](tools/vendor/efootball-re/VENDOR.md)

Do not add code or dependencies under a license that is incompatible with the project's GPL-3.0 requirements.

The Sider `dxgi.dll` runtime itself is not redistributed here.

eFootball is a Konami product. This project is independent and is not affiliated with, sponsored by, or endorsed by Konami.
