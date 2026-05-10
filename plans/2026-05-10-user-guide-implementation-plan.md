# User guide documentation implementation plan

## Goal
- Create a detailed end-user guide linked from `README.md` so `README.md` can stay focused on high-level feature descriptions and important configuration entry points, while the guide explains each Automatics feature in practical detail.
- Preserve the user's stated role split: "READMEは各機能の大まかな説明とコンフィグなどの重要な説明、ガイドは各機能についての詳細な説明".

## Verified facts and sources
| Claim | Evidence | Source | Impact |
| --- | --- | --- | --- |
| The repository already has a `docs/` directory with topic-specific user documentation. | `docs/add-user-defined-object.md`, `docs/custom-icon-pack.md`, and image assets are present. | Local investigation: `rg --files`, `find . -maxdepth 3 ...` | The new guide should live under `docs/` and link to the existing topic pages instead of replacing them. |
| `README.md` currently mixes feature summaries, detailed console command reference, configuration links, and the display/internal name matching explanation. | Headings include `Features`, `Console commands`, `Configurations`, `Supplementary explanation`, `Languages`, `Contacts`, `Credits`, and `License`. | Local investigation: `README.md` | README can be shortened by moving detailed usage/reference material into the guide while keeping key links. |
| The documented feature modules are Automatic door, Automatic mapping, Automatic processing, Automatic feeding, Automatic repair, Automatic mining, and Automatic pickup. | README feature headings and module directories/config classes match these seven modules. | Local investigation: `README.md`, `Automatics/Automatic*/Config.cs` | The guide must include one detailed section for each module. |
| The config reference is already exhaustive and separate. | `CONFIG.md` contains sections `[ #1 System ]` through `[ #9 Automatic Pickup ]` and lists individual config entries, defaults, and acceptable values. | Local investigation: `CONFIG.md` | The guide should explain how config options affect usage, not duplicate the entire `CONFIG.md` table. |
| Each feature can be disabled through a module-level `module` config entry before feature-specific entries are bound. | Each module config binds `module` and returns early when disabled. | Local investigation: `Automatics/AutomaticDoor/Config.cs`, `Automatics/AutomaticMapping/Config.cs`, `Automatics/AutomaticProcessing/Config.cs`, `Automatics/AutomaticFeeding/Config.cs`, `Automatics/AutomaticRepair/Config.cs`, `Automatics/AutomaticMining/Config.cs`, `Automatics/AutomaticPickup/Config.cs` | README and guide should keep the "Disable Module" behavior visible as an important configuration concept. |
| Automatic processing supports per-piece process modes. | `Processor.cs` defines `Craft`, `Refuel`, `Store`, and `Charge` and lists supported processors such as beehives, smelters, cooking stations, torches, sap extractors, eitr refinery, and Ballista. | Local investigation: `Automatics/AutomaticProcessing/Processor.cs` | The guide should include a compact supported-piece/process table or equivalent per-piece explanation. |
| Automatics registers four user-facing console commands. | `Commands.Register()` registers `automatics`, `printnames`, `printobjects`, and `removemappins`. | Local investigation: `Automatics/Commands.cs`, `Automatics/ConsoleCommands/*.cs`, `Automatics/Languages/en_us.json` | Detailed command usage can be moved or linked from the guide, with a concise README pointer. |
| `distributor/thunderstore/README.md` is a separate release asset with absolute versioned links. | The file differs from `README.md`, uses `https://github.com/eideehi/valheim-automatics/blob/1.6.0/...`, and `Automatics.csproj` packages it into the Thunderstore zip. | Local investigation: `distributor/thunderstore/README.md`, `Automatics/Automatics.csproj` | Any README role/link changes should be mirrored in the Thunderstore README using the existing absolute-link style. |
| The current package version visible in release metadata is `1.6.0`. | `distributor/thunderstore/manifest.json` has `"version_number": "1.6.0"`. | Local investigation: `distributor/thunderstore/manifest.json` | Thunderstore links added in this slice should follow the current versioned-link pattern, with release-time tag verification left to the release process. |
| Project instructions forbid editing `Automatics/Libraries/mod-utils` unless the task explicitly updates the submodule. | `AGENTS.md` states the dependency is read-only. | Primary source: user-provided `AGENTS.md` instructions | The documentation slice must not edit the submodule. |

## Requirements
- In scope:
  - Add a durable `docs/user-guide.md` page in English, matching the existing README/docs language.
  - Link the guide from `README.md` near the feature overview and from relevant feature references.
  - Keep `README.md` as the concise entry point: what the mod is, key cautions, feature overview, important configuration concepts, and links to detailed docs.
  - Keep `CONFIG.md` as the exhaustive config reference and link to it from both README and guide.
  - Cover every current user-facing feature module in the guide: Automatic door, Automatic mapping, Automatic processing, Automatic feeding, Automatic repair, Automatic mining, and Automatic pickup.
  - Include common concepts needed across features, especially `Disable Module`, display-name vs internal-name matching, user-defined objects, shortcuts, message display settings, and console commands.
  - Preserve links to existing specialized docs: `docs/add-user-defined-object.md` and `docs/custom-icon-pack.md`.
  - Update `docs/custom-icon-pack.md` if the matching explanation anchor moves from README to the new guide.
  - Update `distributor/thunderstore/README.md` consistently with the README changes, using existing absolute versioned-link conventions.
