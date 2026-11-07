# NTE Omikuji

A Windows desktop app that decodes your own Neverness to Everness pull history
and achievements from game traffic into JSON for trackers. Built with .NET MAUI
Blazor Hybrid.

## Using it

Download `NteOmikuji.App.exe` and double-click it. There is nothing to install:
no .NET, no Npcap, no Python.

1. Open **開始擷取** and start it before pressing Start on the game's main menu.
   Windows asks for Administrator approval, because reading network packets
   requires it.
2. Open the in-game history and scroll down from page 1.
3. Everything saves automatically to `user_data` beside the exe. **總覽** and each
   pool's page update as pages arrive.

The top bar holds 開始擷取, 擷取說明, 原始紀錄, and 帳號. The left side lists 總覽
and the three pools, and 限定棋盤 and 弧盤研募 expand into each event.

Keep the exe in a writable folder of its own, because it creates these beside itself:

| Folder | Contents |
| --- | --- |
| `mappings\rewards.csv`, `achievements.csv` | Editable display names and ranks. Saving applies them at once and also corrects saved history. |
| `mappings\banners.csv` | Each event's featured rewards, which decide what is limited on it. |
| `mappings\schedule.csv` | Announced events and their periods, shown on 總覽 until they end. |
| `user_data\<uid>_<banner>.json` | Cumulative history per account, in version 1 export format. Reloaded at start. |
| `user_data\<uid>_<banner>_partial_<time>.json` | A capture that missed page 1, kept apart from the cumulative file. |
| `user_data\backups` | The previous version of a history file, copied before each session first changes it. The newest 10 per file are kept. To restore one, close the app and copy it back under its original name. |
| `exports` | CSV files from **原始紀錄** and images from **分享圖**. The CSV files leave out your UID, and cells that look like spreadsheet formulas are prefixed with `'`. |

The import control takes `.flows` captures, exported JSON, mapping CSV or JSON, and
the `records.json` kept by NTE_Gacha_Exporter, and tells them apart by content. None
of these need Administrator approval.

A `records.json` only fills gaps: its rows are added at times your history has none,
and anything this app captured stays as it was, even if the two differ. A later
capture of such a time replaces the added rows. Its boards name no event, so added
limited board rows count toward the pool and its pity but belong to no event's page.

### Maintaining mappings

Open **獎勵與卡池設定 > 獎勵** in the left list to edit names, rarity, and each reward's category. Each change
saves at once and also corrects saved history, so no app update is needed when the
game adds rewards:

- Rewards seen in your history without a mapping appear at the top as 未對應.
  Filling in any field creates their row.
- **新增語言** adds a language by code, such as `pt-br`. Names fall back to English,
  then Chinese, then any language, so a gap never hides a reward.
- **復原** steps back through this session's changes.
- **從內建補齊** adds rows and fills blank cells from the data bundled with the
  exe. It never changes a filled cell.

`rewards.csv` can also be edited directly, and changes appear in the open app.
Saving as either "CSV UTF-8" or plain "CSV" from Excel works, but Excel locks the
file, so close it before editing in the app.

| Column | Meaning |
| --- | --- |
| `id` | The game's string for the reward. Matched case-insensitively. |
| `type` | `character`, `arc`, `cosmetic`, `item`, or any other text. |
| `rank` | `S`, `A`, `B`, or any other text. |
| `focus` | The category below, as a code or its Chinese label. Blank sorts it automatically. |
| `name.<locale>` | The name in that language. Add a column to add a language. |

| Category | `focus` code | In the analysis |
| --- | --- | --- |
| S 角色 | `s_character` | Counts as S and resets the pull interval |
| S 弧盤 | `s_arc` | Counts as S and resets the pull interval |
| S 限定角色 | `s_limited_character` | Also counts toward the limited totals |
| S 限定弧盤 | `s_limited_arc` | Also counts toward the limited totals |
| 角色服裝 | `outfit` | Counted and listed only |
| 車皮 | `vehicle_skin` | Counted and listed only |
| 降落傘 | `glider` | Counted and listed only |
| 不關注 | `none` | Ignored |

Replies have no limited flag, so **獎勵與卡池設定 > 活動卡池** lists the featured reward
of each event. A reward is limited on the event that features it and an ordinary S
on any other, whatever its category says. New events in your history appear there
as 未設定 until you fill them in, and until then they follow the category.

`schedule.csv` holds global server names and dates from announcements, with
`estimate` set where the end date is projected. It only informs the 總覽 preview.
A new event's `banner_id` stays blank, since its real ID is unknown until a capture
shows it, and that first capture lists it as 未設定.

A blank category follows the reward: `Fashion_Glide_` is 降落傘, `Fashion_vehicle_`
is 車皮, any other `Fashion_` is 角色服裝, and S-rank characters and arcs are S 角色
and S 弧盤. The data has no signal for limited rewards, so those are set by hand.

