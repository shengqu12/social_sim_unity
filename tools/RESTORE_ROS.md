# RESTORE_ROS.md — bringing the `ros` container + navstack back up

**Machine:** `sheng-linux`, Tailscale **`100.79.138.34`**.
(An older `100.113.199.85` appears in handoff headers up to S73-P4-UNBLOCK. It is
**STALE** — do not use it. What it currently belongs to is unknown.)

This exists because "the container is gone" has cost multiple sessions. It should now
cost about a minute.

---

## TL;DR — every session starts with this

```bash
sean-up                                    # 1. container (~2 s)
# 2. then run the FIRST trial of the session with --fresh-ros:
python3 tools/run_trial.py --fresh-ros ...  # does TEB preflight + canonical bringup + health wait
```

**`sean-up` is NOT enough on its own** — it gives you an empty container with no ROS in
it. The easiest correct path is to let `run_trial.py --fresh-ros` do the navstack bringup
for you: it also runs the `teb_local_planner` preflight that a hand-rolled bringup skips
(§4). Bring the navstack up by hand (§3) only if you need it without running a trial —
and if you do, still run your first trial with `--fresh-ros`.

---

## 1. Why the container is always missing — it is not broken

`~/sim_ws/container:89` starts it with **`docker run --rm`**. That flag means Docker
**deletes the container the moment it stops** — on reboot, on `sean-stop`, on any
crash. There is no systemd unit, no autostart entry, and no compose service that
recreates it (`docker ps -a` will be *empty*, not "exited").

**So an absent container is the expected steady state, not evidence of a problem.**
Do not go looking for what deleted it. Start it and move on.

The image itself is persistent and safe: `ros:latest` (~26.9 GB). Only the *container*
is ephemeral.

> The five `*:social-sim` images referenced by
> `~/Desktop/research/social_navigation/sim_ws/docker/docker-compose.yml` are **not**
> this path and are not built on this machine. Ignore that compose stack.

---

## 2. Step 1 — the container

```bash
sean-up
```

### `sean-up` vs `sean-go` — pick the right one

| Alias | Definition (`~/.zshrc:3,6,9`) | Use when |
|---|---|---|
| `sean-up` | `cd ~/sim_ws && (docker ps \| grep -q " ros$" \|\| ./container start ros)` | **Automation / running trials.** Starts the container if absent, then returns. Idempotent — safe to run when it is already up. |
| `sean-shell` | `cd ~/sim_ws && ./container shell ros` | You want an interactive shell inside. |
| `sean-go` | `sean-up && sean-shell` | **Interactive work.** Starts it *and drops you inside.* |
| `sean-stop` | `docker stop ros` | Tears it down (and, via `--rm`, deletes it). |

**Use `sean-up` before a trial run, not `sean-go`** — `sean-go` leaves you sitting in
an interactive container shell, which is not what a scripted `run_trial.py` needs and
will block a non-interactive caller.

Underneath, `./container start ros` runs (`~/sim_ws/container:89-103`):

```bash
docker run --rm --gpus all \
  -v /home/sheng/sim_ws:/home/sheng/sim_ws \
  -v /home/sheng/Desktop/research/social_navigation/social_sim_unity:/home/sheng/social_sim_unity \
  -v /data:/data \
  -v /mnt/ssd/ros_out:/home/sheng/.ros \
  -v /home/sheng/.ssh/authorized_keys:/home/sheng/.ssh/authorized_keys \
  -v /home/sheng/.bash_history:/home/sheng/.bash_history \
  -v /tmp/.X11-unix:/tmp/.X11-unix:ro \
  -v /dev/shm:/dev/shm \
  -e DISPLAY=:1 \
  -p 6080:80 -p 9090:9090 -p 10000:10000 \
  -it -d --name=ros ros /bin/bash -l
```

The name **must** be `ros` — `tools/run_trial.py:70` hard-codes
`DOCKER_CONTAINER = "ros"`. The script also runs `xhost +local:` first.

---

## 3. Step 2 — the navstack (the part that is easy to forget)

The container starts `/bin/bash -l` and **nothing else**. No roscore, no move_base.
`rostopic list` will say *"Unable to communicate with master!"* until you do this.

Use the bringup documented in **`tools/run_trial.py:36-47`**, extracted read-only from
the launch files. Run each in its own shell/pane inside the container:

```bash
S='source /opt/ros/noetic/setup.bash; source ~/sim_ws/devel/setup.bash;'
docker exec -d ros bash -lc "$S nohup roscore > /tmp/roscore.log 2>&1"
sleep 3
docker exec -d ros bash -lc "$S nohup roslaunch --wait social_sim_ros map_server.launch scene:=outdoor > /tmp/map_server.log 2>&1"
docker exec -d ros bash -lc "$S nohup roslaunch --wait social_sim_ros sean_navstack.launch scene:=outdoor prefix:=<run-name> > /tmp/navstack.log 2>&1"
```

`map_server.launch` is a **separate process** — `sean_navstack.launch` does *not*
include it. It publishes `/map` from `social_sim_ros/maps/<scene>/map.yaml`.

### ⚠ Do NOT use `~/sim_ws/start_ros.sh` for trials
It runs `sean_navstack.launch scene:=labstudy prefix:=test` and **omits
`map_server.launch` entirely**. Wrong map, no `/map` publisher. It is fine for
interactive poking, wrong for `run_trial.py`.

