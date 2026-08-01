extends Node2D
##
## Issue #142 scale spike — NOT production.
##
## Shows the same graybox battle frame at three body-scale multipliers
## (100% / 150% / 200% of the game's current GoblinDrawSize —
## src/DungeonFortress.Presentation/CameraView.cs) so the owner can pick a
## size before Issue #77 (hit feedback, procedural animation, status icons)
## commits to tuning against it. This scene chooses nothing on its own: the
## owner reads the three captured frames and decides.
##
## Weapon and armor are graybox layers drawn by creature_visual.gd. The
## simulation has no equipment yet (that is roadmap slice 7), so "is a
## weapon readable at this scale" and "is heavy armor distinct from light"
## can only be answered with a stand-in silhouette, not real gear.
##
## Geometry rules this scene keeps, per
## docs/decisions/0008-three-quarter-projection.md and the Issue #142 brief:
## the logical tile stays 1x1 and fixed at CameraView.DefaultTileSize (40px);
## only the creature's *draw* size scales. Every creature is one canvas (one
## CreatureVisual node) with one pivot at its feet, and CreatureLayer below
## Y-sorts the crowd by that same pivot.
##
## This project is deliberately separate from src/DungeonFortress.Game (see
## project.godot's header): it reuses the existing goblin sprite pack by
## reading the PNGs directly from disk instead of importing them into this
## project's own res:// tree, so no asset is duplicated and no production
## file is touched.

const TILE_SIZE := 40.0
const REFERENCE_TILE_SIZE := 22.0 # CameraView.ReferenceTileSize
const REFERENCE_GOBLIN_DRAW_SIZE := 20.0 # CameraView.ReferenceGoblinDrawSize
const SPRITE_STATES := ["idle", "work", "combat", "downed"]

# Review round 2, finding #1 (was #2 in the review's numbering): this used
# to be 1.5, applied silently to the whole WorldLayer. The legend printed
# "Tile grid: 40 px" / "Body draw size: 36.4 px" while the screen actually
# showed a 60px tile and a 54.5px body — exactly the pixel size the game
# draws at CameraView zoom level "Detail" (1.5x), not at its default zoom
# (1.0). An owner judging the "100%" frame at face value would have picked a
# size one full zoom step larger than what the default camera actually
# renders. Fixed at 1.0 so the frame is WYSIWYG against the legend's own
# numbers — the honest fix the review offered, over disclosing the
# magnification on-frame instead.
const WORLD_MAGNIFICATION := 1.0
const WORLD_OFFSET := Vector2(440, 150)
const LEGEND_WIDTH := 360.0
const FRAME_HEIGHT := 900.0

var textures: Dictionary = {}
var scale_percent: int = 100
var screenshot_path: String = ""
var screenshot_frames_remaining: int = 0
var creature_count: int = 0

func base_draw_size() -> float:
	return REFERENCE_GOBLIN_DRAW_SIZE * (TILE_SIZE / REFERENCE_TILE_SIZE)

func _ready() -> void:
	var args := OS.get_cmdline_user_args()
	scale_percent = _read_int_arg(args, "--scale", 100)
	screenshot_path = _read_arg(args, "--screenshot")
	screenshot_frames_remaining = 3 if screenshot_path != "" else 0

	_load_goblin_textures()

	var world := Node2D.new()
	world.name = "WorldLayer"
	world.position = WORLD_OFFSET
	world.scale = Vector2(WORLD_MAGNIFICATION, WORLD_MAGNIFICATION)
	add_child(world)

	var map := Node2D.new()
	map.name = "MapLayer"
	map.set_script(load("res://map_layer.gd"))
	world.add_child(map)

	var creature_layer := Node2D.new()
	creature_layer.name = "CreatureLayer"
	creature_layer.y_sort_enabled = true
	world.add_child(creature_layer)

	creature_count = _spawn_creatures(creature_layer)
	_build_legend()

