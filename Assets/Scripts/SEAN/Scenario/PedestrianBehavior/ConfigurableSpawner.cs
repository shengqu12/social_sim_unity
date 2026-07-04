using UnityEngine;

namespace SEAN.Scenario.PedestrianBehavior
{
    /// <summary>
    /// Scenario selector for the minimal configurable pedestrian spawner
    /// (PEDESTRIAN_SPAWNER_DESIGN.md §2.5). Mirrors PedestrianBehavior.Random: finds the
    /// scene-specific "ConfigurableSpawnerRoot" child under /Environment/PedestrianControl,
    /// activates it, and forwards its agents/groups so PositionPublisher/Metrics/GroupPublisher/
    /// the situation classifier see these pedestrians like any other scenario's.
    /// </summary>
    public class ConfigurableSpawner : Base
    {
        Agents.PedestrianSpawner agentManager;
        GameObject configurableSpawnerRoot;

        public override string scenario_name
        {
            get
            {
                return "ConfigurableSpawner";
            }
        }

        public void Start()
        {
            base.Start();
            foreach (Transform transform in pedestrianControl.transform)
            {
                if (transform.name == "ConfigurableSpawnerRoot")
                {
                    configurableSpawnerRoot = transform.gameObject;
                    break;
                }
            }
            if (configurableSpawnerRoot == null)
            {
                throw new System.Exception("Could not find ConfigurableSpawnerRoot game object in pedestrian controllers");
            }
            configurableSpawnerRoot.SetActive(true);
            agentManager = (Agents.PedestrianSpawner)Agents.BaseAgentManager.instance;
            agentManager.Restart();
        }

        public override Trajectory.TrackedGroup[] groups
        {
            get
            {
                if (agentManager == null)
                {
                    return new Trajectory.TrackedGroup[0];
                }
                return agentManager.groups.ToArray();
            }
        }

        public override Trajectory.TrackedAgent[] agents
        {
            get
            {
                if (agentManager == null)
                {
                    return new Trajectory.TrackedAgent[0];
                }
                return agentManager.agents.ToArray();
            }
        }
    }
}
