import numpy as np
import matplotlib.pyplot as plt
from matplotlib.patches import Patch

def force_tip(ki,x,pi,v):
    """
    ki: stiffness of the layer where the needle tip is located
    x: penetration
distance between the insertion point and the current position of the
needle tip
    pi: viscosity factor
    v: linear velocity of the
needle tip
    """
    result=(ki*x)-(pi*v)
    return result

# [name, stiffness (N/m), viscosity (Ns/m2), kinetic friction (N), width (mm)]
layers_ideal = [
    ["Skin",327, 3.21, 0.5,  3],
    ["Subcutaneous tissue",0, 2.98, 0.5,  6],
    ["Supraspinous and interspinous ligaments", 72, 4.06, 0.8, 30],
    ["Yellow ligament",920, 4.21, 1.1,  3],
    ["Epidural space",0, 2.98, 0.5,  6],
    ["Dura mater",920, 4.21, 1.1,  6],
]
layers_model = [
    ["Skin",327, 3.21, 0.5,  2.68],
    ["Subcutaneous tissue",0, 2.98, 0.5,  28.52],
    ["Supraspinous and interspinous ligaments", 72, 4.06, 0.8, 25.27],
    ["Yellow ligament",920, 4.21, 1.1,  17.71],
    ["Epidural space",0, 2.98, 0.5,  4.18],
    ["Dura mater",920, 4.21, 1.1,  7.02],
]
def force_friction(x, layer_data):
    """
    x: needle tip depth from the skin surface (m)
    Sums the kinetic friction of every layer the needle has entered.
    """
    result=0
    start=0
    for name, ki, pi, fi, width in layer_data:
        if x<=start:
            break
        result+=fi
        start+=width/1000
    return result

def force_needle(x, v, layer_data):
    """
    x: needle tip depth from the skin surface (m)
    v: linear velocity of the needle tip (m/s)
    """
    start=0
    for name, ki, pi, fi, width in layer_data:
        if x<start+width/1000:
            break
        start+=width/1000
    return force_tip(ki, x - start, pi, v) + force_friction(x, layer_data)

insertion_time_seconds = 60
colors = plt.get_cmap("tab10").colors

def plot_force_graph(layer_data, title):
    total_depth_mm = sum(layer[4] for layer in layer_data)
    insertion_velocity = (total_depth_mm / 1000) / insertion_time_seconds
    depths_mm = np.linspace(0, total_depth_mm, 1001)
    forces = np.array([
        force_needle(depth_mm / 1000, insertion_velocity, layer_data)
        for depth_mm in depths_mm
    ])

    fig, ax = plt.subplots(figsize=(11, 6))
    layer_start_mm = 0
    layer_legend = []

    for index, (name, ki, pi, fi, width_mm) in enumerate(layer_data):
        layer_end_mm = layer_start_mm + width_mm
        color = colors[index % len(colors)]
        ax.axvspan(layer_start_mm, layer_end_mm, color=color, alpha=0.12)
        layer_legend.append(Patch(facecolor=color, alpha=0.35, label=name))
        if index > 0:
            ax.axvline(layer_start_mm, color="gray", linestyle=":", linewidth=0.8)
        layer_start_mm = layer_end_mm

    ax.plot(depths_mm, forces, color="black", linewidth=2, label="Needle force")
    ax.set_title(title)
    ax.set_xlabel("Depth (mm)")
    ax.set_ylabel("Force (N)")
    ax.grid(True, alpha=0.3)
    ax.legend(handles=layer_legend, title="Layer", bbox_to_anchor=(1.02, 1), loc="upper left")
    fig.tight_layout()


plot_force_graph(layers_ideal, "Ideal Needle Force")
plot_force_graph(layers_model, "Modeled Needle Force")
plt.show()
