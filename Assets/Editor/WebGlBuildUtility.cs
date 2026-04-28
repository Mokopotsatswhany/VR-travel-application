using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class WebGlBuildUtility
{
    private const string OutputDirectory = "docs";

    public static void BuildForGitHubPages()
    {
        var scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new BuildFailedException("No enabled scenes were found in Build Settings.");
        }

        PrepareOutputDirectory();

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            target = BuildTarget.WebGL,
            locationPathName = OutputDirectory,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new BuildFailedException($"WebGL build failed with result: {report.summary.result}");
        }

        File.WriteAllText(Path.Combine(OutputDirectory, ".nojekyll"), string.Empty);
        Debug.Log($"WebGL build completed successfully in '{OutputDirectory}'.");
    }

    private static void PrepareOutputDirectory()
    {
        if (Directory.Exists(OutputDirectory))
        {
            FileUtil.DeleteFileOrDirectory(OutputDirectory);
            FileUtil.DeleteFileOrDirectory($"{OutputDirectory}.meta");
        }

        Directory.CreateDirectory(OutputDirectory);
    }
}
