using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Shopkeeper.EditorTools
{
    public static class BuildTools
    {
        const string Output = "Builds/Windows/ShopkeeperDemo.exe";

        /// <summary>CI entry point: regenerate the scene, then build the player.</summary>
        public static void BuildAllFromCommandLine()
        {
            DemoSceneBuilder.Build();
            BuildWindows();
        }

        [MenuItem("Shopkeeper/Build Windows Player", priority = 20)]
        public static void BuildWindows()
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/ShopDemo.unity" },
                locationPathName = Output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[Shopkeeper] Build {report.summary.result}: {Output} ({report.summary.totalSize / (1024 * 1024)} MB)");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