Exports always write the English name when there is one, because trackers expect
it; the display language only affects this app. A file that cannot be used shows
a warning and the previous mappings stay active. Language packs can be imported
as CSV, or as JSON with `{"id": {"names": {"ja": "..."}, "rank": "S"}}`.

Upgrading an older `rewards.csv` keeps the original as `rewards.csv.v1.bak` or
`rewards.csv.v2.bak`.

### Analysis

**總覽** shows how close each pool is to its guarantee, a comparison table, and the
latest S rewards. Each pool shows pulls since the last S, total pulls, S rate,
interval average, median and spread, the rarity mix, pulls over time, and
most-obtained rewards. Boards also show dice rolls and result types.

- S means the four S categories in the mapping table. Limited totals appear once
  a reward is marked limited.
- Only dice rows count as pulls on boards. The 集點贈禮 given every 10 pulls and
  沉眠地 rewards do not.
- A reward's category applies however it arrived, and anything other than a dice
  roll is tagged with its source.
- Files saved before 0.3.0 misread many pulls as 集點贈禮. They hold no bytes to
  correct from, so the analysis says so when a file has more of these than the
  game gives. Capturing the history again corrects it.
- The oldest S is shown as "至少 N" and left out of the average, because earlier
  pulls may predate the saved history.
- 總覽 and each pool show a luck tag, from 非酋 through 正常 to 歐皇. It compares the
  rewards obtained with the exact mean and standard deviation for that many pulls:
  S on boards, the limited reward on Arcs. Tags change at ±0.5, ±1, and ±2σ. Rates
  come from published guides, since no in-game figures were available: 0.99% on
  boards and 19.59% after 70 pulls, and 3% on Arcs. These reproduce the published
  averages of about 53.5 pulls per S and 59.5 per limited Arc. A misread history or an
  unset event shows no tag.
- 前 N% follows the σ. It ranks all the outcomes that many pulls can bring, exactly:
  more rewards is better, and with the same count, the last one landing earlier is
  better. Pools merge as normal scores weighted by their expected counts. It compares
  against the odds, not other players, and the tag itself still comes from the σ.
- Under one full guarantee of pulls a tag is still worked out, and marked 樣本少. The
  formula holds there, but a single reward decides it: no S in 10 pulls is 正常, and
  an S in 10 pulls is 歐皇.
- Each event also has a luck tag, on its page and in the 各期運氣 table on its pool's
  page. Since pity carries over, its expected count starts from the pulls already
  made when the event began, shown as 開始已墊, with 結束已墊 for those it leaves to the
  next. Share images show both for an event. This assumes an event's pulls run together, so it is approximate if you alternated
  between two events. Events inherit from each other, so their tags do not add up to
  the pool's.
- Guarantees are built in: 90 pulls for an S on both boards with better odds from
  pull 70, and on Arcs 60 for an S and 80 for a limited one, with an A every 10 on
  all three. A board's A is the 集點贈禮 itself. An event's page counts only its own
  pulls, while intervals run across events because pity carries over.
- The rarity mix counts S characters and Arcs apart from S items and cosmetics,
  since items also carry an S.
- An Arc is limited when its event features it. Luck, 最近的 S, and share images count
  an Arc pool up to the limited reward, past any ordinary S. The Arc page shows first
  how many pulls each limited Arc took, how many came from the 80 pull guarantee, and the limited
  share among the rest, for comparing with the stated 25%.
- Everything stays on this computer, so there is no global luck comparison.

### Share images

**分享圖** in the left list draws an image to save as PNG or copy to the clipboard:

| Template | Size | Shows |
| --- | --- | --- |
| 總覽 | 1920×1080 | Overall luck and all three pools with their pity and S history |
| 運氣 | 1080×1080 | The luck tier and where it falls on the bell curve, overall or for one pool or event |
| 單池 | 1080 wide, up to 1350 tall | One pool's or event's figures and how many pulls each S took |

**分享抽卡** on a pool's or event's page opens 單池 with it selected, and **分享運氣**
opens 運氣 the same way. An event's
image counts only that event's pulls and rewards, but each reward's pull count runs
back across earlier events because pity carries over, and it has no pity meter of
its own.

Each comes in a pale 櫻 and a dark 墨 color scheme. Your UID is left out unless you
tick the box, and then its middle is masked.

### Mystery Box

Captures read the layout used since version 1.4, where each request and row names
its pool, as well as the older one. All pools share one file, with each row's pool
in `pool_id`, and they appear under **原始紀錄**. A real capture confirmed the layouts
and five rows per page, with one pool that had pulls and one that had none. The game
first asks how many pages a pool has, and requests no pages when the answer is 0, so
a pool without pulls leaves no rows. Two pools with pulls have not been captured
together, and the pool's size and rules are not decoded.

### Recording packets for debugging