func _spawn_creatures(parent: Node2D) -> int:
	var creature_script := load("res://creature_visual.gd")
	var spawned := 0
	for spec in _creature_specs():
		var node := Node2D.new()
		node.set_script(creature_script)
		node.textures = textures
		node.tile_size = TILE_SIZE
		node.scale_multiplier = scale_percent / 100.0
		node.pose = spec.get("pose", "idle")
		node.weapon = spec.get("weapon", "dagger")
		node.armor = spec.get("armor", "light")
		node.badge = spec.get("badge", "")
		var col: float = spec["col"]
		var row: float = spec["row"]
		node.position = Vector2((col + 0.5) * TILE_SIZE, (row + 1.0) * TILE_SIZE)
		parent.add_child(node)
		spawned += 1
	return spawned

## 26 creatures: 6 queued single-file in the one-tile corridor, 20 piled at
## its mouth inside the room. Positions are the same at every scale on
## purpose — only the body draw size in creature_visual.gd changes, so the
## three captures show how crowding at fixed tile spacing looks at each size.
## Seven are badged 1-7; BADGE_LEGEND below spells out what each one shows.
func _creature_specs() -> Array:
	return [
		{"col": 10, "row": 4, "pose": "idle", "weapon": "dagger", "armor": "light", "badge": "1"},
		{"col": 11, "row": 4, "pose": "walk", "weapon": "sword", "armor": "heavy"},
		{"col": 12, "row": 4, "pose": "idle", "weapon": "spear", "armor": "light"},
		{"col": 13, "row": 4, "pose": "walk", "weapon": "dagger", "armor": "heavy"},
		{"col": 14, "row": 4, "pose": "idle", "weapon": "sword", "armor": "light"},
		{"col": 15, "row": 4, "pose": "walk", "weapon": "spear", "armor": "heavy", "badge": "2"},
		{"col": 7, "row": 1, "pose": "windup", "weapon": "sword", "armor": "heavy", "badge": "3"},
		{"col": 8, "row": 1, "pose": "idle", "weapon": "dagger", "armor": "light"},
		{"col": 9, "row": 1, "pose": "hit", "weapon": "spear", "armor": "heavy", "badge": "4"},
		{"col": 6, "row": 2, "pose": "idle", "weapon": "sword", "armor": "light", "badge": "5"},
		{"col": 7, "row": 2, "pose": "downed", "weapon": "dagger", "armor": "heavy", "badge": "6"},
		{"col": 8, "row": 2, "pose": "windup", "weapon": "spear", "armor": "light"},
		{"col": 9, "row": 2, "pose": "idle", "weapon": "sword", "armor": "heavy"},
		{"col": 6, "row": 3, "pose": "hit", "weapon": "dagger", "armor": "light"},
		{"col": 7, "row": 3, "pose": "idle", "weapon": "spear", "armor": "heavy"},
		{"col": 8, "row": 3, "pose": "windup", "weapon": "sword", "armor": "light"},
		{"col": 9, "row": 3, "pose": "downed", "weapon": "dagger", "armor": "heavy"},
		{"col": 5, "row": 4, "pose": "idle", "weapon": "spear", "armor": "light"},
		{"col": 6, "row": 4, "pose": "windup", "weapon": "sword", "armor": "heavy", "badge": "7"},
		{"col": 7, "row": 4, "pose": "idle", "weapon": "dagger", "armor": "light"},
		{"col": 8, "row": 4, "pose": "hit", "weapon": "spear", "armor": "heavy"},
		{"col": 9, "row": 4, "pose": "windup", "weapon": "sword", "armor": "light"},
		{"col": 6, "row": 5, "pose": "idle", "weapon": "dagger", "armor": "heavy"},
		{"col": 7, "row": 5, "pose": "downed", "weapon": "sword", "armor": "light"},
		{"col": 8, "row": 5, "pose": "idle", "weapon": "spear", "armor": "heavy"},
		{"col": 9, "row": 5, "pose": "windup", "weapon": "dagger", "armor": "light"},
	]

const BADGE_LEGEND := [
	"1  light armor + dagger (idle)",
	"2  heavy armor + spear (walk)",
	"3  windup (about to strike)",
	"4  hit (white flash + red edge)",
	"5  light armor (thin outline)",
	"6  downed (weapon dropped)",
	"7  heavy armor (filled block)",
]

func _read_arg(args: PackedStringArray, arg_name: String) -> String:
	var idx := args.find(arg_name)
	if idx == -1 or idx + 1 >= args.size():
		return ""
	return args[idx + 1]

