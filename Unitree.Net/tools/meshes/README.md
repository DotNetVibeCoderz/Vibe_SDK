# Robot meshes

`convert_meshes.py` turns Unitree's URDF visual meshes into the GLB files the simulator's viewport
loads from `apps/Unitree.Net.Simulator/wwwroot/models`. `urdf_to_rig.py` prints a humanoid's
`RobotRig` rows from its URDF, so that a hundred-odd joint numbers are not copied by hand. Usage, and why it does what it does, are in the
script's docstring. How the viewport uses the result is in
[docs/simulator.md](../../docs/simulator.md#manufacturer-meshes).

The sources are [`unitree_ros`](https://github.com/unitreerobotics/unitree_ros) and, for Go2-W,
[`unitree_mujoco`](https://github.com/unitreerobotics/unitree_mujoco); both BSD-3-Clause. Their
notices ship beside the converted files in `wwwroot/models`.

| Platform | Source |
|---|---|
| Go2 | `unitree_ros/robots/go2_description/dae` |
| Go2-W | Go2's body, hips and thighs; calves and wheel from `unitree_mujoco/unitree_robots/go2w/assets` |
| B2 | `unitree_ros/robots/b2_description/meshes` (per-leg files; `base_link` renamed `base`) |
| B2-W | B2's body, hips and thighs; calves and wheels from `unitree_ros/robots/b2w_description/meshes` |
| G1 | `unitree_ros/robots/g1_description/meshes`, as used by `g1_29dof_rev_1_0.urdf` |
| H1 | `unitree_ros/robots/h1_description/meshes` (torso and logo merged) |
| H1-2 | `unitree_ros/robots/h1_2_description/meshes`, as used by `h1_2_handless.urdf` |
| R1 | `unitree_ros/robots/r1_description/meshes` |
