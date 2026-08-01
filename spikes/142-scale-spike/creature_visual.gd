extends Node2D
## Issue #142 scale spike — one creature. One canvas (this node), one pivot:
## `position` is the creature's feet, set by scale_spike.gd from its tile.
## The body sprite is drawn growing *upward* from the local origin so the
## pivot never moves when scale_multiplier changes the draw size.
##
## Checkpoint 1: body sprite only. Weapon and armor graybox layers are added
## in checkpoint 2 (see docs reference in scale_spike.gd's header comment).

var textures: Dictionary = {}
var tile_size: float = 40.0
var scale_multiplier: float = 1.0
var pose: String = "idle" # idle | walk | windup | hit | downed
var weapon: String = "dagger" # dagger | sword | spear
var armor: String = "light" # light | heavy
var badge: String = "" # short number, e.g. "1"; explained in the legend list

const REFERENCE_TILE_SIZE := 22.0 # CameraView.ReferenceTileSize
const REFERENCE_GOBLIN_DRAW_SIZE := 20.0 # CameraView.ReferenceGoblinDrawSize

const POSE_SPRITE := {
	"idle": "idle",
	"walk": "work",
	"windup": "combat",
	"hit": "combat",
	"downed": "downed",
}

func _draw_size() -> float:
	return REFERENCE_GOBLIN_DRAW_SIZE * (tile_size / REFERENCE_TILE_SIZE) * scale_multiplier

func _draw() -> void:
	var draw_size := _draw_size()
	var half := draw_size * 0.5
	var body_rect := Rect2(Vector2(-half, -draw_size), Vector2(draw_size, draw_size))

	var sprite_key: String = POSE_SPRITE.get(pose, "idle")
	var tex: Texture2D = textures.get(sprite_key)
	if tex:
		draw_texture_rect(tex, body_rect, false)
	else:
		draw_circle(Vector2(0, -half), draw_size * 0.3, Color8(0x84, 0xcc, 0x16))

	# A badge, not a floating caption: at 40px tile spacing several highlighted
	# creatures sit only one or two tiles apart, and a caption's own text width
	# reliably overlapped its neighbour's (seen on the first capture). A small
	# fixed-size numbered badge cannot collide the same way; the legend panel
	# spells out what each number means.
	if badge != "":
		var font := ThemeDB.fallback_font
		var badge_radius: float = max(7.0, draw_size * 0.16)
		var badge_center := Vector2(half * 1.2, -draw_size * 0.78)
		draw_circle(badge_center, badge_radius, Color8(0xfa, 0xcc, 0x15))
		draw_circle(badge_center, badge_radius, Color8(0x0f, 0x17, 0x2a), false, max(1.0, badge_radius * 0.12))
		var fsize: int = int(badge_radius * 1.2)
		var text_size := font.get_string_size(badge, HORIZONTAL_ALIGNMENT_CENTER, -1, fsize)
		var text_pos := badge_center - (text_size * 0.5) + Vector2(0, text_size.y * 0.35)
		draw_string(font, text_pos, badge, HORIZONTAL_ALIGNMENT_LEFT, -1, fsize, Color8(0x0f, 0x17, 0x2a))
