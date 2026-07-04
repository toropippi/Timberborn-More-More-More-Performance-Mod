# -*- coding: utf-8 -*-
# Gallery graphs for T3MP (draft). Dark theme, big fonts, minimal ink.
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

BG = "#101418"
FG = "#e8e4d8"
ACCENT = "#ffd24d"      # gold
VANILLA = "#8a8f98"     # grey
MODC = "#4dd06a"        # green

plt.rcParams.update({
    "figure.facecolor": BG, "axes.facecolor": BG,
    "axes.edgecolor": FG, "axes.labelcolor": FG,
    "xtick.color": FG, "ytick.color": FG, "text.color": FG,
    "font.family": "Segoe UI", "font.size": 20,
})

# ---------------------------------------------------------------- graph 1
# The hidden population speed throttle: pressed vs actual.
fig, ax = plt.subplots(figsize=(16, 9), dpi=120)
pressed = np.linspace(1, 8, 200)
scale = 0.4  # 200+ beavers
vanilla = 1 + (pressed - 1) * scale
ax.plot(pressed, pressed, color=MODC, lw=6, label="T3MP: speed you press = speed you get")
ax.plot(pressed, vanilla, color=VANILLA, lw=6, ls="--",
        label="Vanilla, 200+ beavers (hidden throttle)")

for p in (3, 7):
    v = 1 + (p - 1) * scale
    ax.plot([p, p], [v, p], color=ACCENT, lw=2, ls=":")
    ax.scatter([p, p], [v, p], s=[160, 160], c=[VANILLA, MODC], zorder=5)
    ax.annotate(f"x{p} → x{v:.1f}", (p, v), textcoords="offset points",
                xytext=(14, -26), fontsize=22, color=VANILLA)

ax.set_xlabel("speed you press", fontsize=24)
ax.set_ylabel("speed you actually get", fontsize=24)
ax.set_title("Timberborn quietly throttles your speed as the colony grows.\nT3MP removes it.",
             fontsize=30, fontweight="bold", pad=24)
ax.legend(fontsize=21, loc="upper left", frameon=False)
ax.set_xlim(1, 8); ax.set_ylim(1, 8)
ax.set_xticks([1, 2, 3, 4, 5, 6, 7, 8])
ax.set_yticks([1, 2, 3, 4, 5, 6, 7, 8])
ax.grid(alpha=0.15)
for s in ("top", "right"):
    ax.spines[s].set_visible(False)
fig.tight_layout()
fig.savefig("graph_throttle.png")
print("graph_throttle.png")

# ---------------------------------------------------------------- graph 2
# Measured per-in-game-day tick rates.
fig, ax = plt.subplots(figsize=(16, 9), dpi=120)
labels = ["Vanilla", "T3MP\n(always-on, rendered)", "T3MP + Shift+P\n(animation skip)"]
vals = [19.6, 29.7, 47.2]
mults = ["1.0x", "1.5x", "2.4x"]
colors = [VANILLA, MODC, ACCENT]
bars = ax.bar(labels, vals, color=colors, width=0.62)
for bar, v, m in zip(bars, vals, mults):
    ax.text(bar.get_x() + bar.get_width() / 2, v + 1.0, f"{v:.1f} ticks/s",
            ha="center", fontsize=24, fontweight="bold")
    ax.text(bar.get_x() + bar.get_width() / 2, v / 2, m,
            ha="center", fontsize=34, fontweight="bold", color=BG)
ax.set_ylabel("simulation ticks per second", fontsize=24)
ax.set_title("Measured per full in-game day - same colony, same days, large late-game save",
             fontsize=26, fontweight="bold", pad=24)
ax.set_ylim(0, 54)
ax.grid(axis="y", alpha=0.15)
for s in ("top", "right"):
    ax.spines[s].set_visible(False)
ax.tick_params(axis="x", labelsize=22)
fig.tight_layout()
fig.savefig("graph_measured.png")
print("graph_measured.png")
