# Artwork sources

Historical promotional artwork and its generators. The old speed claims and
captured meter readings are not evidence for v1.2.1; review them before reuse.
The current package preview is `mod/thumbnail.jpg`.

`make_thumbnail.py` reads `source_screenshot.png` and writes `thumbnail.png`.
The other `make_*.py` files generate the corresponding feature cards and graphs.
They require Pillow and/or Matplotlib; inspect each script before regenerating.
Original screenshots and drawing code are retained so artwork can be revised
without reconstructing the design from generated images.