- Out of scope:
  - Changing mod runtime behavior, config defaults, localization strings, generated binaries, package version, release tags, or images.
  - Rewriting `CONFIG.md` except for link-only changes if a link becomes necessary.
  - Editing `Automatics/Libraries/mod-utils`.
  - Adding new screenshots or in-game tutorials beyond links to existing image-backed docs.
- Constraints:
  - Do not duplicate all of `CONFIG.md`; guide text should explain usage and point to config keys/reference where useful.
  - Do not claim behavior that cannot be verified from existing docs, config, localization, or implementation.
  - Use concise user-facing prose and preserve existing terminology such as `Automatic mapping`, `Allow Pinning`, `Pickup All Nearby`, and `Disable Module`.

## Ambiguities, questions, and decisions
- Item: Guide language.
- Options or decision: Use English for `docs/user-guide.md`.
- Evidence: Existing durable docs and release README are English.
- Recommended path: Proceed in English unless the user explicitly asks for a localized Japanese guide.

- Item: Where to put the console command reference.
- Options or decision: Move detailed command usage from README into the guide, and leave a short README section that lists the commands and links to the guide.
- Evidence: README currently carries the full command option tables, while the requested role split says README should stay high-level.
- Recommended path: Move or condense command detail as part of the same docs slice.

- Item: Where to put the display-name/internal-name matching explanation.
- Options or decision: Put the full explanation in the guide under a common concepts section, keep a short README pointer if needed, and update `docs/custom-icon-pack.md` to link to the new guide anchor.
- Evidence: The concept applies to multiple feature configurations and custom icon targets.
- Recommended path: Move the detailed table to the guide so README remains concise.

## Acceptance criteria
- `docs/user-guide.md` exists and is reachable from `README.md`.
- `docs/user-guide.md` has one clearly named section for each current feature module: Automatic door, Automatic mapping, Automatic processing, Automatic feeding, Automatic repair, Automatic mining, and Automatic pickup.
- Each feature section explains, in user terms:
  - what the feature automates;
  - what the player does to use or trigger it;
  - the important config groups or keys that change behavior;
  - target/allowlist behavior when applicable;
  - shortcuts, toggle messages, or interval behavior when applicable;
  - relevant cautions or limitations already supported by local evidence;
  - related links to `CONFIG.md` or specialized docs when useful.
- The guide includes Automatic processing coverage for the four process types `Craft`, `Refuel`, `Store`, and `Charge`, and identifies the supported processor pieces using `Processor.cs` or `CONFIG.md` as source.
- The guide includes Automatic mapping coverage for dynamic objects, static objects, locations, portals, map navigation, saved static pins, destroyed-object cleanup, user-defined objects, and custom icon packs.
- The guide includes common configuration concepts: `Disable Module`, display-name/internal-name matching, user-defined object fields, and the distinction between guide explanations and the exhaustive `CONFIG.md` reference.
- README still includes a high-level feature overview and important configuration links, but no longer needs to carry full per-command option tables or the full matching example table if those are moved to the guide.
- Existing links to `CONFIG.md`, `docs/add-user-defined-object.md`, `docs/custom-icon-pack.md`, and image assets remain valid after the edit.
- `distributor/thunderstore/README.md` reflects the same guide/link structure as `README.md`, using absolute links consistent with the current `1.6.0` release-link style.
- No files under `Automatics/Libraries/mod-utils` are changed.
- `git diff --check` passes.

## Test plan
- Acceptance tests:
  - Inspect `docs/user-guide.md` and confirm all seven feature headings are present.
  - Compare the guide feature list against README headings and `Automatics/Automatic*/Config.cs`.
  - Confirm README links to the guide and still links to `CONFIG.md`.
  - Confirm the Thunderstore README includes an absolute link to the new guide.
- Regression tests:
  - Run `git diff --check`.
  - Use `rg -n "docs/user-guide.md|user-guide|CONFIG.md|add-user-defined-object|custom-icon-pack"` over README, `docs/*.md`, and `distributor/thunderstore/README.md` to inspect link changes.
  - Use `rg -n "^### Automatic|^## \\[ #"` on README/CONFIG/guide to verify no feature module disappeared from documentation.
- Negative and edge cases:
  - Verify no obsolete README anchor is left in `docs/custom-icon-pack.md` after moving the matching explanation.
  - Verify the guide does not state unverified in-game timing, multiplayer behavior, or compatibility claims beyond existing warnings.
  - Verify no config defaults are changed or restated incorrectly.
