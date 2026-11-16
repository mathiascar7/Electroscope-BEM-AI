# Electroscope

<p align="center">
  <img src="Title.png" alt="Electroscope simulation interface with triboelectric controls, curve editing, and BEM panel views" width="100%">
</p>

<p align="center">
  <strong>A Unity-based electroscope simulation and AI-for-Science testbed coupling material-driven charging models, parametric conductor geometry, panel-discretized electrostatics, and an agent-controlled experiment loop.</strong>
</p>

Electroscope is a Unity-based computational-physics research prototype that
connects material-driven charging, parametric conductor geometry, and a 3D
Boundary Element Method (BEM) solver.

**Can a frontier LLM such as Gemini autonomously recover classical
electrostatic relationships—including \(V=Q/C\), bilateral symmetry, and distal
tip charge crowding—and synthesize source-attributed material profiles from
literature inside a strict double-blind loop?** Electroscope tests this question
by connecting a token-blinded agent to the BEM engine. The recorded run reports
zero matches in its configured lexical leakage audit and agent-initiated
profiles for Quartz (`MAT_21`) and Borosilicate (`MAT_22`).

## Project at a glance

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>⚡ Model a charging scenario</h3>
      Select two cached material profiles, specify force, speed, and duration, and
      calculate modeled charge, potential, field, friction, and work.
    </td>
    <td width="50%" valign="top">
      <h3>〰️ Change the conductor geometry</h3>
      Edit a cubic Bézier path, estimate its arc length, and include a wire
      capacitance term in the charge-division model.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>📐 Inspect a 3D panel solve</h3>
      Resolve a default 16 × 4 panel mesh on each leaf, display panel weights,
      compare solver settings, and test panel-range cancellation.
    </td>
    <td width="50%" valign="top">
      <h3>🤖 Run an agent-mediated experiment</h3>
      Send newline-delimited JSON over a loopback TCP socket, map selected actions
      to blind tokens in Python, and save the request/response trace.
    </td>
  </tr>
</table>

### Why these layers are useful together

1. **Computational electrostatics:** the runtime model links leaf geometry,
   charge, capacitance, and pairwise panel forces. This exposes spatial
   discretization and panel-level diagnostics that a single point-charge
   visualization would not show.
2. **AI-for-Science evaluation:** the notebook maps selected physical actions and
   material names to tokens, records blind prompts and unmasked evaluator data,
   and scans prompts for configured forbidden terms.

## Architecture

```mermaid
flowchart LR
    Notebook["Python notebook<br/>Pygame UI + experiment client"] -- "JSON lines<br/>127.0.0.1:8052" --> Socket["BEMSocketServer"]
    Socket --> Framework["AIChargingFramework"]
    Framework --> Builder["ProceduralElectroscopeBuilder"]
    Framework --> Curve["RuntimeBezierEditor<br/>curve + leaf simulation"]
    Framework --> Tribo["TriboelectricChargerUI"]
    Curve --> Panels["Two-leaf panel solver<br/>BEM diagnostics"]
    Tribo --> Cache["Material-property JSON<br/>(runtime input)"]
    Research["ResearchMaterials / ResearchMaterialsCaller"] --> MaterialAPI["Material research service"]
    Tuning["Tuning scripts"] --> Results["CSV / JSON / TXT artifacts"]
```

The local TCP endpoint is configured to bind to `127.0.0.1:8052` by default.

## Mathematical and physical formulation

The equations below summarize the implemented computational models.

### Scale 1 — triboelectric contact model

The runtime charging UI averages the two configured kinetic-friction
coefficients. The resulting friction force and modeled mechanical work are

$$
\bar{\mu}_k = \frac{\mu_{k,A}+\mu_{k,B}}{2},
\qquad
F_f = \bar{\mu}_k F_N,
\qquad
W = F_f v t.
$$

Surface smoothness modulates a nominal contact area; a fixed multiplier
approximates swept transfer area:

$$
A_{\mathrm{eff}} = 10^{-4}
\max\!\left(0.05,\,s_A s_B\right),
\qquad
A_{\mathrm{swept}} = 25 A_{\mathrm{eff}}.
$$

For the runtime UI, the implemented saturation and air-field cap are

