"""
Needle force against depth: the user's model (IdealCurve) and the one Unity sends to the stylus.

Notation: x = depth of the needle tip under the skin, v = its speed (+ going in). Layers i = 1..n, each with stiffness k_i (N/m),
viscosity rho_i (N s/m), kinetic friction mu_i (N) and width w_i; x_i = w_1 + ... + w_(i-1) is where layer i starts, and i is the layer
that contains x. Forces are + against the advance.

IdealCurve (force_needle):
    F(x, v)    = F_tip + F_friction
    F_tip      = k_i (x - x_i) - rho_i v
    F_friction = sum of mu_j over every layer j the tip has entered (x > x_j)

Unity (force_needle_unity; Assets/Scripts/NeedleHaptics.cs):
    F(x, v) = s_i [ k_i (x - x_i) + rho_i v + sigma(v) sum_{j<=i} mu_j ]
    sigma(v) = clamp(v / v_f, -1, 1),  v_f = 3 mm/s        the friction fades out below v_f, the hand's tremor does not flip it
    P_i      = k_i w_i + sum_{j<=i} mu_j                   the steady peak of layer i
    s_i      = min(1, F_max / P_i),  F_max = 3 N           a layer whose peak is above 3 N is scaled to 3 N, keeping its shape
    F_sent   = clamp(F, -F_max, F_max)                     what HapticStylus lets through
    The ideal curve (force_ideal_unity, the white line of the force graph in the headset): v = (w_1 + ... + w_n) / 60 s and sigma = 1.

Both plots show the same equations under the axes (FORMULAS).
"""
import sys
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

# The force Unity sends to the stylus: Assets/Scripts/NeedleHaptics.cs (Force, Ideal), table filled by Assets/Editor/NeedleHapticsBuilder.cs.
# Same tissues and constants as above; what Unity changes:
#   - every term opposes the motion:   k*dx + rho*v + friction*slip   (here + = against the advance; IdealCurve's force_tip subtracts rho*v, ~0.01 N)
#   - the friction fades out below 3 mm/s (slip = v / 3 mm/s, capped at 1) so the hand's tremor does not flip it; at or above it, it is whole
#   - a tissue whose steady peak (k * its width + the friction of every tissue so far) is above 3 N is scaled down to 3 N, keeping its shape
#   - the stylus never gets more than HapticStylus.maxForce (3 N in the scene); TableScale = maxForce / the table's peak = 1 while both are 3 N
# layer_data is a table like layers_model, where "Supraspinous and interspinous ligaments" is one tissue (Unity's two layers with "continues").
UNITY_CEILING_N = 3.0         # NeedleHapticsBuilder.Ceiling
UNITY_FRICTION_SPEED = 0.003  # NeedleHaptics.frictionSpeed, m/s
UNITY_MAX_FORCE_N = 3.0       # HapticStylus.maxForce

def force_needle_unity(x, v, layer_data, slip=None, ceiling=UNITY_CEILING_N, max_force=UNITY_MAX_FORCE_N):
    """
    x: needle tip depth from the skin surface (m)
    v: linear velocity of the needle tip (m/s), + = going in
    slip: how much of the kinetic friction is on (-1..1); None = v / UNITY_FRICTION_SPEED like Unity does with the hand's speed
    Returns the force along the needle (N), + = against the advance.
    """
    if x < 0:
        return 0.0
    start = friction = 0.0
    for n, (name, ki, pi, fi, width) in enumerate(layer_data, 1):
        width /= 1000
        friction += fi
        if x < start + width or n == len(layer_data):   # the last tissue goes on until the target
            break
        start += width
    if slip is None:
        slip = float(np.clip(v / UNITY_FRICTION_SPEED, -1, 1))
    force = ki * (x - start) + pi * v + friction * slip   # dx is not capped
    peak = ki * width + friction
    if peak > ceiling:
        force *= ceiling / peak
    return float(np.clip(force, -max_force, max_force))

def force_ideal_unity(x, layer_data):
    """NeedleHaptics.Ideal: the Unity formula at an even advance (the whole depth in insertion_time_seconds), friction whole.
    It is the white line of the force graph in the headset."""
    total = sum(layer[4] for layer in layer_data) / 1000
    return force_needle_unity(x, total / insertion_time_seconds, layer_data, slip=1)