func _read_int_arg(args: PackedStringArray, arg_name: String, default_value: int) -> int:
	var raw := _read_arg(args, arg_name)
	if raw == "":
		return default_value
	return int(raw)

## Reuses the existing goblin pack from src/DungeonFortress.Game instead of
## importing a copy into this project. This project's res:// root is exactly
## two directories below the repository root
## (spikes/142-scale-spike/ -> spikes/ -> repo root), so the path is derived
## rather than hard-coded to one machine.
func _load_goblin_textures() -> void:
	var project_dir := ProjectSettings.globalize_path("res://")
	if project_dir.ends_with("/"):
		project_dir = project_dir.substr(0, project_dir.length() - 1)
	var repo_root := project_dir.get_base_dir().get_base_dir()
	var goblins_dir := repo_root + "/src/DungeonFortress.Game/assets/generated/goblins"
	for state in SPRITE_STATES:
		var path := "%s/goblin_%s_v1.png" % [goblins_dir, state]
		var image := Image.new()
		var err := image.load(path)
		if err != OK:
			printerr("ERROR: scale spike could not load reused goblin sprite '%s' (error %d)." % [path, err])
			get_tree().quit(1)
			return
		textures[state] = ImageTexture.create_from_image(image)

func _build_legend() -> void:
	var panel := ColorRect.new()
	panel.color = Color8(0x0f, 0x17, 0x2a, 235)
	panel.position = Vector2.ZERO
	panel.size = Vector2(LEGEND_WIDTH, FRAME_HEIGHT)
	add_child(panel)

	var draw_size := base_draw_size() * (scale_percent / 100.0)
	var text := "ISSUE #142 — SCALE SPIKE (not production)\n"
	text += "Scale: %d%%\n" % scale_percent
	text += "Tile grid: %.0f px (fixed at every scale)\n" % TILE_SIZE
	text += "Body draw size: %.1f px (100%% = %.1f px)\n\n" % [draw_size, base_draw_size()]
	text += "Layers per creature, one canvas + one pivot at the feet:\n"
	text += "  body sprite -> armor overlay -> weapon -> hit/downed FX\n\n"
	text += "Armor:\n  light = thin tan outline\n  heavy = filled grey block + pauldrons\n\n"
	text += "Weapon (colour = kind):\n  grey short = dagger\n  silver + guard = sword\n  brown long = spear\n\n"
	text += "Pose reuses the current sprite pack (no new art):\n"
	text += "  idle -> goblin_idle_v1\n  walk -> goblin_work_v1\n"
	text += "  windup/hit -> goblin_combat_v1\n  downed -> goblin_downed_v1\n\n"
	text += "Narrow passage: single-file queue (right).\n"
	text += "Cluster: piled at its mouth (centre-left).\n\n"
	text += "Numbered badges:\n"
	for entry in BADGE_LEGEND:
		text += "  %s\n" % entry

	var label := Label.new()
	label.text = text
	label.position = Vector2(14, 14)
	label.size = Vector2(LEGEND_WIDTH - 28, FRAME_HEIGHT - 28)
	label.add_theme_color_override("font_color", Color8(0xf1, 0xf5, 0xf9))
	label.add_theme_font_size_override("font_size", 15)
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	add_child(label)

func _process(_delta: float) -> void:
	if screenshot_path == "":
		return
	if screenshot_frames_remaining > 0:
		screenshot_frames_remaining -= 1
		return

	var path_to_report := screenshot_path
	screenshot_path = ""

	DirAccess.make_dir_recursive_absolute(path_to_report.get_base_dir())
	var image := get_viewport().get_texture().get_image()
	var err := image.save_png(path_to_report)
	if err != OK:
		printerr("ERROR: scale spike screenshot save failed (%d) for '%s'." % [err, path_to_report])
		get_tree().quit(1)
		return

	print(JSON.stringify({
		"event": "scale_spike_capture",
		"status": "ok",
		"scalePercent": scale_percent,
		"tileSizePx": TILE_SIZE,
		"baseDrawSizePx": base_draw_size(),
		"drawSizePx": base_draw_size() * (scale_percent / 100.0),
		"creatureCount": creature_count,
		"path": path_to_report,
	}))
	get_tree().quit()
