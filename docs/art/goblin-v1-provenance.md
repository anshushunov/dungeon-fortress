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
- Source: one 2×2 sheet on a flat magenta background; the large source and
  intermediate alpha sheet remain outside Git to keep the prototype repository
  small. The four runtime state PNGs are the project-bound finals.
- Chroma key: sampled `#f803f6`, processed by `remove_chroma_key.py` with
  auto-key border, soft matte, thresholds `12/220`, and despill.
- Post-process: each state cropped to non-transparent bounds, resized with
  LANCZOS to fit 84px, bottom-anchored in a transparent 96×96 canvas; no manual
  paint.

Exact prompt:

> Use case: stylized-concept; Asset type: 2D top-down game character sprite sheet for a Godot prototype; one consistent small green dungeon goblin in exact 2x2 grid: idle, working with pick/hammer, combat-ready with short spear, downed; flat #ff00ff chroma-key background; pixel-art-inspired chunky hard-edged limited palette; readable at ~40x40; top-down three-quarter view; same scale/anchor; green skin, dark teal/brown clothes, orange accent; no shadows/text/logos/watermark/scenery.
