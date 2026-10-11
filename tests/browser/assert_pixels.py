"""Reject a blank surface even when the transport receives real RDP packets.
The isolated XRDP fixture owns these solid terminal/root colors. No application
code paints them; only the decoded remote desktop can satisfy both thresholds.
Run with system Python (the fixture installs python3-pil).
"""
import json
from pathlib import Path
import sys
from PIL import Image

image = Image.open(sys.argv[1]).convert('RGB')
counts = image.getcolors(image.width * image.height)
root = sum(n for n, rgb in counts if all(abs(a-b) <= 2 for a,b in zip(rgb, (20,40,60))))
terminal = sum(n for n, rgb in counts if all(abs(a-b) <= 2 for a,b in zip(rgb, (23,57,86))))
report = {'root_pixels':root,'terminal_pixels':terminal,'screenshot_width':image.width,'screenshot_height':image.height}
Path(sys.argv[1]).with_suffix('.pixels.json').write_text(json.dumps(report,indent=2))
if root < 30_000 or terminal < 30_000:
    raise AssertionError(f'Remote desktop did not visibly render in Skia: {report}')
print('PASS rendered remote desktop pixels', json.dumps(report))
