# Goblin v1 — exploratory generated art

Status: prototype-only; this is **not** the final art direction.

Runtime files live in `src/DungeonFortress.Game/assets/generated/goblins/`:
`goblin_idle_v1.png`, `goblin_work_v1.png`, `goblin_combat_v1.png`, and
`goblin_downed_v1.png`. They are transparent 96×96 PNGs used for crew and
raider character readability in the Godot projection.

## Reproduction record

- Date: 2026-07-27
- Tool: built-in OpenAI image generation
- Manual paint/editing: none
- Source: `call_1XyNBeXZYWqooldTTJ6Pn2Gs.png`, 1254×1254 RGB. SHA-256
  `2d2ed58e9cc01830b6be9ec3f9eca40ea4477b9196761c56665d333a0d09ee93`. Lives on
  the generating machine at
  `~/.codex/generated_images/019f9dd3-1365-7a02-82db-0cb8a1b7075f/`; outside Git
  per the decision recorded in [`PROVENANCE_VERIFIABILITY.md`](PROVENANCE_VERIFIABILITY.md)
  (Issue #179). The four runtime state PNGs are the project-bound finals.
- Chroma key: sampled `#f803f6`, processed by `remove_chroma_key.py` with
  auto-key border, soft matte, thresholds `12/220`, and despill.
- Post-process: each state cropped to non-transparent bounds, resized with
  LANCZOS to fit 84px, bottom-anchored in a transparent 96×96 canvas; no manual
  paint.

## Verifiability (Issue #179, 2026-08-02)

Post-processing steps and their executability from the repo:

1. **Chroma key** — executable. `scripts/art/remove_chroma_key.py` in this repo
   is byte-identical to the helper used (SHA-256 `7e512369...`). Run from the
   repo root on the source with the recorded flags:

   ```powershell
   python scripts/art/remove_chroma_key.py --input <source.png> --out <alpha.png> `
     --auto-key border --soft-matte --transparent-threshold 12 --opaque-threshold 220 --despill
   ```

   The helper's own run on the source (measured 2026-08-02) returns
   `Key color: #f803f6`, matching the sampled value recorded above.
2. **Crop, LANCZOS resize to 84px, bottom-anchor in 96×96** — not executable
   from the repo: performed with one-off inline Python/Pillow scripts that were
   not retained. The result is the committed `goblin_*_v1.png` finals (96×96
   RGBA), so the outcome is inspectable even though the exact step cannot be
   re-run.

Exact prompt:

> Use case: stylized-concept; Asset type: 2D top-down game character sprite sheet for a Godot prototype; one consistent small green dungeon goblin in exact 2x2 grid: idle, working with pick/hammer, combat-ready with short spear, downed; flat #ff00ff chroma-key background; pixel-art-inspired chunky hard-edged limited palette; readable at ~40x40; top-down three-quarter view; same scale/anchor; green skin, dark teal/brown clothes, orange accent; no shadows/text/logos/watermark/scenery.
