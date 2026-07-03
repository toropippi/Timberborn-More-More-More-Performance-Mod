# assets — store thumbnail / marketing images

Version-controlled art for the mod's store pages.

| File | Use | Notes |
|---|---|---|
| `thumbnail.png` | **mod.io logo** and gallery image | 1920×1080 (16:9), ~1.7 MB. mod.io auto-generates 320×180 / 640×360 / 1280×720 from it. |
| `thumbnail_steam.jpg` | **Steam Workshop preview** | Same image as JPG, ~330 KB — under Steam's **1 MB** preview limit. |
| `source_screenshot.png` | Raw in-game capture the thumbnail is built from | Untouched screenshot (shows the live rSPD/iSPD meter bottom-right). |
| `make_thumbnail.py` | Regenerates `thumbnail.png` from the screenshot | Self-contained; run it from this folder. |

## Regenerate

```bash
pip install pillow
python make_thumbnail.py      # reads source_screenshot.png -> writes thumbnail.png
```

Then, if `thumbnail.png` changed, refresh the Steam JPG:

```bash
python -c "from PIL import Image; Image.open('thumbnail.png').convert('RGB').save('thumbnail_steam.jpg','JPEG',quality=92,optimize=True)"
```

The script uses Windows system fonts (Arial Black, Segoe UI). On other OSes, edit
the font paths near the top of `make_thumbnail.py`.

## Design

`PERFORMANCE IMPROVED x1.7` headline (metallic-gold gradient + bloom, silver
`IMPROVED`, drop shadow, vignette) over the game scene, with the **real captured
in-game speed meter** (`rSPD/iSPD` + `UPS`) enlarged 9× in a glowing panel — i.e.
the mod's own live readout is the proof element, not a mock-up.

The headline `x1.7` is the *up-to* figure (≈1.5× always-on, ≈1.75× with the
Shift+P render-skip toggle); see the root `README.md` for the honest breakdown.
