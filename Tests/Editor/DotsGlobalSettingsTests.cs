using System.IO;
using System.Linq;
using Unity.NetCode.Hybrid;
using UnityEditor;
using NUnit.Framework;
using Unity.Entities.Build;
using Unity.Entities.Conversion;
using Unity.NetCode.PrespawnTests;
using Unity.NetCode.Tests;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.Scenes.Editor.Tests
{
    public class DotsGlobalSettingsTests : TestWithSceneAsset
    {
        [Test]
        public void SuccessfulClientBuildTest()
        {
            // Temporary hack to work around issue where headless no-graphics CI pass would spit out
            // `RenderTexture.Create with shadow sampling failed` error, causing this test to fail.
            // Feature has been requested to NOT log this error when running headless.
            LogAssert.ignoreFailingMessages = true;

            var dotsSettings = DotsGlobalSettings.Instance;
            var originalPlayerType = dotsSettings.GetPlayerType();
            var originalNetCodeClientTarget = NetCodeClientSettings.instance.ClientTarget;
            try
            {
                bool isOSXEditor = Application.platform == RuntimePlatform.OSXEditor;

                var buildOptions = new BuildPlayerOptions();
                buildOptions.subtarget = 0;
                buildOptions.target = EditorUserBuildSettings.activeBuildTarget;

                var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
                SubSceneHelper.CreateSubScene(scene, Path.GetDirectoryName(scene.path), "Sub0", 5, 5, null, Vector3.zero);

                buildOptions.scenes = new string[] {scene.path};
                var uniqueTempPath = FileUtil.GetUniqueTempPathInProject();
                buildOptions.locationPathName = uniqueTempPath + "/Test.exe";

                if(isOSXEditor)
                    buildOptions.locationPathName = uniqueTempPath + "/Test.app";
                buildOptions.extraScriptingDefines = new string[] {"UNITY_CLIENT"};

                NetCodeClientSettings.instance.ClientTarget = NetCodeClientTarget.Client;

                var report = BuildPipeline.BuildPlayer(buildOptions);

                EnsureResourceCatalogHasBeenDeployed(uniqueTempPath, isOSXEditor, report);
            }
            finally
            {
                NetCodeClientSettings.instance.ClientTarget = originalNetCodeClientTarget;
            }
        }

        [Test]
        public void SuccessfulClientAndServerBuildTest()
        {
            // Temporary hack to work around issue where headless no-graphics CI pass would spit out
            // `RenderTexture.Create with shadow sampling failed` error, causing this test to fail.
            // Feature has been requested to NOT log this error when running headless.
            LogAssert.ignoreFailingMessages = true;

            var dotsSettings = DotsGlobalSettings.Instance;
            var originalPlayerType = dotsSettings.GetPlayerType();
            var originalNetCodeClientTarget = NetCodeClientSettings.instance.ClientTarget;
            try
            {
                bool isOSXEditor = Application.platform == RuntimePlatform.OSXEditor;

                var buildOptions = new BuildPlayerOptions();
                buildOptions.subtarget = 0;
                buildOptions.target = EditorUserBuildSettings.activeBuildTarget;

                var scene = SubSceneHelper.CreateEmptyScene(ScenePath, "Parent");
                SubSceneHelper.CreateSubScene(scene, Path.GetDirectoryName(scene.path), "Sub0", 5, 5, null, Vector3.zero);
                buildOptions.scenes = new string[] {scene.path};
                var uniqueTempPath = FileUtil.GetUniqueTempPathInProject();
                buildOptions.locationPathName = uniqueTempPath + "/Test.exe";

                if(isOSXEditor)
                    buildOptions.locationPathName = uniqueTempPath + "/Test.app";

                NetCodeClientSettings.instance.ClientTarget = NetCodeClientTarget.ClientAndServer;

                var report = BuildPipeline.BuildPlayer(buildOptions);

                EnsureResourceCatalogHasBeenDeployed(uniqueTempPath, isOSXEditor, report);
            }
            finally
            {
                NetCodeClientSettings.instance.ClientTarget = originalNetCodeClientTarget;
            }
        }


        static void EnsureResourceCatalogHasBeenDeployed(string uniqueTempPath, bool isOSXEditor, BuildReport report)
        {
            var locationPath = Application.dataPath + "/../" + uniqueTempPath;
            var streamingAssetPath = locationPath + "/Test_Data/StreamingAssets/";
            if(isOSXEditor)
                streamingAssetPath = locationPath  + $"/Test.app/Contents/Resources/Data/StreamingAssets/";

            // REDO: Just check the resource catalog has been deployed
            var sceneInfoFileRelativePath = EntityScenesPaths.FullPathForFile(streamingAssetPath, EntityScenesPaths.RelativePathForSceneInfoFile);
            var resourceCatalogFileExists = File.Exists(sceneInfoFileRelativePath);
            var reportMessages = string.Join('\n', report.steps.SelectMany(x => x.messages).Select(x => $"[{x.type}] {x.content}"));
            var stringReport = $"[{report.summary.result}, {report.summary.totalErrors} totalErrors, {report.summary.totalWarnings} totalWarnings, resourceCatalogFileExists: {resourceCatalogFileExists}]\nBuild logs ----------\n{reportMessages} ------ ";
            Assert.AreEqual(BuildResult.Succeeded, report.summary.result, $"Expected build success! Report: {stringReport}");
            Assert.IsTrue(resourceCatalogFileExists, $"Expected '{sceneInfoFileRelativePath}' file to exist! Report: {stringReport}");
        }
    }
}