- Manual or visual checks:
  - Read the rendered Markdown flow for README and guide: README should be scannable, and the guide should be detailed enough to answer feature-specific usage questions.
  - No .NET build is required for a docs-only change. If a non-doc file is edited unexpectedly, follow the repository instruction and use `dotnet msbuild`, not `dotnet build`.

## Skill usage plan
- Skill: `vibe-plan-execution`
- Availability source: Current session skill metadata lists `/home/eideehi/dev/mods/valheim-automatics/.agents/skills/vibe-plan-execution/SKILL.md`.
- Use when: The user asks to implement or continue from this plan.
- Matching reason: Its description matches executing an existing implementation plan.
- Fallback if unavailable: Follow this plan directly, re-check local facts, and stop if the proceed condition is blocked.

- Skill: `writing-style-guide`
- Availability source: Current session skill metadata lists `/home/eideehi/dev/mods/valheim-automatics/.agents/skills/writing-style-guide/SKILL.md`; the file was read during planning.
- Use when: Drafting or editing `docs/user-guide.md`, `README.md`, and `distributor/thunderstore/README.md`.
- Matching reason: The planned work produces user-facing docs and README prose.
- Fallback if unavailable: Keep prose concise, preserve existing facts and terminology, and avoid unsupported claims.

## Implementation plan
1. Re-check the current docs and source facts before editing: `README.md`, `CONFIG.md`, `docs/add-user-defined-object.md`, `docs/custom-icon-pack.md`, all `Automatics/Automatic*/Config.cs`, `Automatics/AutomaticProcessing/Processor.cs`, `Automatics/Commands.cs`, and command localization keys in `Automatics/Languages/en_us.json`.
2. Create `docs/user-guide.md` with this outline:
   - `# User guide`
   - `## How to use this guide`
   - `## Common configuration concepts`
   - `## Automatic door`
   - `## Automatic mapping`
   - `## Automatic processing`
   - `## Automatic feeding`
   - `## Automatic repair`
   - `## Automatic mining`
   - `## Automatic pickup`
   - `## Console commands`
   - `## Related references`
3. Draft each feature section from verified docs/config/source behavior. Prefer concise explanations plus links to `CONFIG.md` over copying every default value.
4. Move or recreate the display-name/internal-name matching explanation in the guide under common configuration concepts. Keep the existing examples if still accurate.
5. Move or condense the full console command reference into the guide. Leave README with a short command list and link to the guide.
6. Update `README.md`:
   - Add a prominent link to `docs/user-guide.md` near `Features`.
   - Keep each feature's high-level summary.
   - Keep the Configuration Manager recommendation, `CONFIG.md` link, and user-defined object/custom icon pack links.
   - Remove or condense details now covered by the guide.
7. Update `docs/custom-icon-pack.md` links if its matching-explanation target moves from README to the guide.
8. Update `distributor/thunderstore/README.md` to mirror the README structure and add an absolute versioned link to the guide, following the existing `1.6.0` URL style.
9. Run the verification steps in the test plan.
10. Do a final diff review against the acceptance criteria and remove any unsupported wording or accidental scope expansion.

## Commit checkpoints
- Commit checkpoints are omitted because this is a single documentation slice. If the user asks for a commit after verification, make one docs-only commit after all acceptance criteria pass.

## Risks and unproven items
- Item: Some user-visible behavior details may require more than config inspection.
- Evidence label: `Unproven`
- Impact: The guide could overstate runtime behavior if it describes interactions not plainly supported by code or existing docs.
- Fastest proof path: Before drafting each feature section, inspect the corresponding implementation files and localization strings; use in-game verification only for details that remain ambiguous.
- Revisit trigger: Any sentence that describes exact player interaction, timing, target selection, inventory priority, or failure behavior without direct source support.

- Item: Thunderstore absolute links rely on the release tag matching the package version.
- Evidence label: `Unproven`
- Impact: Newly added guide links in `distributor/thunderstore/README.md` can break after release if the tag is missing or the version changes without updating links.
- Fastest proof path: Before release, verify `distributor/thunderstore/manifest.json` version and the matching Git tag, following `AGENTS.md`.
- Revisit trigger: Any release/version bump or package preparation.

- Item: Whether users prefer the detailed guide to include full config defaults.
- Evidence label: `Unproven`
- Impact: Duplicating defaults would make the guide longer and create drift risk; omitting them may require users to open `CONFIG.md`.
- Fastest proof path: Implement the guide with conceptual explanations and links to `CONFIG.md`; revise only if review shows important setup decisions are hard to find.
- Revisit trigger: User feedback that guide sections are not actionable without default values.

## Implementation handoff
- When implementing this plan, treat this document as authoritative. Re-check local facts before editing, follow the acceptance criteria, test plan, and skill usage plan, implement only the current in-scope slice, and stop if the `Proceed condition` is blocked or local evidence contradicts the plan.

## Proceed condition
- Ready to implement. No user decision is required before the first documentation slice, provided the guide is written in English to match existing docs and README changes are kept to the requested role split.