# The equations under the plots (matplotlib mathtext), one line each; the docstring at the top has them in plain text.
FORMULAS = (
    r"IdealCurve (black):   $F(x,v)=k_i\,(x-x_i)-\rho_i\,v+\sum_{x>x_j}\mu_j$" "\n"
    r"Unity (red, dashed):   $F(x,v)=s_i\,[\,k_i\,(x-x_i)+\rho_i\,v+\sigma(v)\sum_{j\leq i}\mu_j\,]$,   "
    r"$\sigma(v)=\mathrm{clamp}(v/v_f,\,-1,\,1)$,   "
    r"$s_i=\min\left(1,\,\frac{F_{max}}{k_i\,w_i+\sum_{j\leq i}\mu_j}\right)$" "\n"
    r"$i$: layer that contains $x$;  $x_i$: where it starts;  $w_i$: its width;  $F_{max}=3$ N;  $v_f=3$ mm/s;  "
    r"on this plot $v=\sum w\,/\,60$ s and $\sigma=1$;  + = against the advance"
)

def plot_force_graph(layer_data, title):
    total_depth_mm = sum(layer[4] for layer in layer_data)
    insertion_velocity = (total_depth_mm / 1000) / insertion_time_seconds
    depths_mm = np.linspace(0, total_depth_mm, 1001)
    forces = np.array([
        force_needle(depth_mm / 1000, insertion_velocity, layer_data)
        for depth_mm in depths_mm
    ])
    forces_unity = np.array([force_ideal_unity(depth_mm / 1000, layer_data) for depth_mm in depths_mm])

    fig, ax = plt.subplots(figsize=(11, 7.6))
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

    formula, = ax.plot(depths_mm, forces, color="black", linewidth=2, label="IdealCurve formula")
    unity, = ax.plot(depths_mm, forces_unity, color="tab:red", linewidth=1.6, linestyle="--",
                     label="Unity formula (tissues above 3 N scaled to 3 N)")
    ax.set_title(title)
    ax.set_xlabel("Depth (mm)")
    ax.set_ylabel("Force (N)")
    ax.grid(True, alpha=0.3)
    layers_box = ax.legend(handles=layer_legend, title="Layer", bbox_to_anchor=(1.02, 1), loc="upper left")
    ax.add_artist(layers_box)
    ax.legend(handles=[formula, unity], title="Formula", bbox_to_anchor=(1.02, 0), loc="lower left")
    fig.tight_layout(rect=(0, 0.22, 1, 1))
    fig.text(0.01, 0.01, FORMULAS, fontsize=9, ha="left", va="bottom", linespacing=2.7)


def selftest():
    """Unity's numbers (Assets/Editor/NeedleHapticsCheck.cs) against this file: python IdealCurve.py --selftest"""
    for table in (layers_ideal, layers_model):
        total = sum(layer[4] for layer in table) / 1000
        v = total / insertion_time_seconds
        start = 0
        for name, ki, pi, fi, width in table:
            end = start + width / 1000
            x = end - 1e-9
            unscaled = force_needle_unity(x, v, table, slip=1, ceiling=np.inf, max_force=np.inf)
            # without the scaling it is IdealCurve's force, apart from the sign of rho*v (2 * rho * v < 0.03 N)
            assert abs(unscaled - force_needle(x, v, table)) < 0.03, (name, unscaled, force_needle(x, v, table))
            # with it, no tissue ends above 3 N (+ rho*v of the scaled one), and the soft ones are untouched
            ideal = force_ideal_unity(x, table)
            assert ideal <= UNITY_MAX_FORCE_N + 1e-9, (name, ideal)
            if unscaled > UNITY_CEILING_N:
                assert abs(ideal - UNITY_CEILING_N) < 0.02, (name, ideal)
            else:
                assert abs(ideal - unscaled) < 1e-9, (name, ideal)
            start = end
    ends = [force_ideal_unity(sum(l[4] for l in layers_model[:i + 1]) / 1000 - 1e-9, layers_model) for i in range(6)]
    assert np.allclose(ends, [1.38, 1.00, 3, 3, 3, 3], atol=0.02), ends     # what the headset's graph ends each tissue with
    assert force_needle_unity(-0.001, 0.01, layers_model) == 0                # nothing before the skin
    epidural = sum(l[4] for l in layers_model[:4]) / 1000 + 0.001
    assert force_needle_unity(epidural, 0, layers_model) == 0                 # no spring, no speed: the friction is off
    assert abs(force_needle_unity(epidural, UNITY_FRICTION_SPEED, layers_model) - 3.0) < 0.05   # friction whole: the epidural is flat at its scaled 3 N
    print("selftest ok")


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        selftest()
    else:
        plot_force_graph(layers_ideal, "Ideal Needle Force")
        plot_force_graph(layers_model, "Modeled Needle Force")
        plt.show()
