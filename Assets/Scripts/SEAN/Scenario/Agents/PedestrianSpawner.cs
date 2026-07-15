using System.Collections.Generic;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// One row of the Inspector-configurable spawn list. This pass only supports the
    /// TransformList spawn mode (Point/Area modes from PEDESTRIAN_SPAWNER_DESIGN.md §2.2 are
    /// out of scope) -- spawnPoints are scene Transforms dragged in the Inspector, one is
    /// picked at random per spawned agent.
    /// </summary>
    [System.Serializable]
    public class SpawnGroupConfig
    {
        public string label;
        public PedestrianModulator.PersonalityType personality = PedestrianModulator.PersonalityType.Indifferent;
        public int count;
        public List<Transform> spawnPoints;

        // Patrol is orthogonal to personality (not a PersonalityType case) -- e.g. a
        // Surprised patroller still reacts to the robot, it just resumes ping-ponging
        // between patrolPointA/patrolPointB afterwards. See PedestrianModulator.EnablePatrol().
        public bool patrol = false;
        public Transform patrolPointA;
        public Transform patrolPointB;

        // null => fall back to PedestrianSpawner.agentPrefab (see SpawnAgent()). Lets a group
        // spawn a different appearance (e.g. an AppearanceAvatar container wrapping a
        // special-character avatar) without affecting other groups.
        public GameObject appearancePrefabOverride;

        // Shares PedestrianModulator.walkSpeedMultiplier's modulation hook (Scale()) -- != 1.0f
        // forces a PedestrianModulator onto this group even when Indifferent, since that's the
        // common case for e.g. a slower-walking child appearance (see SpawnAgent()).
        public float walkSpeedMultiplier = 1.0f;
    }

    /// <summary>
    /// Minimal closed-loop configurable pedestrian spawner (PEDESTRIAN_SPAWNER_DESIGN.md §2.2).
    /// Sibling of RandomABNavAgentManager/IVI.NavManager under Agents.BaseAgentManager's single
    /// instance slot -- activated as its own scenario via PedestrianBehavior/ConfigurableSpawner.cs
    /// so spawned agents are visible to PositionPublisher/Metrics/GroupPublisher like any other
    /// scenario's agents (see design doc §1.6).
    ///
    /// agentPrefab is expected to carry an AppearanceAvatar component referencing an avatar
    /// array (see Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab) -- a group can override
    /// this per-group via SpawnGroupConfig.appearancePrefabOverride (must be a prefab with its
    /// own AppearanceAvatar component, not a bare avatar body -- see SpawnAgent()). Personality
    /// supports Scared and Curious; Indifferent groups spawn without a PedestrianModulator
    /// component at all (Base.cs's ModulateVelocity() then no-ops) unless walkSpeedMultiplier
    /// requires one; Surprised is reserved in the enum but not implemented.
    /// </summary>
    public class PedestrianSpawner : BaseAgentManager
    {
        public List<SpawnGroupConfig> spawnGroups = new List<SpawnGroupConfig>();

        public GameObject agentPrefab;

        public List<IVI.INavigable> agents;
        public List<Trajectory.TrackedGroup> groups;

        private GameObject agentsGO;

        void Update()
        {
            foreach (var agent in agents)
            {
                var modulator = agent.gameObject.GetComponent<PedestrianModulator>();
                if (modulator != null && modulator.IsControllingDestination)
                {
                    // Curious's Approach/Follow states are driving destPos themselves via
                    // InitDest() -- don't fight them with a random-walk retarget (V2 §2.6).
                    continue;
                }
                if (agent.CloseEnough())
                {
                    agent.InitDest(Util.Navmesh.RandomPose().position);
                }
            }
        }

        public void Restart()
        {
            foreach (Transform child in transform)
            {
                if (child.gameObject.name == "Agents")
                {
                    agentsGO = child.gameObject;
                }
            }
            Clear();
            foreach (var group in spawnGroups)
            {
                if (group.spawnPoints == null || group.spawnPoints.Count == 0)
                {
                    Debug.LogWarning("PedestrianSpawner: group '" + group.label + "' has no spawn points assigned, skipping.");
                    continue;
                }
                string groupLabel = string.IsNullOrEmpty(group.label) ? "Group" : group.label;
                for (int i = 0; i < group.count; i++)
                {
                    Transform point = group.spawnPoints[Random.Range(0, group.spawnPoints.Count)];
                    var hit = Util.Navmesh.RandomHit(point.position, 0f, 2f);
                    var pose = new Pose(hit.position, Util.Navmesh.RandomRotation());
                    SpawnAgent(group, groupLabel + "_" + i, pose);
                }
            }
        }

        void Clear()
        {
            if (!agentsGO) { return; }
            agents = new List<IVI.INavigable>();
            groups = new List<Trajectory.TrackedGroup>();
            foreach (Transform child in agentsGO.transform)
            {
                GameObject.Destroy(child.gameObject);
            }
        }

        IVI.INavigable SpawnAgent(SpawnGroupConfig group, string name, Pose pose)
        {
            var prefabToUse = group.appearancePrefabOverride != null ? group.appearancePrefabOverride : agentPrefab;
            var container = Instantiate(prefabToUse, Vector3.zero, Quaternion.identity);
            IVI.INavigable agent = container.GetComponentInChildren<IVI.INavigable>();
            agent.name = name;
            agent.transform.position = pose.position;
            agent.transform.rotation = pose.rotation;
            agent.transform.parent = agentsGO.transform;

            bool patrolValid = group.patrol && group.patrolPointA != null && group.patrolPointB != null;
            if (group.patrol && !patrolValid)
            {
                Debug.LogError("PedestrianSpawner: group '" + group.label + "' has patrol enabled but patrolPointA/patrolPointB is missing, falling back to random walk.");
            }

            // Indifferent = no modulator component at all, Base.ModulateVelocity() then
            // no-ops via a null GetComponent<IVelocityModulator>() result. Patrol groups
            // attach the modulator even when Indifferent -- Indifferent modulation is a
            // passthrough (Modulate()'s Indifferent case just returns
            // Scale(socialForceVelocity)), and the patrol ping-pong check runs ahead of
            // that switch regardless of personality (see PedestrianModulator.Modulate()).
            // Also force a modulator when walkSpeedMultiplier != 1.0f -- an Indifferent group
            // (e.g. slower-walking children) still needs Scale() to apply the speed scaling.
            if (group.personality != PedestrianModulator.PersonalityType.Indifferent || patrolValid
                || group.walkSpeedMultiplier != 1.0f)
            {
                var modulator = agent.gameObject.AddComponent<PedestrianModulator>();
                modulator.personality = group.personality;
                modulator.walkSpeedMultiplier = group.walkSpeedMultiplier;
                if (patrolValid)
                {
                    modulator.EnablePatrol(group.patrolPointA.position, group.patrolPointB.position);
                }
            }

            agents.Add(agent);
            agent.InitDest(patrolValid ? group.patrolPointA.position : Util.Navmesh.RandomPose().position);
            return agent;
        }
    }
}