Tick **記錄除錯封包** before **開始擷取** to check decoded rows against the bytes.
It is off each time the app starts. Each capture writes
`debug_packets\packets_<time>.jsonl` beside the exe, one JSON object per line:

- A header line, then one line per recorded packet.
- `direction`, `role`, `remote`, `local_port`, `length`, and `payload_hex`.
- Requests add `kind` and `page`. Replies add the decoded `pages` and `rows`,
  including each row's `record_hex`.

Only history requests over UDP, and inbound UDP on the same stream within 5 seconds
of one, are written, so the `other` role can hold unrelated game data. A request that
resembles Mystery Box history but matches no known layout is written as
`unrecognized_request`, so a new layout can still be studied. TCP, which
carries login traffic, is left out. The file may still identify your account, so
check it before sharing. A file stops at 64 MB, and any writing problem ends the
log without interrupting the capture.

Notes:

- Requires 64-bit Windows 10 1809 or later and the WebView2 runtime, which ships
  with Windows 11 and is normally present on Windows 10.
- The exe is about 93 MB. Its first start unpacks to `%TEMP%\.net`, and its
  browser profile lives in `%LOCALAPPDATA%\NteOmikuji`.
- If Npcap is already installed, the app uses it and skips the Administrator prompt.
- The exe is unsigned, so SmartScreen may warn about it.

## Building

Requires the .NET 9 SDK and `dotnet workload install maui-windows`.

```powershell
dotnet test tests\NteOmikuji.Core.Tests
dotnet run --project src\NteOmikuji.App
dotnet publish src\NteOmikuji.App -p:PublishProfile=Portable
```

The last command writes the single exe to `publish\NteOmikuji.App.exe`. Only
that file needs distributing.

## Releasing

`.github/workflows/release.yml` builds and tests every push and pull request on
Windows. To publish, raise `<Version>` in `Directory.Build.props` and push to
`main`. If that version has no release yet, the workflow creates tag `v<version>`
with the exe and its SHA-256, and generated notes. A version with a suffix, such
as `0.3.0-beta.1`, becomes a prerelease. Other pushes only produce a build
artifact.

## Layout

| Path | Purpose |
| --- | --- |
| `src/NteOmikuji.Core` | Decoders, session, export, flows import, capture backends, bundled mappings. No UI. |
| `src/NteOmikuji.App` | MAUI Blazor Hybrid shell. Web files in `WebRoot` are embedded in the assembly. |
| `tests/NteOmikuji.Core.Tests` | Parity tests against Python output, plus mapping, library, analysis, and robustness tests. |
| `ref` | The original Python exporter, kept for reference only. Nothing builds from or depends on it. |

`Data/python_golden.json` in the tests is output captured from the Python
exporter. `src/NteOmikuji.Core/Mappings` holds the CSVs the app seeds on first
run. Reward names come from [NTE_Assets](https://github.com/Waifus-Grace/NTE_Assets)
at commit `1816e23`. They are not updated automatically; a new reward still
exports, flagged as unmapped, until someone maintains it.

The interface uses pale Morandi tones centered on dusty rose, defined in
`WebRoot/app.css`. Luck tags use Japanese traditional colors: golds for the lucky
tiers and brown-blacks for the unlucky ones.

## Arbitrary result strings

The Python decoder finds Monopoly rewards by known prefixes (`fork_`, `Dice`,
`Fashion_`, and `10xx` character IDs) and accepts only identifier characters.
A new reward outside those patterns exports with an empty ID. Here:

- Packed text is decoded by undoing its bit shift, which accepts any UTF-8.
- A record's reward is found from its length-prefixed string, not its prefix.
  Known prefixes remain only as a fallback for damaged headers.
- Structured blocks also read UTF-16 and empty Unreal `FString` values.
- Mappings only add display names. Unmapped rewards keep the game's text and are
  flagged in the UI.
- When a structured row's result text differs from its reward, the JSON gains an
  optional `result_text` field. Everything else matches format version 1.

UTF-16 handling follows the Unreal `FString` convention and is covered by
synthetic tests only; it has not been observed in real game traffic.

## Differences from the Python exporter

- `exporter.name` is `nte-omikuji`. UIDs are unchanged.
- Windows only, with no Linux or macOS capture backends.
- wpcap.dll loads only from System32 by absolute path, and clipboard access uses
  the platform API rather than `tkinter` or `clip.exe`.
- TCP reassembly and LZ4 output have size limits, and the flows reader has a
  nesting limit.
- Diagnostics sidecars, CSV debug output, and the update check are not ported.
- Board rolls and quantities come from each row's parsed fields. The Python parser
  stops before the row's trailing `Lottery_*` banner, so its blocks fail to parse
  and it scans for the roll instead, which labels real pulls `points_gift`
  cannot be parsed still falls back to that scan.
- `result_type` keeps the name `points_gift` for the reward given every 10 pulls.
