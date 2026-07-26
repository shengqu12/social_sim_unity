using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 5: builds a narrow corridor out of two parallel walls, centred on the
    /// robot's own start->goal bearing, so head-on encounters can be run at a controlled passable
    /// width (the ticket's 3.0 / 2.0 / 1.5 / 1.2 m sweep).
    ///
    /// WHY THIS IS RUNTIME GEOMETRY AND NOT THE NEW SCENE FILE THE TICKET ASKED FOR
    /// ---------------------------------------------------------------------------
    /// The ticket specifies `Assets/Scenes/Corridor/CorridorTest.unity`. That is not achievable
    /// without a change outside this project's writable scope: navigation in this pipeline is not
    /// scene-local. `AutoTrialEditorRunner.EnterPlay` explicitly opens Assets/Scenes/SEAN/
    /// Outdoor.unity, and move_base localizes/plans against a matching pre-built ROS occupancy map
    /// served by `roslaunch social_sim_ros map_server.launch scene:=outdoor` from
    /// social_sim_ros/maps/outdoor/map.yaml. A brand-new Unity scene has no corresponding ROS map,
    /// so the robot would have no costmap to plan in and would not move at all -- and authoring
    /// that map means writing into sim_ws, which is read-only by default here.
    ///
    /// Spawning the walls into the existing, already-map-matched Outdoor corridor instead gets the
    /// scientifically load-bearing property the ticket actually wants (a controlled, laser-visible
    /// passable width that forces the 3.0 -> 1.2m behavioural progression) with zero shared-scene
    /// edits, zero sim_ws edits, and zero new ROS maps. The walls are real colliders in the robot's
    /// depth-camera/laser path, so they enter the costmap through the normal live-perception route
    /// rather than needing to pre-exist in the static map.
    ///
    /// Placement: centred on the pedestrian's own spawn point projected onto the robot start->goal
    /// line, so the corridor brackets the encounter rather than sitting somewhere the robot passes
    /// before or after meeting the pedestrian.
    /// </summary>
    public class S41CorridorBuilder : MonoBehaviour
    {
        public float widthMeters = 2.0f;
        public float lengthMeters = 12.0f;
        // >= 1.0m so the walls are unambiguously visible to the robot's sensor plane (the
        // RealSense sits at 0.32m); 0.2m thick so they read as solid walls, not thin panes.
        public float heightMeters = 1.6f;
        public float thicknessMeters = 0.2f;

        private GameObject root;

        // ---- deferred build ----
        // The first implementation built the corridor during bootstrap and put it ~60m off target:
        // at that moment the robot has not yet been teleported to its scene start pose, so
        // robot.transform.position is meaningless. Instead the corridor waits until the robot and
        // pedestrian are genuinely closing, then centres on their live midpoint -- which is where
        // the head-on pass actually occurs, and is robust to any scenario's spawn geometry.
        [HideInInspector] public Transform pedestrian;
        [HideInInspector] public float buildWhenDistanceBelow = 12.0f;
        private bool built;

        void Update()
        {
            if (built || pedestrian == null) { return; }
            if (SEAN.instance == null) { return; }

            Scenario.Robot robot;
            try { robot = SEAN.instance.robot; }
            catch (System.Exception) { return; }

            Vector3 robotPos = robot.position;
            Vector3 pedPos = pedestrian.position;
            Vector3 toPed = pedPos - robotPos;
            toPed.y = 0f;
            if (toPed.magnitude > buildWhenDistanceBelow) { return; }

            built = true;
            Vector3 center = (robotPos + pedPos) * 0.5f;
            center.y = 0f;
            // Corridor axis is the robot's direction of travel, i.e. toward the pedestrian.
            Build(center, toPed);
        }

        /// <summary>
        /// Builds the corridor along `bearing` (robot start -> goal), centred at `center`.
        /// Returns the created root so the caller can log/verify it.
        /// </summary>
        public GameObject Build(Vector3 center, Vector3 bearing)
        {
            if (root != null) { Destroy(root); }

            bearing.y = 0f;
            if (bearing.sqrMagnitude < 1e-6f)
            {
                Debug.LogError("[S41Corridor] degenerate bearing -- refusing to build.");
                return null;
            }
            Vector3 unit = bearing.normalized;
            Vector3 perp = Vector3.Cross(Vector3.up, unit).normalized;

            root = new GameObject("S41Corridor_w" + widthMeters.ToString("F1"));
            root.transform.position = center;

            float halfW = widthMeters * 0.5f;
            BuildWall(perp * (halfW + thicknessMeters * 0.5f), unit, "WallLeft");
            BuildWall(-perp * (halfW + thicknessMeters * 0.5f), unit, "WallRight");

            Debug.Log(string.Format(
                "[S41Corridor] built width={0:F2}m length={1:F2}m height={2:F2}m at ({3:F2},{4:F2},{5:F2}) " +
                "bearing=({6:F3},{7:F3}) -- passable gap between wall inner faces = {8:F2}m",
                widthMeters, lengthMeters, heightMeters, center.x, center.y, center.z,
                unit.x, unit.z, widthMeters));
            return root;
        }

        private void BuildWall(Vector3 lateralOffset, Vector3 unit, string name)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.parent = root.transform;
            // Cube pivot is centred, so lift by half the height to sit the base on the ground.
            wall.transform.localPosition = lateralOffset + Vector3.up * (heightMeters * 0.5f);
            wall.transform.localRotation = Quaternion.LookRotation(unit, Vector3.up);
            // LookRotation puts +Z along the corridor, so the cube's Z is its length.
            wall.transform.localScale = new Vector3(thicknessMeters, heightMeters, lengthMeters);

            // Matte mid-grey. Deliberately non-reflective for the same reason TASK 4's carried box
            // is: specular highlights are a confound for VLM judgement of the scene.
            var renderer = wall.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                renderer.material.color = new Color(0.55f, 0.55f, 0.57f);
                if (renderer.material.HasProperty("_Glossiness")) { renderer.material.SetFloat("_Glossiness", 0f); }
                if (renderer.material.HasProperty("_Metallic")) { renderer.material.SetFloat("_Metallic", 0f); }
            }
            // Keeps its BoxCollider (CreatePrimitive default) -- that is what makes it a real
            // obstacle for both the depth camera and any physics query.
        }
    }
}