(Available scenes: `ETH hotel lab labstudy outdoor university warehouse
warehouse_small zara`. Trials use **`outdoor`**.)

---

## 4. Post-start checks

```bash
S='source /opt/ros/noetic/setup.bash; source ~/sim_ws/devel/setup.bash;'
docker ps --format '{{.Names}}\t{{.Status}}'                       # ros, Up
docker exec ros bash -lc "$S rosnode list"                          # expect 10 nodes
docker exec ros bash -lc "$S rostopic info /move_base_simple/goal"  # /move_base must be a subscriber
docker exec ros bash -lc "$S rostopic echo -n1 /map/header"         # frame_id: "map"
docker exec ros bash -lc "ps aux | grep -o 'scene:=[^ ]*' | sort -u" # exactly one scene
```

Expected 10 nodes: `/depth_synchronizer /depthimage_to_laserscan /map_publisher
/map_server /move_base /pose_to_people /robot_state_publisher /rosout /tcp_server
/trial_info`.

### What is normal on a cold bringup, and what is not

**Normal — do not chase these:**
- `/move_base/clear_costmaps` and `/move_base/set_parameters` are **absent** until a
  real nav cycle has run. `run_trial.py`'s warmup primes move_base with a guarded
  DiagCmdVel cycle; that is the fix, and the tool does it for you.
- `dynparam set oscillation_timeout 3.0` failing / staying at `1.0`. Explicitly
  documented as cosmetic in `run_trial.py`'s warmup docstring.
- A KDL warning about `base_link` inertia in the URDF.

- **`scanReceived=False costmapReceived=False`** in `unity.log` is a **RED HERRING**.
  The costmaps run with `obstacles_layer.enabled: false` and no `observation_sources`
  (`params/kuri/costmap_common_params.yaml`, `map_nav_params/local_costmap_params.yaml`),
  so move_base **never subscribes to `/scan` by design**. `depthimage_to_laserscan` uses
  lazy subscription, so with no `/scan` subscriber it never subscribes to the depth image
  either. The entire depth→scan chain sitting silent is **expected**. The readiness gate
  logs these as *best-effort, not gating* for exactly this reason. Do not chase it.

### Normal on batchmode trial runs — do not investigate, do not commit

- **`UserSettings/Layouts/default-2022.dwlt` shows as modified (` M`) after any batchmode
  run.** This is live Unity editor layout state that the Editor rewrites on launch.

  **Do not commit it and do not revert it** — reverting throws away real editor state.
  Just leave it dirty; it is expected noise, not a trial artifact.

  Note it is **tracked** (`git ls-files` matches) and **not** covered by `.gitignore`
  (line 54 covers only `UserSettings/EditorUserSettings.asset`). Because it is already
  tracked, **adding it to `.gitignore` would not silence it** — `.gitignore` only affects
  untracked files. Properly stopping it would need `git rm --cached`, an index change.
  **Flagged only; no ignore-file or index change made** (S73 F2).

  `run_trial.py`'s snapshot/revert guard (`snapshot_modified_tracked_files()`) does not
  cover it, which is why it survives a run.

**NOT normal — a trial will produce garbage if you ignore it:**
- **`move_base` crash-looping (`process has died ... exit code 1`, respawn=true) with no
  `move_base-*.log` ever written.** This is the **Session 30R root cause** and it cost
  S73 Phase 4 two failed trials before diagnosis.

  Check: `docker exec ros bash -lc 'source /opt/ros/noetic/setup.bash; rospack find teb_local_planner'`

  If ABSENT: `params/kuri/move_base_params.yaml:21` sets
  `base_local_planner: teb_local_planner/TebLocalPlannerROS`, so `MoveBase::MoveBase()`
  throws FATAL at `move_base.cpp:142` and dies ~every 1.1 s, forever. It does **not**
  self-resolve. TEB was installed live into the old long-running container and never
  baked into `ros:latest`, so **every freshly-created container starts without it** —
  and `--rm` means every session gets a freshly-created container.

  Symptom at the trial level: `robotSpeedAtTrigger=0.000`, SLATE trigger timeout,
  `dist0` stuck at its spawn value. The robot has no planner, so it never moves.

  **Fix — no manual apt needed: just run the first trial of a session with `--fresh-ros`.**
  `run_trial.py`'s `ros_fresh_bringup()` calls `ensure_teb_plugin_installed()` as a
  preflight (idempotent, one-time per container), then does the canonical bringup and
  waits for a *stable* move_base PID. Observed healthy in 8 s once TEB was present.

  ⚠ The preflight lives **inside** `ros_fresh_bringup()`. A hand-rolled bringup (§3)
  followed by the default `--reused-ros` **skips it entirely** — and the health check can
  still pass, because it can catch move_base in an alive-but-wedged window. That is
  exactly how S73 Phase 4 lost two trials.

  The durable fix (baking TEB into `ros:latest` via `docker commit` or the Dockerfile) is
  still owed — flagged in `HOWARD_HANDOFF.md`.

---

## 5. Teardown

```bash
sean-stop     # stops and (via --rm) deletes the container; ROS dies with it
```

Nothing persists except `/mnt/ssd/ros_out` (mounted as `~/.ros` inside) and anything
under the mounted `sim_ws` / `social_sim_unity` trees.
