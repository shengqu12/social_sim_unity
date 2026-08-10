using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SEAN.AutoTrial.EditorTools
{
    /// <summary>
    /// Session 68 §0 -- the checks that gate everything else: import "Crouch To Stand.fbx" as a
    /// Humanoid rig into its OWN directory, build a controller for it, then smoke-test the retarget
    /// onto male_adult_01 before a single line of state-machine logic gets written.
    ///
    /// Why a separate directory and a separate importer from S41MixamoImport: that script scans
    /// Assets/PedestrianAssets/Mixamo/*.fbx and hard-fails on any file that is in neither its
    /// Moving nor its Stationary set. Dropping a tenth FBX in there would either break it or force
    /// an edit to a shared, already-validated import path that nine shipped clips depend on. A
    /// sibling directory is additive and touches none of it. Same reasoning for the controller: it
    /// is a NEW asset in a new tree, not an edit to a shared one (§2.1).
    ///
    /// Root-motion policy: STATIONARY, matching S41MixamoImport's stationary handling exactly
    /// (lockRootPositionXZ / lockRootHeightY / lockRootRotation all true). A crouch is an action
    /// performed in place -- the hip drop belongs in the skeleton, not in a root translation that
    /// would slide the character across the ground while it squats.
    ///
    /// loopTime is FALSE, which is where this departs from every clip S41 imported. Those are
    /// ambient behaviours that have to fill a 90 s trial; this is a one-shot transition whose
    /// endpoints are load-bearing -- the state machine keys CROUCH_ENTER -> CROUCH_HOLD on the clip
    /// reaching its end, and a looping clip never reaches one.
    ///
    /// The actual pose check does NOT happen here. Two edit-mode routes were tried and both posed
    /// nothing on this avatar -- see S68CrouchSmokeRunner, which runs it in play mode instead. This
    /// method ends by handing off to that runner.
    ///
    ///     --exec-editor-method SEAN.AutoTrial.EditorTools.S68CrouchImport.Apply
    /// </summary>
    public static class S68CrouchImport
    {
        public const string Dir = "Assets/PedestrianAssets/S68Crouch";

        // S68-A: Sheng re-downloaded "Crouch To Stand" after judging the first one's motion wrong.
        // Same filename at the source, so it is imported under a DISTINCT asset name and the
        // original is left in place -- an A/B comparison and a one-line revert both stay available
        // (point this constant back at the v1 path). They are genuinely different files:
        // 470000 bytes / md5 7c1683f0... against 528496 / md5 8971be62...
        // S68-C: "Kneeling Down" -- a different Mixamo action family, chosen because the right-foot
        // roll is a defect BOTH "Crouch To Stand" downloads shared, so another clip from that family
        // would very likely inherit it.
        //
        // Direction is reversed relative to its predecessors: this clip runs stand -> kneel, so the
        // descent is its FORWARD half and the stand-up is the reversed one (see S68CuriousCrouch's
        // seek mapping). Nothing else about the playback machinery changes.
        public const string FbxPath = Dir + "/Kneeling Down.fbx";
        public const string FbxPathCrouchToStandV2 = Dir + "/Crouch To Stand v2.fbx";
        public const string FbxPathV1 = Dir + "/Crouch To Stand.fbx";

        /// <summary>
        /// S68-B §1.3 tier C. The project's own crouch, referenced READ-ONLY -- nothing under
        /// Assets/IVI is imported, re-imported, or edited here; only the AnimationClip sub-asset is
        /// pointed at from a controller that lives in this session's own directory.
        ///
        /// Chosen after tier A was ruled out by measurement rather than by a failed attempt: the
        /// Mixamo FBX's own Avatar already maps RightFoot -> mixamorig4:RightFoot and
        /// RightToes -> mixamorig4:RightToeBase, symmetrically with the left, so there is no
        /// mismapping to correct. The reversal survives into BOTH downloads, while the same avatar
        /// renders correct feet under its own controller and under the shipped Old_Man_Walk clip --
        /// so it is specific to these crouch clips and not fixable by remapping them.
        ///
        /// This clip is a full idle -> crouch -> idle ROUND TRIP on a Rocketbox-family skeleton
        /// (Hips/LeftUpLeg/LeftFoot/LeftToes, copyAvatar from the shared avatar), which is why §1.3
        /// expected near-zero retarget risk. It also removes the reversal question entirely: the
        /// descent is the clip's own first half played forward, so nothing is ever played backwards.
        /// </summary>
        public const string IviCrouchPath = Dir + "/IviCrouch_copy.fbx";
        /// <summary>Where the copy came from. Byte-identical (md5 773cc67e...), copied not edited --
        /// the IVI original is untouched, which is what keeps this on the allowed side of §1.4.
        /// A COPY rather than a direct reference because the clip needs in-place root settings
        /// (lockRootPositionXZ/HeightY/Rotation), and those live in the FBX's import settings --
        /// i.e. inside the IVI directory, which may not be modified. Owning a copy is the only way
        /// to set them without touching IVI.</summary>
        public const string IviCrouchOrigin =
            "Assets/IVI/Animations/Locomotion Pack/Interacting/Idle2Crouch_Neutral2Crouch2Idle.fbx";

        /// <summary>Tier C is live. Set false to fall back to the Mixamo clip above.</summary>
        public const bool UseIviCrouch = false;   // S68-B: tier C abandoned -- the IVI clip contains no grounded crouch (see report)
        public const string ResourcesDir = Dir + "/Resources";
        public const string ControllerPath = ResourcesDir + "/S68_CuriousCrouch.controller";
        // Kept in step with S68CuriousCrouch's own constants -- these names are the contract between
        // the generated asset and the code that Play()s into it.
        public const string StatePose = "S68CrouchPose";
        private const string AvatarPrefab = "Assets/Resources/Prefabs/Rocketbox/Male_Adult_01.prefab";

        [MenuItem("AutoTrial/Session 68/Import + smoke-test crouch clip")]
        public static void Apply()
        {
            var sb = new StringBuilder();

            AnimationClip clip;
            if (UseIviCrouch)
            {
                clip = LoadIviClip(sb);
            }
            else
            {
                clip = ConfigureImport(sb);
            }
            if (clip == null) { Debug.Log(sb.ToString()); EditorApplication.Exit(1); return; }

            if (BuildCrouchController(clip, sb) == null)
            {
                Debug.Log(sb.ToString());
                EditorApplication.Exit(1);
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefab) == null)
            {
                sb.AppendLine("[S68Crouch] avatar prefab not found: " + AvatarPrefab);
                Debug.Log(sb.ToString());
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log(sb.ToString());

            // An EMPTY scene of our own, never the shared Outdoor.unity: the question is whether one
            // clip retargets onto one avatar, and a real scene would only add ROS, navigation and
            // spawner failure modes that have nothing to do with it. Created in memory and never
            // saved, so no scene file is touched.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var host = new GameObject("S68CrouchSmokeRunner");
            // Unqualified: `SEAN.AutoTrial...` does not resolve from in here, because `SEAN` binds to
            // the SEAN singleton CLASS before it binds to the namespace. The enclosing namespace is
            // in scope anyway.
            var runner = host.AddComponent<S68CrouchSmokeRunner>();
            runner.controllerResource = Path.GetFileNameWithoutExtension(ControllerPath);
            // One state now, seeked explicitly -- see BuildCrouchController.
            runner.stateName = StatePose;
            runner.enterState = StatePose;
            runner.exitState = StatePose;
            // S68-A §3: also dump the enter -> hold -> exit sequence, since POV cannot show it.
            runner.renderSequence = true;
            // S68-B §1.1: gives the runner the clip's own source rig, so it can tell a retarget
            // defect apart from a defect that is already in the clip.
            runner.sourceFbxPath = FbxPath;

            // No EditorApplication.Exit here -- the runner owns the exit, exactly as
            // AutoTrialBootstrap/TrialController own it for a real trial.
            EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// Tier C: pull the AnimationClip out of the IVI FBX WITHOUT touching it.
        ///
        /// No ModelImporter is fetched and no SaveAndReimport is called here, deliberately -- that
        /// is the difference between referencing an IVI asset (allowed) and modifying one (red
        /// line). Whatever import settings IVI ships with are the ones this uses.
        /// </summary>
        private static AnimationClip LoadIviClip(StringBuilder sb)
        {
            var importer = AssetImporter.GetAtPath(IviCrouchPath) as ModelImporter;
            if (importer == null)
            {
                sb.AppendLine("[S68Crouch] no ModelImporter for " + IviCrouchPath);
                return null;
            }
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            var cfg = importer.defaultClipAnimations;
            if (cfg != null && cfg.Length > 0)
            {
                for (int i = 0; i < cfg.Length; i++)
                {
                    cfg[i].loopTime = false;
                    // In place, same policy as every stationary clip this project imports: a crouch
                    // is performed on the spot and its vertical belongs in the pose, not in a root
                    // track that would lift the character off the floor (which is exactly what the
                    // read-only reference did -- the feet floated at the crouch).
                    cfg[i].lockRootPositionXZ = true;
                    cfg[i].lockRootHeightY = true;
                    cfg[i].lockRootRotation = true;
                }
                importer.clipAnimations = cfg;
            }
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            AssetDatabase.Refresh();

            var clip = AssetDatabase.LoadAllAssetsAtPath(IviCrouchPath).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null)
            {
                sb.AppendLine("[S68Crouch] no AnimationClip in " + IviCrouchPath);
                return null;
            }
            var fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(IviCrouchPath);
            var srcAnim2 = fbxGo != null ? fbxGo.GetComponentInChildren<Animator>(true) : null;
            bool ih = srcAnim2 != null && srcAnim2.avatar != null && srcAnim2.avatar.isHuman;
            sb.AppendFormat("[S68Crouch] copy imported Humanoid: isHuman={0}\n", ih);
            if (!ih) { sb.AppendLine("[S68Crouch] copy did not import as Humanoid -- stopping"); return null; }
            Vector3 avg = clip.averageSpeed; avg.y = 0f;
            sb.AppendFormat("[S68Crouch] TIER C -- own copy of {0}\n           -> {1}\n", IviCrouchOrigin, IviCrouchPath);
            sb.AppendFormat("[S68Crouch] CLIP name='{0}' len={1:F3}s fps={2:F0} loop={3} "
                + "rootCurves={4} motionCurves={5} averageSpeedXZ={6:F4} m/s averageSpeedY={7:F4}\n",
                clip.name, clip.length, clip.frameRate, clip.isLooping,
                clip.hasRootCurves, clip.hasMotionCurves, avg.magnitude, clip.averageSpeed.y);
            // The scaler's domain test still has to hold, and this clip was never checked against it.
            sb.AppendFormat("[S68Crouch] scaler classification: authored XZ speed {0:F4} m/s -> {1}\n",
                avg.magnitude,
                avg.magnitude < 0.05f ? "non_locomotion, animator.speed held at 1.0 (as required)"
                                      : "TRANSLATES -- the scaler will scale it; check for drift");
            return clip;
        }

        /// <summary>Import settings + the §0.1 questions the ticket asked to confirm, measured.
        /// Returns the clip, or null if the rig gate failed.</summary>
        private static AnimationClip ConfigureImport(StringBuilder sb)
        {
            if (!File.Exists(FbxPath))
            {
                sb.AppendLine("[S68Crouch] FBX not found at " + FbxPath);
                return null;
            }

            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
            {
                sb.AppendLine("[S68Crouch] no ModelImporter for " + FbxPath);
                return null;
            }

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            importer.useFileScale = true;
            importer.globalScale = 1.0f;

            var clips = importer.defaultClipAnimations;
            if (clips != null && clips.Length > 0)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    clips[i].loopTime = false;
                    clips[i].lockRootPositionXZ = true;
                    clips[i].lockRootHeightY = true;
                    clips[i].lockRootRotation = true;
                }
                importer.clipAnimations = clips;
            }
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            AssetDatabase.Refresh();

            var fbxGo = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var srcAnim = fbxGo != null ? fbxGo.GetComponentInChildren<Animator>(true) : null;
            bool isHuman = srcAnim != null && srcAnim.avatar != null && srcAnim.avatar.isHuman;
            bool avatarValid = srcAnim != null && srcAnim.avatar != null && srcAnim.avatar.isValid;
            sb.AppendFormat("[S68Crouch] RIG isHuman={0} avatarValid={1} -> {2}\n",
                isHuman, avatarValid, (isHuman && avatarValid) ? "PASS" : "FAIL");
            if (!isHuman || !avatarValid) { return null; }

            var clip = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null)
            {
                sb.AppendLine("[S68Crouch] no AnimationClip sub-asset in " + FbxPath);
                return null;
            }

            // averageSpeed is the number S32AnimatorSpeedScaler divides by -- or refuses to, via its
            // LocomotionAuthoredSpeedMps domain test. Measuring it here is what decides whether this
            // clip needs any speed-classification plumbing at all (§2.3).
            Vector3 avg = clip.averageSpeed; avg.y = 0f;
            sb.AppendFormat("[S68Crouch] CLIP name='{0}' len={1:F3}s fps={2:F0} loop={3} "
                + "rootCurves={4} motionCurves={5} averageSpeedXZ={6:F4} m/s averageSpeedY={7:F4}\n",
                clip.name, clip.length, clip.frameRate, clip.isLooping,
                clip.hasRootCurves, clip.hasMotionCurves, avg.magnitude, clip.averageSpeed.y);
            return clip;
        }

        /// <summary>
        /// The crouch controller: three states over the SAME clip, which is §0.1's single-clip plan
        /// made concrete because the inventory came back with only "Crouch To Stand" and no
        /// Stand-To-Crouch or Crouching-Idle to pair it with.
        ///
        ///   S68CrouchEnter  speed -1   the clip reversed -- stand descending into the kneel
        ///   S68CrouchHold   speed  0   parked on frame 0, which IS the kneel pose
        ///   S68CrouchExit   speed +1   the clip as authored -- kneel rising to stand
        ///
        /// The speeds live on the STATES, not on animator.speed, and that is the whole point (§2.3).
        /// S32AnimatorSpeedScaler writes the global animator.speed every Update from
        /// |Base.velocity| / authored-clip-speed, so anything written there is gone by the next
        /// frame. State speed multiplies with it and the scaler cannot reach it.
        ///
        /// The scaler's contribution during these states is 1.0, and that is measured rather than
        /// assumed: this clip's averageSpeed is 0.0000 m/s (it is imported in-place), which is below
        /// S32AnimatorSpeedScaler.MinAuthoredSpeedMps, so the scaler takes its "not locomotion, hold
        /// the authored rate" branch and returns 1.0. State speed is therefore the only factor.
        ///
        /// No transitions are authored between the states. S68CuriousCrouch drives them with
        /// Animator.Play(), so the sequencing lives in one place that can log it (§1.3) instead of
        /// being split between code and a graph.
        /// </summary>
        private static AnimatorController BuildCrouchController(AnimationClip clip, StringBuilder sb)
        {
            if (!AssetDatabase.IsValidFolder(ResourcesDir))
            {
                AssetDatabase.CreateFolder(Dir, "Resources");
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            if (controller == null)
            {
                sb.AppendLine("[S68Crouch] could not create controller at " + ControllerPath);
                return null;
            }

            // Mirror the shared controller's parameter names. Base.cs writes Forward/Strafe every
            // frame and TriggerAnimation() fires Surprised/AssertiveGesture by name; those writes
            // keep arriving while this controller is swapped in, and a write to a parameter that
            // does not exist logs a warning per call. Declaring them makes the writes land
            // harmlessly instead of filling the log the demo's own transition trace has to be read
            // out of.
            controller.AddParameter("Forward", AnimatorControllerParameterType.Float);
            controller.AddParameter("Turn", AnimatorControllerParameterType.Float);
            controller.AddParameter("Strafe", AnimatorControllerParameterType.Float);
            controller.AddParameter("Idling", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
            controller.AddParameter("OnGround", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Jump", AnimatorControllerParameterType.Float);
            controller.AddParameter("JumpLeg", AnimatorControllerParameterType.Float);
            controller.AddParameter("Surprised", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("AssertiveGesture", AnimatorControllerParameterType.Trigger);

            // S68-A: ONE state, speed 0, seeked frame by frame from code -- replacing the previous
            // three states at speed -1 / 0 / +1.
            //
            // The -1 state did not work and the demo video is the proof: the pedestrian was fully
            // standing at t=15.20 s and fully kneeling at t=15.45 s, so the 2.567 s descent never
            // played at all -- it snapped. Cause: Animator.Play(state, layer, normalizedTime) maps
            // normalizedTime INVERTED on a negative-speed state, so BeginCrouch's Play(enter, 0, 1f)
            // landed on clip time 0, which IS the kneel, instead of on the standing end.
            //
            // Rather than invert the argument to compensate -- which would encode a quirk nothing
            // here can verify from the API contract -- playback direction now comes from code that
            // sets normalizedTime explicitly every frame against a state that never advances on its
            // own. Reverse is then just a descending parameter, using the same forward mapping the
            // pose probe and the smoke screenshots already confirm (normalizedTime 0 = kneel,
            // 1 = stand). It also still satisfies the original constraint that made state speed
            // necessary: nothing here touches the global animator.speed that S32AnimatorSpeedScaler
            // overwrites every frame.
            var sm = controller.layers[0].stateMachine;
            var pose = AddCrouchState(sm, StatePose, clip, 0.0f);
            sm.defaultState = pose;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            sb.AppendFormat("[S68Crouch] controller -> {0}\n", ControllerPath);
            sb.AppendFormat("[S68Crouch]   state: {0} speed={1:F1} (clip '{2}' {3:F3}s, default={4})\n",
                pose.name, pose.speed, clip.name, clip.length, sm.defaultState.name);

            // S68-A §1.2.3: read the asset back off disk and list what each state's motion ACTUALLY
            // resolves to. Writing three references and then reporting the variable you wrote from
            // is not a check -- it cannot catch a state that silently kept the old clip. Reloading
            // by path is what makes this a readback rather than an echo.
            AnimatorController reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (reloaded == null)
            {
                sb.AppendLine("[S68Crouch] READBACK FAILED: controller did not reload from disk");
                return null;
            }
            string expectedClipPath = AssetDatabase.GetAssetPath(clip);
            int wrong = 0, n = 0;
            foreach (var child in reloaded.layers[0].stateMachine.states)
            {
                n++;
                Motion m = child.state.motion;
                string mPath = m != null ? AssetDatabase.GetAssetPath(m) : "(null)";
                bool ok = m != null && mPath == expectedClipPath;
                if (!ok) { wrong++; }
                sb.AppendFormat("[S68Crouch]   READBACK state='{0}' speed={1:+0.0;-0.0;0.0} motion='{2}' from='{3}' -> {4}\n",
                    child.state.name, child.state.speed,
                    m != null ? m.name : "(null)", mPath, ok ? "OK" : "WRONG CLIP");
            }
            sb.AppendFormat("[S68Crouch]   READBACK {0}/{1} states point at '{2}'  -> {3}\n",
                n - wrong, n, expectedClipPath, wrong == 0 ? "PASS" : "FAIL");
            if (wrong > 0 || n != 1) { return null; }
            return controller;
        }

        private static AnimatorState AddCrouchState(AnimatorStateMachine sm, string name,
                                                    AnimationClip clip, float speed)
        {
            var state = sm.AddState(name);
            state.motion = clip;
            state.speed = speed;
            // Foot IK, matching S41MixamoControllerGen: these are retargeted humanoid clips on an
            // avatar with different proportions from the source rig, and foot placement error is the
            // generic consequence. It matters more here than there -- a kneel puts the body's weight
            // through a foot and a knee in a pose authored for a different skeleton.
            state.iKOnFeet = true;
            state.writeDefaultValues = true;
            return state;
        }
    }
}
