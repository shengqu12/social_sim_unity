using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace SEAN.Scenario.Agents
{
    public abstract class Base : IVI.INavigable
    {
        //PARAMETERS
        public const float RADIUS = 0.2f;
        protected const float ROBOT_RADIUS = 0.2f;
        protected const float MASS = 80;
        protected const float PERCEPTION_RADIUS = 2;
        protected const float ANGULAR_SPEED = 120;
        protected const float ANIMATION_SMOOTHING = 0.6f;

        //private CapsuleCollider collisionCapsule;
        private Animator animator;
        private IVelocityModulator modulator;

        //NAVIGATION
        NavMeshPath nmPath;
        protected NavMeshAgent nma;
        protected Rigidbody rb;
        protected CapsuleCollider collisionCapsule;

        //ANIMATION
        public Vector3 velocity { get; protected set; }
        private float animationScale = 1.0f;
        private float idleSpeed = 0.5f;
        private bool applyRootMotion = true;

        // Set in Start() once the locomotion Animator is resolved. False when that Animator
        // lives on a nested child (e.g. White_Cane_User's Male_Adult_12) rather than this
        // GameObject -- Unity only dispatches OnAnimatorMove() to scripts co-located with the
        // Animator, so in that case neither this class nor PedestrianModulator ever receives
        // it. LateUpdate() below uses this to explicitly apply root motion only when Unity's
        // own dispatch can't reach us, and to otherwise stay out of the way entirely.
        private bool animatorOnRoot = true;

        // True for avatars whose Animator produces no usable root motion at all -- e.g. a
        // wheelchair looping a static seated-idle clip, where deltaPosition is always zero and
        // root-motion-driven translation can never move the agent. When true, Move() bypasses
        // animation-driven translation/animation-parameter feeding entirely and applies the
        // social-force `velocity` straight to the transform instead, while the Animator keeps
        // playing its own clip for posture only (see Move()). Defaults false so every existing
        // root-motion-driven agent is unaffected. Set via AppearanceAvatar.directVelocityDrive
        // (see DirectVelocityDrive below) -- not intended to be hand-authored per prefab.
        [SerializeField] private bool directVelocityDrive = false;

        public bool DirectVelocityDrive
        {
            get => directVelocityDrive;
            set => directVelocityDrive = value;
        }

        #region Unity Functions

        protected override void Start()
        {
            nmPath = new NavMeshPath();
            // Having a disabled navmesh agent allows it to move
            nma = gameObject.AddComponent<NavMeshAgent>();
            nma.radius = RADIUS;
            nma.enabled = false;

            rb = gameObject.GetComponent<Rigidbody>();
            if (!rb)
            {
                rb = gameObject.AddComponent<Rigidbody>();
            }
            rb.mass = MASS;
            rb.constraints = RigidbodyConstraints.FreezeRotation;

            var agentMeshBounds = GetComponentInChildren<SkinnedMeshRenderer>().bounds;
            var agentHeight = agentMeshBounds.extents.y * 2;
            collisionCapsule = gameObject.GetComponent<CapsuleCollider>();
            if (collisionCapsule == null)
            {
                collisionCapsule = gameObject.AddComponent<CapsuleCollider>();
            }
            collisionCapsule.radius = RADIUS;
            collisionCapsule.height = agentHeight;
            collisionCapsule.center = Vector3.up * agentHeight / 2f;

            // Some character packages (e.g. White_Cane_User) put the Animator on a nested
            // child instead of the root -- use the shared self/children/parent lookup so both
            // layouts work (see AppearanceAvatar.cs / AvatarAnimatorUtility.cs).
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator != null)
            {
                animator.applyRootMotion = applyRootMotion;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Nested case: keep applyRootMotion enabled above (so deltaPosition/deltaRotation
                // keep getting computed) but stop Unity from auto-applying that delta to the
                // child's own transform, which would slide the mesh out from under the agent
                // root -- LateUpdate() below applies it to the root explicitly instead. A no-op
                // OnAnimatorMove() on the Animator's own GameObject is what switches Unity from
                // "auto-apply" to "only apply if something reads deltaPosition/deltaRotation".
                animatorOnRoot = animator.gameObject == gameObject;
                if (!animatorOnRoot && animator.gameObject.GetComponent<RootMotionSink>() == null)
                {
                    animator.gameObject.AddComponent<RootMotionSink>();
                }
            }
            else
            {
                Debug.LogWarning($"[Base] No Animator found anywhere under '{name}' -- "
                    + $"spawning unanimated so navigation/TrackedTrajectory setup still runs.", this);
            }
            // Cached once here instead of doing GetComponent<IVelocityModulator>() every
            // frame in ModulateVelocity() -- agents without a modulator (the common case)
            // pay this cost exactly once.
            modulator = GetComponent<IVelocityModulator>();
            base.Start();
        }

        void Update()
        {
            velocity = ModulateVelocity(UpdateVelocity());
            //print(name + " velocity: " + velocity);
            Move();

            //if (path.Count > 1 && Util.Geometry.GroundPlaneDist(transform.position, path[0]) < Parameters.MIN_DIST)
            //{
            //    path.RemoveAt(0);
            //}
            //else if (path.Count == 1 && Util.Geometry.GroundPlaneDist(transform.position, path[0]) < Parameters.MIN_DIST)
            //{
            //    path.RemoveAt(0);

            //    StopAnimator();
            //}
        }

        // Only does anything when the resolved Animator is on a nested child (animatorOnRoot
        // false, see Start()). When the Animator is on this GameObject, Unity's own
        // OnAnimatorMove() dispatch (handled by PedestrianModulator when present, or Unity's
        // default auto-apply when there's no modulator at all) already applies root motion --
        // adding another application here would double it, so this is a no-op in that case.
        void LateUpdate()
        {
            // directVelocityDrive agents take translation ONLY from Move()'s
            // velocity * Time.deltaTime application -- applying the nested Animator's
            // root motion delta here on top of that would double-drive the root (see
            // cyclist: rider clip has un-baked XZ root motion, rocketed ahead of social force).
            if (animator == null || animatorOnRoot || directVelocityDrive) { return; }

            var pedestrianModulator = modulator as PedestrianModulator;
            if (pedestrianModulator != null)
            {
                // Reuse the exact same logic Unity would have called via OnAnimatorMove() if
                // the Animator were on this GameObject, including the frozen-Surprised
                // facing-only override -- see PedestrianModulator.ApplyAnimatorRootMotion().
                pedestrianModulator.ApplyAnimatorRootMotion();
            }
            else
            {
                // No modulator (Indifferent personality never gets one, see ModulateVelocity())
                // -- reproduce Unity's default root motion application, which the nested
                // Animator's own GameObject can no longer do now that RootMotionSink is on it.
                transform.position += animator.deltaPosition;
                transform.rotation *= animator.deltaRotation;
            }
        }

        // No-op OnAnimatorMove() sink added (see Start()) to the resolved Animator's own
        // GameObject only when that Animator is on a nested child. Implementing OnAnimatorMove()
        // anywhere on a GameObject tells Unity to stop auto-applying that GameObject's root
        // motion to its own transform and call this instead -- since this is intentionally
        // empty, the child's transform simply stops moving on its own, leaving
        // animator.deltaPosition/deltaRotation available for Base.LateUpdate() to read and
        // apply to the agent root instead.
        private class RootMotionSink : MonoBehaviour
        {
            void OnAnimatorMove() { }
        }

        #endregion

        protected override bool PlanNavigation()
        {
            //print(name + " StartNavigation");
            if (destPos[0] == Mathf.Infinity || destPos[1] == Mathf.Infinity || destPos[2] == Mathf.Infinity)
            {
                print(name + " goal set to infinity");
                return false;
            }
            ComputePath(destPos);
            return true;
        }

        protected override void StopNavigation()
        {
            if (nmPath != null) { nmPath.ClearCorners(); }
            destPos = Vector3.zero;
            StopAnimator();
        }

        protected override void StartGroup(IVI.GroupNavNode group)
        {
            group.AddMember(this);
        }

        protected override void StopGroup(IVI.GroupNavNode group)
        {
            group.RemoveMember(this);
        }

        #region Public Functions

        public void StopAnimator()
        {
            //animator.SetBool("Idling", true);
            animator.SetFloat("Forward", 0);
            animator.SetFloat("Strafe", 0);
        }

        public void ComputePath(Vector3 destination)
        {
            destPos = destination;
            if (nmPath == null) { return; }
            NavMesh.CalculatePath(transform.position, destPos, NavMesh.AllAreas, nmPath);
            //if (!nma) { return; }
            //nma.enabled = true;
            //var nmPath = new NavMeshPath();
            //if (nma.isOnNavMesh)
            //{
            //    //print("nma.CalculatePath(" + destination + "," + nmPath + ");");
            //    if (!nma.CalculatePath(destination, nmPath))
            //    {
            //        print("No path found for " + name + " to " + destination);
            //    }
            //    path = nmPath.corners.ToList();
            //    //print(name + " path count is " + path.Count);
            //}
            //else
            //{
            //    print(name + " is not on Navmesh");
            //}
            //print(name + " ComputePath " + destination);
            //nma.enabled = false;
        }

        #endregion

        #region Abstract Functions

        protected abstract Vector3 UpdateVelocity();

        #endregion

        /// <summary>
        /// Hook for an optional IVelocityModulator component (e.g. PedestrianModulator)
        /// on this same GameObject to adjust the social-force velocity before it drives
        /// rotation/animation in Move(). Agents without such a component (SFAgent/ORCA.Agent/
        /// Playback.Agent by default) get modulator == null and this is a no-op passthrough.
        /// </summary>
        protected virtual Vector3 ModulateVelocity(Vector3 socialForceVelocity)
        {
            return modulator != null ? modulator.Modulate(socialForceVelocity, this) : socialForceVelocity;
        }

        public void TriggerAnimation(string triggerName)
        {
            if (animator != null)
            {
                animator.SetTrigger(triggerName);
            }
        }

        #region Private Functions

        protected Vector3 nearestGoalPoint
        {
            get
            {
                // Skip points too close
                foreach (Vector3 position in nmPath.corners)
                {
                    if (Util.Geometry.GroundPlaneDist(transform.position, position) > Parameters.NEXT_NAV_MIN_DIST)
                    {
                        return position;
                    }
                }
                return destPos;
            }
        }
        private void Move()
        {
            float angle = 0;
            // compute angular velocity from next goal position
            if (!(GetType().Equals(typeof(Scenario.Agents.Playback.Agent)) || GetType().Equals(typeof(PlayerAgent))))
            {
                // $$$ FIX: can't move to 0,0,0
                if (destPos == Vector3.zero)
                {
                    //print(name + " destPos is zero");
                    return;
                }
                if (modulator == null || !modulator.IsRotationSuppressed())
                {
                    Vector3 goalDir = nearestGoalPoint - transform.position;
                    float goalWeight = 0.5f;
                    goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;
                    goalDir.y = 0;
                    angle = -Vector3.SignedAngle(goalDir, transform.forward, Vector3.up);
                }
            }
            else
            {
                if (GetType().Equals(typeof(PlayerAgent)))
                {
                    angle = velocity.y * ANGULAR_SPEED * Time.deltaTime;
                }
                // read the angular velocity from the velocity field
                // note: this line is nearly identical to the following lines
                // but i didn't want to  change it in case it's specific to
                // SF code
                else
                {
                    angle = -Vector3.SignedAngle(velocity, transform.forward, Vector3.up);
                }
            }
            //Angular Velocity and rotation
            if (Mathf.Abs(angle) > ANGULAR_SPEED * Time.deltaTime)
            {
                angle = Mathf.Sign(angle) * ANGULAR_SPEED * Time.deltaTime;
            }
            //angle = Mathf.Sign(angle) * Mathf.Min(ANGULAR_SPEED, Mathf.Abs(angle)) * Time.deltaTime;
            transform.RotateAround(transform.position, Vector3.up, angle);

            if (directVelocityDrive)
            {
                // No usable root motion from this avatar's Animator (e.g. a wheelchair's
                // looping seated-idle has zero deltaPosition every frame) -- drive the
                // transform directly from the social-force velocity instead. Deliberately
                // skips the whole animation-parameter block below rather than relying on it
                // being harmless: animator.speed is a real playback-rate knob (not a named
                // parameter like Forward/Strafe/Idling), and setting it to velocity.magnitude
                // would freeze this avatar's idle loop at 0 speed whenever it's stationary.
                transform.position += velocity * Time.deltaTime;
            }
            else
            {
                // Motion
                Vector3 animParams = Quaternion.Euler(0, -transform.eulerAngles.y, 0) * velocity;
                animParams *= animationScale;
                var idle = animParams.magnitude < idleSpeed && !applyRootMotion;

                animator.SetBool("Idling", idle);
                if (!GetType().Equals(typeof(PlayerAgent)))
                {
                    animator.speed = velocity.magnitude;

                }
                animator.SetFloat("Forward", animParams.z/ANIMATION_SMOOTHING);
                animator.SetFloat("Strafe", animParams.x/ANIMATION_SMOOTHING);
            }

            if (ShowDebug)
            {
        
                if (velocity.y < 1 && velocity.y > -1){
                    Debug.DrawLine(transform.position, transform.position + velocity, Color.red);
                }
                else{
                    Debug.DrawLine(transform.position, transform.position + velocity, Color.yellow);
                }
            }

        }

        protected override void OnDrawGizmosSelected()
        {
            if (!ShowDebug) { return; }
            Gizmos.color = Color.black;
            Vector3 lastPos = transform.position;
            foreach (Vector3 position in nmPath.corners)
            {
                if (Util.Geometry.GroundPlaneDist(transform.position, position) > Parameters.NEXT_NAV_MIN_DIST)
                {
                    Debug.DrawLine(lastPos, position);
                    Gizmos.DrawCube(position, new Vector3(0.15f, 0.15f, 0.15f));
                    lastPos = position;
                }
            }
            //Debug.DrawLine(transform.position, destPos);
            //Gizmos.DrawCube(destPos, new Vector3(0.25f, 0.25f, 0.25f));
            base.OnDrawGizmosSelected();
        }
        #endregion
    }
}
