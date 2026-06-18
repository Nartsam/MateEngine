using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    [MenuItem("Build/Build Windows x64")]
    public static void BuildWindows()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity" },
            locationPathName = "Build/MateEngine.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }
}