$$
g = 1-\exp\!\left[-0.015\max(0,F_Nvt)\right],
\qquad
\sigma_{\mathrm{raw}} = (\sigma_A-\sigma_B)g,
$$

$$
\sigma_{\max}=\epsilon_0 E_{\mathrm{air}},
\qquad
\sigma = \operatorname{clamp}
  \left(\sigma_{\mathrm{raw}},-\sigma_{\max},+\sigma_{\max}\right),
\qquad
Q_{\mathrm{gen}} = \sigma A_{\mathrm{swept}}.
$$

Here the code sets \(E_{\mathrm{air}}=3.0\times10^6\ \mathrm{V/m}\), then
estimates

$$
C_{\mathrm{rod}} =
10^{-11}\left(1+0.1\ln\max(\epsilon_r,1)\right)\ \mathrm{F},
\qquad
V_{\mathrm{gen}}=\frac{Q_{\mathrm{gen}}}{C_{\mathrm{rod}}},
\qquad
E_{\mathrm{peak}}=\frac{|\sigma|}{\epsilon_0}.
$$

The separate charging tuner uses a simpler affinity/smoothness expression:

$$
Q_{\mathrm{tune}} =
(\sigma_A-\sigma_B)
\left(10^{-4}s_A s_B\right)F_Nvt.
$$

The UI also exposes two spatial grids and pattern/ablation controls.

### Scale 2 — cubic Bézier wire and capacitance division

The curve editor evaluates a cubic Bézier curve from four control points:

$$
\mathbf{B}(t) =
(1-t)^3\mathbf{P}_0
+3(1-t)^2t\mathbf{P}_1
+3(1-t)t^2\mathbf{P}_2
+t^3\mathbf{P}_3,
\qquad 0\le t\le1.
$$

Arc length is estimated by summing line segments along sampled curve points
(100 samples by default):

$$
L \approx \sum_{i=1}^{n}
\left\|\mathbf{B}(t_i)-\mathbf{B}(t_{i-1})\right\|,
\qquad t_i=\frac{i}{n}.
$$

The runtime uses fixed sampling for this path length. The wire-capacitance
approximation and charge division are

$$
C_{\mathrm{wire}}(L,r_w) =
\frac{2\pi\epsilon_0L}
{\max\!\left(0.1,\,\ln(2L/r_w)-1\right)},
$$

$$
C_{\mathrm{eq}} =
C_{\mathrm{leaf-pair}}(\theta)+C_{\mathrm{wire}},
\qquad
Q_{\mathrm{leaves}} =
Q_{\mathrm{input}}
\frac{C_{\mathrm{leaf-pair}}(\theta)}
{C_{\mathrm{leaf-pair}}(\theta)+C_{\mathrm{wire}}}.
$$

The runtime code uses \(r_w=0.5\ \mathrm{mm}\) by default and enforces a lower
bound on the logarithmic denominator.

### Scale 3 — two-leaf panel BEM and angular response

With the runtime defaults \(16\) panels along the leaf and \(4\) across its
width, the two leaves are represented by

$$
N=2(16)(4)=128
\quad\text{panels},\qquad
\Delta A =
\frac{(0.08)(0.02)}{16\cdot4}
=2.5\times10^{-5}\ \mathrm{m}^2
$$

for the solver’s default \(0.08\ \mathrm{m}\times0.02\ \mathrm{m}\) leaf
dimensions. The solver assembles a dense collocation matrix from source-panel influence
coefficients and solves the unit-potential system:

$$
\mathbf A\mathbf q_{\mathrm{unit}}=\mathbf 1,
\qquad
C_{\mathrm{leaf-pair}}=\sum_{i=1}^{N}q_{\mathrm{unit},i}.
$$

The continuous Green-function kernel motivating the panel interactions is

