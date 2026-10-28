# Robot meshes

`convert_meshes.py` turns Unitree's URDF visual meshes into the GLB files the simulator's viewport
loads from `apps/Unitree.Net.Simulator/wwwroot/models`. Usage, and why it does what it does, are in the
script's docstring. How the viewport uses the result is in
[docs/simulator.md](../../docs/simulator.md#manufacturer-meshes).

The source is [`unitree_ros`](https://github.com/unitreerobotics/unitree_ros), BSD-3-Clause. Its
notice ships beside the converted files as `wwwroot/models/LICENSE.unitree_ros`.

| Platform | Source | Status |
|---|---|---|
| Go2 | `robots/go2_description/dae` | Converted |
| B2, B2-W | `robots/b2_description`, `robots/b2w_description` | Not yet |
| G1, H1, H1-2 | `robots/g1_description`, `robots/h1_description`, `robots/h1_2_description` | Not yet. The humanoid rigs do not use the URDF's link frames yet, so these need the rig changed first |
| Go2-W, R1 | — | `unitree_ros` has no meshes for them |
