extends Node2D
## Issue #142 scale spike — one creature. One canvas (this node), one pivot:
## `position` is the creature's feet, set by scale_spike.gd from its tile.
## The body sprite is drawn growing *upward* from the local origin so the
## pivot never moves when scale_multiplier changes the draw size.
##
## Draw order (checkpoint 2 adds armor + weapon + pose FX on top of the body
## sprite, all in this one canvas so they never drift off the shared pivot):
##   body sprite -> armor overlay -> weapon -> hit/downed FX -> badge
##
## The simulation has no equipment yet (roadmap slice 7), so armor and
## weapon are graybox primitives, not new art: a silhouette stand-in the
## owner can judge readability against, not a preview of final gear.

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

const WEAPON_COLOR := {
	"dagger": Color8(0x9c, 0xa3, 0xaf),
	"sword": Color8(0xcb, 0xd5, 0xe1),
	"spear": Color8(0xb4, 0x83, 0x54),
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

	_draw_armor(draw_size, half)
	_draw_weapon(draw_size, half)

	if pose == "hit":
		draw_rect(body_rect, Color(1, 1, 1, 0.45))
		draw_rect(body_rect, Color8(0xdc, 0x26, 0x26), false, max(1.0, draw_size * 0.035))

	if pose == "downed":
		draw_rect(body_rect, Color(0.35, 0.35, 0.35, 0.32))
		var lw: float = max(1.0, draw_size * 0.05)
		draw_line(Vector2(-half * 0.5, -draw_size * 0.62), Vector2(half * 0.5, -draw_size * 0.18), Color8(0x7f, 0x1d, 0x1d), lw)
		draw_line(Vector2(half * 0.5, -draw_size * 0.62), Vector2(-half * 0.5, -draw_size * 0.18), Color8(0x7f, 0x1d, 0x1d), lw)

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

## Torso overlay. Deliberately a silhouette, not a texture: heavy armor must
## read as bulkier than light armor at every one of the three scales, and a
## filled block vs. a thin outline stays legible even shrunk to 100%'s ~36px
## body, which a second sprite layered at that size might not.
func _draw_armor(draw_size: float, half: float) -> void:
	# Fractions measured against goblin_idle_v1.png: the head fills roughly
	# the top 40%, shoulders sit around 45-50%, so the chest band has to stay
	# below that or it paints over the face — checkpoint 2's first capture
	# did exactly that with a 0.72 top fraction and had to be corrected.
	var top := -draw_size * 0.55
	var bottom := -draw_size * 0.27
	var torso := Rect2(Vector2(-half * 0.5, top), Vector2(half * 1.0, bottom - top))
	if armor == "heavy":
		var grown := torso.grow(draw_size * 0.03)
		draw_rect(grown, Color8(0x47, 0x55, 0x69))
		draw_rect(grown, Color8(0x1e, 0x29, 0x3b), false, max(1.0, draw_size * 0.03))
		var shoulder_r: float = draw_size * 0.075
		draw_circle(Vector2(grown.position.x, grown.position.y + shoulder_r * 0.3), shoulder_r, Color8(0x33, 0x41, 0x55))
		draw_circle(Vector2(grown.position.x + grown.size.x, grown.position.y + shoulder_r * 0.3), shoulder_r, Color8(0x33, 0x41, 0x55))
		draw_line(
			Vector2(grown.position.x, grown.position.y + grown.size.y * 0.55),
			Vector2(grown.position.x + grown.size.x, grown.position.y + grown.size.y * 0.55),
			Color8(0x94, 0xa3, 0xb8), max(1.0, draw_size * 0.02))
	else:
		draw_rect(torso, Color8(0xa1, 0x62, 0x07), false, max(1.0, draw_size * 0.045))

## A colour-coded line/polygon stand-in for the weapon: dagger is short and
## thin, sword is medium with a crossguard, spear is long with a small head.
## Windup raises it, downed lays it beside the body ("weapon dropped" —
## badge 6), everything else rests it at the hip.
func _draw_weapon(draw_size: float, half: float) -> void:
	var color: Color = WEAPON_COLOR.get(weapon, Color.WHITE)
	var hand := Vector2(half * 0.5, -draw_size * 0.6)
	var length := draw_size * 0.4
	var width: float = max(1.0, draw_size * 0.03)
	if weapon == "sword":
		length = draw_size * 0.62
		width = max(1.0, draw_size * 0.05)
	elif weapon == "spear":
		length = draw_size * 0.92
		width = max(1.0, draw_size * 0.035)

	var angle_deg := -55.0
	if pose == "windup":
		angle_deg = -115.0
	elif pose == "downed":
		angle_deg = 8.0
		hand = Vector2(-half * 0.7, -draw_size * 0.12)

	var angle := deg_to_rad(angle_deg)
	var direction := Vector2(cos(angle), sin(angle))
	var tip := hand + direction * length
	draw_line(hand, tip, color, width)

	if weapon == "sword":
		var perp := Vector2(-direction.y, direction.x) * (draw_size * 0.12)
		var guard_center := hand.lerp(tip, 0.18)
		draw_line(guard_center - perp, guard_center + perp, color.darkened(0.25), width)
	elif weapon == "spear":
		var head_perp := Vector2(-direction.y, direction.x) * (draw_size * 0.05)
		var points := PackedVector2Array([
			tip,
			tip - direction * draw_size * 0.12 + head_perp,
			tip - direction * draw_size * 0.12 - head_perp,
		])
		draw_colored_polygon(points, Color8(0x94, 0xa3, 0xb8))
