"""Generate a 256x256 plugin icon: Jellyfin teal circle with a white Alexa-style waveform."""
from PIL import Image, ImageDraw

size = 256
img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# Background circle - Jellyfin teal (#00A4DC)
margin = 16
draw.ellipse([margin, margin, size - margin, size - margin], fill=(0, 164, 220, 255))

# Sound wave bars centered in the circle
cx, cy = size // 2, size // 2
bar_w = 12
gap = 6
bar_count = 5
total_w = bar_count * bar_w + (bar_count - 1) * gap
start_x = cx - total_w // 2

heights = [40, 64, 80, 64, 40]  # symmetric wave pattern
for i, h in enumerate(heights):
    x = start_x + i * (bar_w + gap)
    y_top = cy - h // 2
    y_bot = cy + h // 2
    draw.rounded_rectangle([x, y_top, x + bar_w, y_bot], radius=4, fill=(255, 255, 255, 230))

# Small dot above bars (voice/signal indicator)
dot_r = 6
draw.ellipse([cx - dot_r, cy - 58, cx + dot_r, cy - 58 + dot_r * 2], fill=(255, 255, 255, 200))

# Two small arcs radiating from the dot (wireless signal)
arc_bbox = [cx - 18, cy - 76, cx + 18, cy - 40]
draw.arc(arc_bbox, 220, 320, fill=(255, 255, 255, 160), width=3)
arc_bbox2 = [cx - 28, cy - 86, cx + 28, cy - 50]
draw.arc(arc_bbox2, 220, 320, fill=(255, 255, 255, 120), width=3)

img.save("icon.png")
print(f"Created icon.png ({img.size[0]}x{img.size[1]})")
