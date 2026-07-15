// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree.

using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace SEAN.Editor
{
    public class NavMeshMapExporter
    {
        const float Resolution = 0.1f;
        const float Margin = 2f;
        const float SampleMaxDistance = 0.3f;
        const byte FreeValue = 254;
        const byte OccupiedValue = 0;

        const string OutdoorScenePath = "Assets/Scenes/SEAN/Outdoor.unity";
        // From the live tf reading: `rosrun tf tf_echo map base_link` -> map (-65.399, 0.399).
        // ros_x = unity_z, ros_y = -unity_x (see conversion note below), so unity x=-0.399, z=-65.399.
        static readonly Vector2 ExpectedRobotUnityXZ = new Vector2(-0.399f, -65.399f);
        const float SanityToleranceMeters = 1.5f;
        const float PostCheckMaxOriginRosX = -67f;

        [MenuItem("SEAN/Export NavMesh Map")]
        public static void ExportNavMeshMap()
        {
            int width, height;
            float rosMinX, rosMinY;
            if (!RunExport(out width, out height, out rosMinX, out rosMinY))
            {
                return;
            }
            Debug.Log($"NavMeshMapExporter: exported {width}x{height} map, origin=({rosMinX:F3}, {rosMinY:F3}, 0) to {OutputDir}");
        }

        // Batchmode entry point for headless deployment runs:
        //   Unity -batchmode -quit -projectPath <project> -executeMethod SEAN.Editor.NavMeshMapExporter.ExportOutdoorBatch -logFile -
        // Opens the Outdoor scene, sanity-checks that the live tf position is actually navigable
        // before touching any files, then exports and post-checks the result.
        public static void ExportOutdoorBatch()
        {
            Scene scene = EditorSceneManager.OpenScene(OutdoorScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                throw new System.Exception($"NavMeshMapExporter: failed to open scene {OutdoorScenePath}");
            }

            // --- Informational only: the scene-saved spawn/base_link positions are NOT expected
            // to match the live tf reading once the robot has navigated away from spawn. ---
            Tasks.CustomStartGoal startGoal = FindFirst<Tasks.CustomStartGoal>();
            Scenario.Robot robot = FindSingleActiveRobot();
            Vector3? startLocationPos = (startGoal != null && startGoal.RobotStartLocation != null)
                ? startGoal.RobotStartLocation.transform.position
                : (Vector3?)null;
            Vector3? baseLinkPos = (robot != null && robot.base_link != null)
                ? robot.base_link.transform.position
                : (Vector3?)null;
            Debug.Log("NavMeshMapExporter: CustomStartGoal.RobotStartLocation position = " +
                (startLocationPos.HasValue ? startLocationPos.Value.ToString("F3") : "NOT FOUND") + " (informational)");
            Debug.Log("NavMeshMapExporter: robot base_link position = " +
                (baseLinkPos.HasValue ? baseLinkPos.Value.ToString("F3") : "NOT FOUND") + " (informational)");

            // --- Sanity gate: is the live tf position actually navigable in this scene's NavMesh? ---
            NavMeshTriangulation triCheck = NavMesh.CalculateTriangulation();
            if (triCheck.vertices.Length == 0)
            {
                throw new System.Exception("NavMeshMapExporter: no NavMesh data found. Bake the Outdoor scene's NavMesh before exporting.");
            }
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (Vector3 v in triCheck.vertices)
            {
                minY = Mathf.Min(minY, v.y);
                maxY = Mathf.Max(maxY, v.y);
            }
            float sampleY = (minY + maxY) * 0.5f;
            Vector3 expectedUnityPos = new Vector3(ExpectedRobotUnityXZ.x, sampleY, ExpectedRobotUnityXZ.y);

            NavMeshHit gateHit;
            bool onNavMesh = NavMesh.SamplePosition(expectedUnityPos, out gateHit, SanityToleranceMeters, NavMesh.AllAreas);
            Debug.Log($"NavMeshMapExporter: NavMesh check at expected robot position {expectedUnityPos:F3} (from tf map->base_link " +
                $"-65.399, 0.399) = {(onNavMesh ? "ON NAVMESH, hit=" + gateHit.position.ToString("F3") : "NOT ON NAVMESH")}");

            if (!onNavMesh)
            {
                throw new System.Exception(
                    $"NavMeshMapExporter: SANITY GATE FAILED - expected robot position {expectedUnityPos:F3} " +
                    $"(unity x={ExpectedRobotUnityXZ.x:F3}, z={ExpectedRobotUnityXZ.y:F3}, from tf map->base_link -65.399, 0.399) " +
                    $"is not within {SanityToleranceMeters}m of any NavMesh surface. Aborting export - no files were written.");
            }
            Debug.Log("NavMeshMapExporter: sanity gate PASSED (live tf position is navigable)");

            // --- Export ---
            int width, height;
            float rosMinX, rosMinY;
            if (!RunExport(out width, out height, out rosMinX, out rosMinY))
            {
                throw new System.Exception("NavMeshMapExporter: export failed - no NavMesh data found in " + OutdoorScenePath);
            }

            // --- Post-check: the exported map must actually cover the robot at ros_x=-65.399 ---
            if (rosMinX > PostCheckMaxOriginRosX)
            {
                throw new System.Exception(
                    $"NavMeshMapExporter: POST-CHECK FAILED - origin ros_x={rosMinX:F3} is greater than " +
                    $"{PostCheckMaxOriginRosX} (map does not cover the robot at ros_x=-65.399). Aborting.");
            }

            Debug.Log($"NavMeshMapExporter: BATCH EXPORT COMPLETE width={width} height={height} origin=({rosMinX:F3}, {rosMinY:F3}, 0)");
        }

        static bool MatchesExpectedRobotPosition(Vector3? pos)
        {
            if (!pos.HasValue) return false;
            return Mathf.Abs(pos.Value.x - ExpectedRobotUnityXZ.x) <= SanityToleranceMeters
                && Mathf.Abs(pos.Value.z - ExpectedRobotUnityXZ.y) <= SanityToleranceMeters;
        }

        static T FindFirst<T>() where T : UnityEngine.Object
        {
            T[] all = UnityEngine.Object.FindObjectsOfType<T>(true);
            return all.Length > 0 ? all[0] : null;
        }

        static Scenario.Robot FindSingleActiveRobot()
        {
            Scenario.Robot[] all = UnityEngine.Object.FindObjectsOfType<Scenario.Robot>(true);
            Scenario.Robot active = null;
            int activeCount = 0;
            foreach (Scenario.Robot r in all)
            {
                if (r.gameObject.activeInHierarchy)
                {
                    active = r;
                    activeCount++;
                }
            }
            if (activeCount != 1)
            {
                Debug.LogWarning($"NavMeshMapExporter: expected exactly 1 active Robot, found {activeCount} (of {all.Length} total).");
            }
            return active;
        }

        static string OutputDir
        {
            get
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(projectRoot, "ExportedMaps", "outdoor");
            }
        }

        // Does the actual triangulation -> grid -> sample -> write work. Returns false (no files
        // written) if there is no NavMesh data to export.
        static bool RunExport(out int width, out int height, out float rosMinX, out float rosMinY)
        {
            width = height = 0;
            rosMinX = rosMinY = 0;

            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            if (tri.vertices.Length == 0)
            {
                Debug.LogError("NavMeshMapExporter: no NavMesh data found. Open the Outdoor scene and bake its NavMesh before exporting.");
                return false;
            }

            // Unity-space (X right, Z forward) bounds of the navmesh, expanded by a margin.
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (Vector3 v in tri.vertices)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
                minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
            }
            minX -= Margin; maxX += Margin;
            minZ -= Margin; maxZ += Margin;
            // NavMesh.SamplePosition needs a query height; outdoor terrain is assumed to stay
            // within SampleMaxDistance of this center height across the whole grid.
            float sampleY = (minY + maxY) * 0.5f;

            // Unity world (x, y, z) -> ROS map (x, y): ros_x = unity_z, ros_y = -unity_x.
            // Matches SEAN.TF.WorldTransformPublishers, which builds map_to_base_link/map_to_odom
            // directly from the robot's Unity world position via Vector3.To<FLU>() with no extra
            // offset, so Unity world (0,0,0) is ROS map (0,0).
            rosMinX = minZ;
            float rosMaxX = maxZ;
            rosMinY = -maxX;
            float rosMaxY = -minX;

            width = Mathf.CeilToInt((rosMaxX - rosMinX) / Resolution);
            height = Mathf.CeilToInt((rosMaxY - rosMinY) / Resolution);

            byte[] pixels = new byte[width * height];

            bool cancelled = false;
            for (int row = 0; row < height; row++)
            {
                if (row % 8 == 0)
                {
                    cancelled = EditorUtility.DisplayCancelableProgressBar(
                        "Exporting NavMesh Map", $"Row {row}/{height}", (float)row / height);
                    if (cancelled) break;
                }

                // PGM row 0 is the top of the image, which map_server treats as the highest ROS y.
                float rosY = rosMinY + (height - row - 0.5f) * Resolution;
                float unityX = -rosY;

                for (int col = 0; col < width; col++)
                {
                    float rosX = rosMinX + (col + 0.5f) * Resolution;
                    float unityZ = rosX;

                    NavMeshHit hit;
                    bool onNavMesh = NavMesh.SamplePosition(
                        new Vector3(unityX, sampleY, unityZ), out hit, SampleMaxDistance, NavMesh.AllAreas);
                    pixels[row * width + col] = onNavMesh ? FreeValue : OccupiedValue;
                }
            }
            EditorUtility.ClearProgressBar();
            if (cancelled)
            {
                Debug.LogWarning("NavMeshMapExporter: export cancelled.");
                return false;
            }

            string outDir = OutputDir;
            Directory.CreateDirectory(outDir);

            WritePgm(Path.Combine(outDir, "map.pgm"), width, height, pixels);
            WriteYaml(Path.Combine(outDir, "map.yaml"), rosMinX, rosMinY);
            return true;
        }

        static void WritePgm(string path, int width, int height, byte[] pixels)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
                stream.Write(header, 0, header.Length);
                stream.Write(pixels, 0, pixels.Length);
            }
        }

        static void WriteYaml(string path, float originX, float originY)
        {
            string yaml =
                "image: map.pgm\n" +
                $"resolution: {Resolution}\n" +
                $"origin: [{originX:F6}, {originY:F6}, 0.0]\n" +
                "negate: 0\n" +
                "occupied_thresh: 0.65\n" +
                "free_thresh: 0.196\n";
            File.WriteAllText(path, yaml);
        }
    }
}