$$
G(\mathbf r,\mathbf r') =
\frac{1}{4\pi\epsilon_0\|\mathbf r-\mathbf r'\|},
\qquad
\Phi(\mathbf r_i)\sim
\sum_{j=1}^{N}q_j
\int_{S_j}\frac{G(\mathbf r_i,\mathbf r')}{\Delta A_j}\,dS'.
$$

In the runtime implementation, the self-panel contribution is evaluated
analytically, near panels use fixed subdivisions, and more distant panels use an
area-over-distance approximation. The separate tuning engine exposes panel
dimensions and near-panel refinement settings and uses a four-point Gauss rule
for its panel quadrature.

For total system charge \(Q\), the runtime solver derives potential and panel
charge weights as

$$
V=\frac{Q}{C_{\mathrm{leaf-pair}}},
\qquad
Q_i=q_{\mathrm{unit},i}V,
\qquad
w_i=\frac{|Q_i|}{\sum_{k=1}^{N}|Q_k|}.
$$

The direct torque calculation uses panel-center positions and pairwise Coulomb
forces between the left and right leaves:

$$
\boldsymbol{\tau}_{\mathrm{elec}} =
\sum_{i\in L}\sum_{j\in R}
(\mathbf r_i-\mathbf r_{\mathrm{pivot}})
\times
\left[
\frac{Q_iQ_j}{4\pi\epsilon_0}
\frac{\mathbf r_i-\mathbf r_j}
{\|\mathbf r_i-\mathbf r_j\|^3}
\right].
$$

The motion model includes electrostatic torque, gravity, quadratic air drag,
and linear hinge friction:

$$
I\ddot{\theta} =
\tau_{\mathrm{elec}}(\theta)
-\frac{1}{2}mg\ell\sin\theta
-k_{\mathrm{air}}\dot{\theta}|\dot{\theta}|
-c_{\mathrm{hinge}}\dot{\theta}.
$$

The included `FoilRepulsionSlideSimulator` advances angular speed and angle
using 20 fixed substeps per frame.

## Solver verification and recorded benchmarks

### BEM tuning checks

The charging tuner also uses Unity's data-oriented job stack:
`Tuning/vChargingHyperTuneEngine.cs` assembles a Green matrix with the
`[BurstCompile]` `BuildGreenMatrixJob` (`IJobParallelFor`) and runs its
`[BurstCompile]` `BiCGSTABSolverJob` (`IJob`) over `NativeArray<float>` buffers
and `float3` geometry data. It sweeps material pairs and force/speed/duration
settings, appending rows to
`ChargingValidation/Charging_Hypertune_Validation.csv` and saving a winner
configuration to `ChargingValidation/BEM_Winner_Config.json`.

The repulsion tuner runs a 14-check metrological suite. The checked-in
`RepulsionSave/vRepulsionHyperTuneEngine_AutoTune_FinalResult.json` reports
`hardGateFails: 0`, `hardGateChecks: 14`, and `overallScore: 10.0` for the
selected high-precision configuration; all 14 hard gates passed. The
corresponding CSV lists checks for:

- finite solver success, charge balance, left/right leaf symmetry,
  equipotential spread, and linear-system residual;
- capacitance and electrostatic-energy identities;
- capacitance monotonicity, fixed-charge energy trend, opening-torque sign, and
  even/odd angular parity checks; and
- direct Coulomb torque versus a finite-difference virtual-work estimate.

One BEM UI capture also records a maximum linear-system residual of
\(1.110\times10^{-15}\) for its displayed \(128\times128\) solve. The residual
and 14-gate result characterize numerical consistency for the recorded
configuration, with a residual near double-precision machine precision.

### Quantitative simulation and discovery benchmarks

The following tables summarize recorded simulation panels and agent observations.

| Run | Material/configuration | Recorded model output | Evidence |
| --- | --- | --- | --- |
| Leaf-pair response | Gold, \(80\times20\ \mathrm{mm}\), \(1.50\ \mu\mathrm m\), \(46.32\ \mathrm{mg}\), \(Q_{\mathrm{input}}=20.000\ \mathrm{nC}\) | \(\theta=11.63^\circ\) per leaf (\(23.26^\circ\) total aperture); \(C_{\mathrm{BEM}}=5.903\ \mathrm{pF}\); \(C_{\mathrm{wire}}=9.059\ \mathrm{pF}\); \(C_{\mathrm{eq}}=14.96\ \mathrm{pF}\). | `Electroscope BEM solver panel.png` |
| Leaf-pair response | Silver, \(80\times20\ \mathrm{mm}\), \(10.00\ \mu\mathrm m\), \(168.00\ \mathrm{mg}\), \(Q_{\mathrm{input}}=20.000\ \mathrm{nC}\) | \(\theta=5.49^\circ\) per leaf (\(10.99^\circ\) total aperture); \(C_{\mathrm{BEM}}=5.578\ \mathrm{pF}\); \(C_{\mathrm{wire}}=8.573\ \mathrm{pF}\); \(C_{\mathrm{eq}}=14.15\ \mathrm{pF}\). | `Electroscope BEM solver panel 2.png` |
| Panel distribution | Silver screenshot's \(16\times4\) panels per leaf | The capture reports a maximum panel charge weight of \(1.74\%\); the displayed map contrasts corner weights (about \(1.7\%\)) with interior values (about \(0.1\%\)). | `Electroscope BEM solver panel 2.png` |
| Dielectric charging case | Gold vs. Teflon; \(10\ \mathrm N\), \(3\ \mathrm{m/s}\), \(5\ \mathrm s\) | \(Q=56.78\ \mathrm{nC}\); \(V=5{,}285.59\ \mathrm V\); \(E_{\mathrm{peak}}=3.0\times10^6\ \mathrm{V/m}\); \(F_f=1.70\ \mathrm N\); \(W=25.50\ \mathrm J\). | `Triboelectric charging panel.png` |
| Conductive charging case | Gold vs. Silver; \(10\ \mathrm N\), \(3\ \mathrm{m/s}\), \(5\ \mathrm s\) | \(Q=-8.07\ \mathrm{nC}\); \(V=-807.38\ \mathrm V\); \(E_{\mathrm{peak}}=4.0415\times10^5\ \mathrm{V/m}\); \(F_f=3.00\ \mathrm N\); \(W=45.00\ \mathrm J\). | `Triboelectric charging panel2.png` |

The blind-agent transcript reports these examples from its detailed Steps 1–19:

| Perturbation | Transcript observation |
| --- | --- |
| Bilateral panel masking | At \(50\ \mathrm{nC}\), masking panels 1–24 or 65–88 reduced reported capacitance from \(14.5211\) to \(13.7430\ \mathrm{pF}\), a \(-5.36\%\) change in both cases. |
| Distal-panel masking | Masking panels 113–128 was reported as a \(-5.01\%\) capacitance change and \(+0.11\%\) torque change. |
| Charge sweep | The transcript reports \(5.47^\circ\) at \(10\ \mathrm{nC}\) and \(46.12^\circ\) at \(80\ \mathrm{nC}\), with potential telemetry following \(V=Q/C\) for the recorded states. |
| Material profile synthesis | The agent autonomously requested Quartz (`MAT_21`) and Borosilicate (`MAT_22`) profiles; the log records a Quartz/Aluminum Foil scenario at \(Q=53.62\ \mathrm{nC}\) and \(V=5362.32\ \mathrm V\). |

## Blind-agent experiment and command protocol

`AIChargingFramework.cs` dispatches these JSON `command` values through
`BEMSocketServer.cs`:

| Command | Purpose |
| --- | --- |
| `GET_STATUS` | Return basic framework/angle status. |
| `INVESTIGATE_MATERIALS` | Load or investigate material profiles. |
| `BUILD_ELECTROSCOPE` | Build the procedural electroscope from request parameters. |
| `EXECUTE_TRIBO` | Calculate and optionally inject a triboelectric charge. |
| `SET_WIRE_GEOMETRY` | Update curve slope and leaf placement. |
| `INJECT_CHARGE` | Set the injected charge. |
| `SET_LEAF_ABLATION` | Disable a one-indexed panel range. |
| `SET_SPATIAL_PATTERN` | Configure a spatial grid pattern and ablation percentages. |
| `RUN_FULL_PIPELINE` | Start the master pipeline coroutine. |
| `RESEARCH_MATERIAL` | Request material research through `ResearchMaterialsCaller`. |

The Python `BlindProtocolAdapter` maps five operation tokens—`OP_ALPHA` through
`OP_EPSILON`—to geometry, triboelectric, panel-ablation, material-research, and
spatial-pattern actions. It also uses opaque material tokens and observation
fields such as `Y_1`–`Y_6`, `U_drv`, and `S_len`.

```mermaid
flowchart LR
    subgraph Unity["Unity physical domain"]
        Server["BEMSocketServer<br/>127.0.0.1:8052"]
        Physics["Physical labels/actions<br/>Gold · Quartz · INJECT_CHARGE<br/>Readouts: Q · C · V · theta"]
        Server --- Physics
    end
    subgraph Boundary["Zero-Leakage Double-Blind Isolation Boundary"]
        Adapter["BlindProtocolAdapter<br/>physical ↔ blind translation"]
        Audit["audit_zero_leakage()<br/>configured lexical scan"]
    end
    subgraph Agent["Gemini LLM view"]
        Tokens["Opaque tokens<br/>OP_ALPHA..OP_EPSILON<br/>MAT_01..MAT_22 · TOPO_*"]
        Telemetry["Unit-stripped observables<br/>U_drv · Y_1..Y_6 · S_len"]
        Tokens --- Telemetry
    end
    Server -->|physical JSON response| Adapter
    Adapter -->|blinded prompt| Audit
    Audit -->|opaque tokens and observables| Agent
    Agent -->|blind command| Adapter
    Adapter -->|translated TCP JSON| Server
```

<details>
  <summary><strong>Example: blind command → TCP payload → blind telemetry</strong></summary>
  <br>
  <table>
    <tr>
      <th>Gemini blind request</th>
      <th>TCP JSON received by <code>BEMSocketServer</code></th>
      <th>Encoded blind response</th>
    </tr>
    <tr>
      <td><pre><code class="language-json">{
  "op": "OP_GAMMA",
  "node_start": 0,
  "node_count": 0,
  "gain_u": 50.0
}</code></pre></td>
      <td><pre><code class="language-json">{
  "command": "SET_LEAF_ABLATION",
  "ablationStartPanel": 0,
  "ablationCountPanels": 0,
  "injectedCharge_nC": 50.0
}</code></pre><small>Sent to <code>127.0.0.1:8052</code></small></td>
      <td><pre><code class="language-json">{
  "status": "OK",
  "observables": {
    "Y_1": 30.0088,
    "Y_2": 14.5211,
    "Y_3": 0.00014307,
    "Y_4": 3443.26,
    "Y_5_delta_pct": 0.0,
    "Y_6_delta_pct": 0.0,
    "U_drv": 50.0,
    "S_len": 1.1515
  }
}</code></pre></td>
    </tr>
  </table>
  <p><small>Recorded intact-mesh baseline (Step 3). The blind response strips physical labels and unit labels; the numeric observables retain the simulation's scale and are not rescaled.</small></p>
</details>

The notebook's dual-view Pygame evaluator uses a \(1440\times850\) display. It
keeps the LLM's blind token stream separate from the evaluator's unmasked
physical-action and telemetry view for the reported double-blind protocol.

The transcript records matched \(-5.36\%\) capacitance changes for two masked
subdomains, a \(-5.01\%\) change for a distal-panel mask, and Quartz material
research followed by a Quartz/Aluminum Foil scenario.

### Notebook evaluator controls

| Key | Action |
| --- | --- |
| `SPACE` | Pause or resume the worker loop. |
| `B` | Run a single-shot blind evaluation. |
| `E` or `ESC` | Run the terminal blind evaluation, finalize the report, and exit. |

The notebook also waits for a stable angle over repeated status polls before
recording certain steps. The saved report specifies a \(0.05^\circ\) tolerance,
four consecutive stable polls, a \(0.25\ \mathrm{s}\) poll interval, and a
maximum wait of \(7.5\ \mathrm{s}\).

## Material research and provenance

The material cache schema supports 11 base properties: color, density, surface
smoothness, transparency, electrical conductivity, triboelectric affinity, work
function, relative permittivity, water absorption, kinetic friction, and
dielectric strength. Each profile can include units, uncertainty bounds/ranges,
scale-coupled formulas, status fields, and bibliographic/source URLs. This
closes a material-synthesis loop: the blind agent's `OP_DELTA` action invokes
Gemini-backed `RESEARCH_MATERIAL`, and `ResearchMaterials.cs` merges the returned
profile into `ElectroscopePhysicsCache_v2.json` without manual cache editing
during the experiment.

The transcript records Quartz (`MAT_21`) in Step 7 and Borosilicate (`MAT_22`)
in Step 19; Quartz is then used in the subsequent triboelectric scenario.

The Teflon PDF contains a material-profile example and source references.

## Repository map

| Path | Contents |
| --- | --- |
| `Assets/` | Unity scenes, material/cache assets, UI, and C# implementation. `Assets/_Recovery/0 (29).unity` has the simulation and socket components attached to `Main Camera`; configured objects are stored in the scene (there are no separate `.prefab` assets in this snapshot). |
| `Packages/` | Unity manifest and lockfile for URP, Input System, Collections, Burst, Mathematics, and Newtonsoft.Json; the Unity runtime provides the Jobs API. |
| `ProjectSettings/` | Unity 6.3 project/player settings, including Run In Background. |
| `Scripts/` | Top-level mirror of core C# sources for direct browsing on GitHub (socket server, command framework, builder, runtime, and UI). |
| `Tuning/` | Top-level mirror of charging sweeps, repulsion BEM tuning/validation, and production-profile recorder sources. |
| `ChargingValidation/` | Saved BEM report/results, winner configuration, and charging sweep CSV. |
| `RepulsionSave/` | Tuning configuration/result, phase-test CSV, and production profile. |
| `Communications/` | Ten numbered Unity/Python step screenshots. |
| `Important_Highlights/` | Fourteen selected experiment and milestone screenshots. |
| `Python LLM Agent learner.ipynb` | Single-cell Python agent, socket client, report writer, and Pygame evaluator. |
| `Python LLM learner full test.txt` | Recorded blind/evaluator experiment transcript. |
| `Teflon Research Test.pdf` | Teflon property-profile sample with source references. |
| `*.png` | Interface, curve, material, and experiment visuals; all 34 PNG files are shown in the gallery below. |

## Quickstart & Unity Implementation Guide

### Unity & Python Setup Guide

1. **Open in Unity.** Clone the repository and add its root folder in Unity Hub.
   The project targets Unity `6000.3.11f1`; Unity loads the included `Assets/`,
   `Packages/manifest.json`, and `ProjectSettings/`. The package configuration
   provides URP, Collections, Jobs, Burst, Mathematics, Input System, and
   Newtonsoft.Json. Alternatively, copy `Assets/`, `Packages/`, and
   `ProjectSettings/` into a compatible Unity project.
2. **Verify the scene and Main Camera.** Open
   `Assets/_Recovery/0 (29).unity` and select **Main Camera** in the Hierarchy.
   Its Inspector contains `BEMSocketServer`, `AIChargingFramework`,
   `RuntimeBezierEditor`, `TriboelectricChargerUI`, and
   `ResearchMaterialsCaller`. Confirm **Run In Background** is enabled under
   *Edit → Project Settings → Player* (it is enabled in the included settings).
   In this recovered scene, set `BEMSocketServer.serverPort` to `8052` to match
   the notebook; the scene currently serializes `5055`.
3. **Launch the simulation and Python agent.** Press **Play** in Unity. The
   enabled `BEMSocketServer` starts on `127.0.0.1:8052` after the port check
   above. In the terminal used to launch Jupyter, set `GEMINI_API_KEY`, install
   `pygame` into the notebook's Python environment, and run
   `Python LLM Agent learner.ipynb`.

   ```powershell
   $env:GEMINI_API_KEY = "<your Gemini API key>"
   python -m pip install pygame
   jupyter lab
   ```

   Open the notebook in Jupyter Lab and run its experiment cell. Keep the API
   key in the environment; do not commit it to the repository.

## Visual gallery

The gallery preserves the 34 PNG assets included in the workspace. Experimental
labels are retained as captions so readers can trace the saved materials to
their source files.

### Core simulation and multiphysics UI — 10 captures

<table>
  <tr>
    <td align="center"><img src="Electroscope%20BEM%20solver%20panel.png" alt="BEM solver panel and leaf diagnostics" width="460"><br><sub>BEM solver and leaf diagnostics</sub></td>
    <td align="center"><img src="Electroscope%20BEM%20solver%20panel%202.png" alt="Alternate BEM solver panel view" width="460"><br><sub>Alternate solver view</sub></td>
  </tr>
  <tr>
    <td align="center"><img src="Curve%20Editor%20Custom%20point%20%231.png" alt="Curve editor with a selected custom point" width="460"><br><sub>Curve editor — custom point</sub></td>
    <td align="center"><img src="Curve%20Editor%20Custom%20Point%232.png" alt="Curve editor with another custom point" width="460"><br><sub>Curve editor — second point view</sub></td>
  </tr>
  <tr>
    <td align="center"><img src="Triboelectric%20charging%20panel.png" alt="Triboelectric charging controls and calculated output" width="460"><br><sub>Triboelectric charging controls</sub></td>
    <td align="center"><img src="Triboelectric%20charging%20panel2.png" alt="Alternate triboelectric charging panel" width="460"><br><sub>Alternate charging panel</sub></td>
  </tr>
  <tr>
    <td align="center"><img src="Triboelectric%20Charging%20grid%202D%20Blind%20default%20ablation.png" alt="2D dual grid with default ablation pattern" width="460"><br><sub>Dual grid — default ablation</sub></td>
    <td align="center"><img src="Triboelectric%20Charging%20grid%202D%20Blind%20scattered%20ablation.png" alt="2D dual grid with scattered ablation pattern" width="460"><br><sub>Dual grid — scattered ablation</sub></td>
  </tr>
  <tr>
    <td align="center"><img src="Material%20Research%20Sample.png" alt="Material research sample" width="460"><br><sub>Material research sample</sub></td>
    <td align="center"><img src="Title.png" alt="Full electroscope interface overview" width="460"><br><sub>Full interface overview</sub></td>
  </tr>
</table>

<details>
  <summary><strong>Unity ↔ Python TCP experiment captures — 10 steps</strong></summary>
  <br>
  <table>
    <tr>
      <td align="center"><img src="Communications/20261116_090858_093_Step01_01_GET_STATUS_DB.png" alt="Step 1: get status and database" width="460"><br><sub>Step 01 · GET_STATUS_DB</sub></td>
      <td align="center"><img src="Communications/20261116_090900_450_Step02_02_SET_WIRE_GEOMETRY.png" alt="Step 2: set wire geometry" width="460"><br><sub>Step 02 · SET_WIRE_GEOMETRY</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Communications/20261116_090901_516_Step03_03_EXECUTE_TRIBO.png" alt="Step 3: execute triboelectric scenario" width="460"><br><sub>Step 03 · EXECUTE_TRIBO</sub></td>
      <td align="center"><img src="Communications/20261116_090903_894_Step04_04_INJECT_CHARGE.png" alt="Step 4: inject charge" width="460"><br><sub>Step 04 · INJECT_CHARGE</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Communications/20261116_090906_848_Step05_05_BEM3D_INTACT_128.png" alt="Step 5: BEM 3D intact 128-panel state" width="460"><br><sub>Step 05 · BEM3D_INTACT_128</sub></td>
      <td align="center"><img src="Communications/20261116_090909_711_Step06_06_ABLATION_LEAF1_1_24.png" alt="Step 6: first leaf panel ablation" width="460"><br><sub>Step 06 · ABLATION_LEAF1_1_24</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Communications/20261116_090912_555_Step07_07_ABLATION_LEAF1_TIP_49_64.png" alt="Step 7: first leaf tip panel ablation" width="460"><br><sub>Step 07 · ABLATION_LEAF1_TIP_49_64</sub></td>
      <td align="center"><img src="Communications/20261116_090915_332_Step08_08_ABLATION_LEAF2_65_88.png" alt="Step 8: second leaf panel ablation" width="460"><br><sub>Step 08 · ABLATION_LEAF2_65_88</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Communications/20261116_090916_446_Step09_09_DUAL_GRID_2D_CHECKER.png" alt="Step 9: dual-grid checker pattern" width="460"><br><sub>Step 09 · DUAL_GRID_2D_CHECKER</sub></td>
      <td align="center"><img src="Communications/20261116_090922_361_Step10_10_RESEARCH_GRAPHENE.png" alt="Step 10: research Graphene" width="460"><br><sub>Step 10 · RESEARCH_GRAPHENE</sub></td>
    </tr>
  </table>
</details>

<details>
  <summary><strong>Selected experiment and milestone captures — 14 images</strong></summary>
  <br>
  <table>
    <tr>
      <td align="center"><img src="Important_Highlights/Baseline_Step003_Intact_Mesh_50nC.png" alt="Recorded intact-mesh baseline at 50 nC" width="460"><br><sub>Baseline · intact mesh · 50 nC</sub></td>
      <td align="center"><img src="Important_Highlights/Discovery1and4_Step012_Low_Charge_Sweep_10nC.png" alt="Recorded low-charge sweep at 10 nC" width="460"><br><sub>Low-charge sweep · 10 nC</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Important_Highlights/Discovery2_Step013_Midspan_Asymmetry_Subdomain_A.png" alt="Recorded midspan view for subdomain A" width="460"><br><sub>Midspan view · subdomain A</sub></td>
      <td align="center"><img src="Important_Highlights/Discovery2_Step014_Midspan_Asymmetry_Subdomain_B.png" alt="Recorded midspan view for subdomain B" width="460"><br><sub>Midspan view · subdomain B</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Important_Highlights/Discovery4_Step017_High_Charge_Nonlinearity_80nC.png" alt="Recorded high-charge case at 80 nC" width="460"><br><sub>High-charge case · 80 nC</sub></td>
      <td align="center"><img src="Important_Highlights/Milestone1_Step001_Blind_Calibration.png" alt="Recorded blind calibration step" width="460"><br><sub>Milestone 1 · blind calibration</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Important_Highlights/Milestone2a_Step004_Subdomain_A_Symmetry.png" alt="Recorded subdomain A symmetry case" width="460"><br><sub>Milestone 2a · subdomain A</sub></td>
      <td align="center"><img src="Important_Highlights/Milestone2b_Step005_Subdomain_B_Symmetry.png" alt="Recorded subdomain B symmetry case" width="460"><br><sub>Milestone 2b · subdomain B</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Important_Highlights/Milestone3_Step006_Edge_Effect_and_V_Q_C_Law.png" alt="Recorded edge-effect and V-Q-C law case" width="460"><br><sub>Milestone 3 · edge effect and V–Q–C</sub></td>
      <td align="center"><img src="Important_Highlights/Milestone4a_Step007_Quartz_Synthesis_MAT21.png" alt="Recorded Quartz material research step" width="460"><br><sub>Milestone 4a · Quartz material research</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Important_Highlights/Milestone4b_Step008_Tribo_Quartz_Aluminum.png" alt="Recorded Quartz and aluminum triboelectric scenario" width="460"><br><sub>Milestone 4b · Quartz and Aluminum Foil scenario</sub></td>
      <td align="center"><img src="Important_Highlights/Milestone4c_Step009_2D_Checkerboard_Topology.png" alt="Recorded 2D checkerboard topology" width="460"><br><sub>Milestone 4c · 2D checkerboard view</sub></td>
    </tr>
    <tr>
      <td align="center"><img src="Important_Highlights/Milestone5_Step010_Full_Lattice_Restoration.png" alt="Recorded full-lattice restoration step" width="460"><br><sub>Milestone 5 · full-lattice restoration</sub></td>
      <td align="center"><img src="Important_Highlights/Synthesis_Step019_Borosilicate_MAT22.png" alt="Recorded Borosilicate material research step" width="460"><br><sub>Step 19 · Borosilicate material research</sub></td>
    </tr>
  </table>
</details>

## Scope & Future Work

The electrostatic and agent-discovery benchmarks come from the deterministic
3D BEM numerical oracle. A saved solve reports a linear residual of
\(1.110\times10^{-15}\), and the metrological suite passes all 14 hard gates.
These results establish numerical consistency for the configured model; the
triboelectric charging figures are produced by the separate charging model.

The reported outputs are simulation results rather than independent laboratory
measurements. The runtime charging UI and tuner use different charge
calculations; the runtime UI omits relative-humidity and water-absorption
attenuation and material-specific dielectric-strength clamping. The Unity foil
simulator uses 20 fixed substeps per frame, while the notebook labels its
steady-state telemetry “RK4.” The transcript details Steps 1–19; Steps 20–27
were truncated after socket timeouts/API quota exhaustion, despite a final
summary claiming 27 steps. Its zero-leakage result counts configured lexical
prompt matches, not semantic leakage. Material source URLs are retained in
profiles but are not independently verified here.

The next research milestone is physical calibration: compare predicted leaf
angles against high-speed optical tracking and validate charge/potential
response with a reference electrometer.
