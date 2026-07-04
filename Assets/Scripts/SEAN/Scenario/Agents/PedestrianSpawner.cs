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
    }

    /// <summary>
    /// Minimal closed-loop configurable pedestrian spawner (PEDESTRIAN_SPAWNER_DESIGN.md §2.2).
    /// Sibling of RandomABNavAgentManager/IVI.NavManager under Agents.BaseAgentManager's single
    /// instance slot -- activated as its own scenario via PedestrianBehavior/ConfigurableSpawner.cs
    /// so spawned agents are visible to PositionPublisher/Metrics/GroupPublisher like any other
    /// scenario's agents (see design doc §1.6).
    ///
    /// Appearance is fixed to "Simple" this pass: agentPrefab is expected to carry an
    /// AppearanceAvatar component referencing the Rocketbox Female_Adult_01/02 array (see
    /// Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab). Personality supports Scared and
    /// Curious; Indifferent groups spawn without a PedestrianModulator component at all (Base.cs's
    /// ModulateVelocity() then no-ops); Surprised is reserved in the enum but not implemented.
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
            var container = Instantiate(agentPrefab, Vector3.zero, Quaternion.identity);
            IVI.INavigable agent = container.GetComponentInChildren<IVI.INavigable>();
            agent.name = name;
            agent.transform.position = pose.position;
            agent.transform.rotation = pose.rotation;
            agent.transform.parent = agentsGO.transform;

            // Indifferent = no modulator component at all, Base.ModulateVelocity() then
            // no-ops via a null GetComponent<IVelocityModulator>() result.
            if (group.personality != PedestrianModulator.PersonalityType.Indifferent)
            {
                var modulator = agent.gameObject.AddComponent<PedestrianModulator>();
                modulator.personality = group.personality;
            }

            agents.Add(agent);
            agent.InitDest(Util.Navmesh.RandomPose().position);
            return agent;
        }
    }
}
