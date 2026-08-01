extends Node2D
## Issue #142 scale spike — graybox floor/wall layer, drawn once beneath the
## creature layer. NOT production geometry: it exists only to give the scale
## comparison a "узкий проход" (the single-tile corridor) and a room for the
## "скопление у прохода" cluster to pile up in, per the Issue #142 brief.
##
## Row 0 is the northernmost row. '#' = wall, '.' = floor, ' ' = undug/void
## (nothing drawn). The corridor is exactly one tile wide (row 4, cols 10-15);
## the room mouth around cols 5-9 / rows 1-5 is where scale_spike.gd piles up
## the crowd. Tile pitch is CameraView.DefaultTileSize (40px) and does not
## change with the scale multiplier — only creature draw size scales, per the
## geometry rules in the Issue #142 brief.

const TILE_SIZE := 40.0

const GRID := [
	"##########        ",
	"#.........        ",
	"#.........        ",
	"#.........######  ",
	"#...............  ",
	"#.........######  ",
	"#.........        ",
	"#.........        ",
	"#.........        ",
	"##########        ",
]

const FLOOR_COLOR := Color8(0xcb, 0xd5, 0xe1)
const FLOOR_LINE := Color8(0x94, 0xa3, 0xb8)
const WALL_COLOR := Color8(0x33, 0x41, 0x55)
const WALL_TOP := Color8(0x47, 0x55, 0x69)

func _draw() -> void:
	for row in GRID.size():
		var line: String = GRID[row]
		for col in line.length():
			var cell := line[col]
			if cell == " ":
				continue
			var rect := Rect2(Vector2(col, row) * TILE_SIZE, Vector2(TILE_SIZE, TILE_SIZE))
			if cell == ".":
				draw_rect(rect, FLOOR_COLOR)
				draw_rect(rect, FLOOR_LINE, false, 1.0)
			else:
				draw_rect(rect, WALL_COLOR)
				draw_rect(Rect2(rect.position, Vector2(TILE_SIZE, TILE_SIZE * 0.25)), WALL_TOP)
