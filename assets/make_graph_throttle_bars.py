# -*- coding: utf-8 -*-
# Gallery graph: the hidden population throttle, in PLAYER language only.
# One story: the fastest speed button (3rd button) on a 200+ beaver colony.
# Two bars: vanilla actual vs T3MP. No internal-multiplier jargon.
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

BG = "#101418"
FG = "#e8e4d8"
MUTED = "#9aa0a8"
VANILLA = "#8a8f98"     # de-emphasis gray (context)
MODC = "#4dd06a"        # green (the subject)

plt.rcParams.update({
    "figure.facecolor": BG, "axes.facecolor": BG,
    "axes.edgecolor": "#3a4048", "axes.labelcolor": FG,
    "xtick.color": FG, "ytick.color": FG, "text.color": FG,
    "font.family": "Segoe UI", "font.size": 20,
})

fig, ax = plt.subplots(figsize=(16, 9), dpi=120)

labels = ["Vanilla\n200+ beavers", "T3MP"]
vals = [3.4, 7.0]
colors = [VANILLA, MODC]

y = np.arange(2)
bars = ax.barh(y, vals, height=0.42, color=colors, zorder=3)
ax.invert_yaxis()   # vanilla on top, T3MP below

# direct labels: the story is these two numbers
ax.text(3.4 + 0.15, 0, "x3.4", va="center", fontsize=44, fontweight="bold", color=VANILLA)
ax.text(3.4 + 1.35, 0, "you lose over half", va="center", fontsize=24, color=MUTED)
ax.text(7.0 - 0.15, 1, "x7.0  full speed", va="center", ha="right",
        fontsize=44, fontweight="bold", color="#0c1210")

ax.set_yticks(y)
ax.set_yticklabels(labels, fontsize=28, fontweight="bold")

ax.set_xlim(0, 8.0)
ax.set_xticks([0, 1, 2, 3, 4, 5, 6, 7])
ax.set_xticklabels(["0", "x1", "x2", "x3", "x4", "x5", "x6", "x7"])
ax.set_xlabel("how fast the game actually runs  (x1 = normal speed)", fontsize=24)
ax.grid(axis="x", alpha=0.15, zorder=0)
for s in ("top", "right", "left"):
    ax.spines[s].set_visible(False)
ax.tick_params(axis="y", length=0)

ax.set_title('The fastest speed button promises 7x normal speed.\n'
             'On a big colony, vanilla secretly delivers only x3.4 - T3MP fixes that.',
             fontsize=28, fontweight="bold", pad=26, loc="left")

fig.text(0.99, 0.015,
         "measured in-game: 3rd speed button, colony with 200+ beavers",
         ha="right", fontsize=15, color=MUTED)

fig.tight_layout(rect=(0, 0.03, 1, 1))
fig.savefig("graph_throttle_bars.png")
print("graph_throttle_bars.png")
