using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// -executeMethod target for batchmode trial runs:
    ///   Unity -batchmode -executeMethod SEAN.AutoTrial.AutoTrialEditorRunner.EnterPlay -trialConfig <path>
    /// Deliberately never paired with -quit on the Unity command line -- AutoTrialBootstrap/
    /// TrialController own the exit (EditorApplication.Exit(0/1)) once the trial finishes or a
    /// setup step fails, so the editor process itself is what ends the batchmode invocation.
    /// </summary>
    public static class AutoTrialEditorRunner
    {
        // Confirmed via recon (2026-07-15) as the active development scene: most recently
        // modified, contains ConfigurableSpawnerRoot, matches the exported nav map. A fresh
        // batchmode Editor session does not reliably reopen whichever scene was last open in the
        // GUI -- observed opening a stale Temp/__Backupscenes/*.backup instead (no SEAN singleton
        // in it), which made AutoTrialBootstrap time out waiting for SEAN.instance. Opening the
        // scene explicitly here removes that nondeterminism without touching any scene file.
        private const string TargetScenePath = "Assets/Scenes/SEAN/Outdoor.unity";

        public static void EnterPlay()
        {
            Scene active = EditorSceneManager.GetActiveScene();
            if (active.path != TargetScenePath)
            {
                Debug.Log("[AutoTrial] active scene is '" + active.path + "', opening " + TargetScenePath + " instead.");
                EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            }
            EditorApplication.isPlaying = true;
        }
    }
}
