using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Scoundrel.Editor
{
    public static class WebGLItchBuilder
    {
        private const string MainScenePath = "Assets/Scenes/DungeonTabletop.unity";
        private const string DefaultOutputPath = "Build/WebGL";
        private const string DefaultItchZipPath = "Build/Scoundrel-WebGL-itch.zip";

        [MenuItem("Scoundrel/Apply WebGL itch.io Settings")]
        public static void ApplyItchWebSettings()
        {
            var target = NamedBuildTarget.WebGL;

            // Log baseline before applying
            Debug.Log(
                "[WebGLItchBuilder] Baseline WebGL settings:\n" +
                $"  compressionFormat = {PlayerSettings.WebGL.compressionFormat}\n" +
                $"  decompressionFallback = {PlayerSettings.WebGL.decompressionFallback}\n" +
                $"  dataCaching = {PlayerSettings.WebGL.dataCaching}\n" +
                $"  exceptionSupport = {PlayerSettings.WebGL.exceptionSupport}\n" +
                $"  debugSymbolMode = {PlayerSettings.WebGL.debugSymbolMode}\n" +
                $"  managedStrippingLevel = {PlayerSettings.GetManagedStrippingLevel(target)}\n" +
                $"  il2cppCodeGeneration = {PlayerSettings.GetIl2CppCodeGeneration(target)}\n" +
                $"  stripEngineCode = {PlayerSettings.stripEngineCode}");

            // itch.io-safe delivery: CompressionFormat.Disabled ensures clean, uncompressed .wasm & .data serving
            // without requiring CDN Content-Encoding headers or risk of ERR_CONNECTION_RESET on .unityweb files.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;

            // Keep explicitly thrown exceptions enabled so SolanaScoundrelBridge try/catch blocks work in WebAssembly
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;

            // Optimize IL2CPP size while keeping Minimal managed stripping so Newtonsoft.Json / Solana.Unity-SDK DTOs are preserved
            PlayerSettings.SetIl2CppCodeGeneration(target, Il2CppCodeGeneration.OptimizeSize);
            PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Minimal);
            PlayerSettings.stripEngineCode = true;
            PlayerSettings.stripUnusedMeshComponents = true;

            // Ensure background execution stays active while browser wallet popups (Phantom/Solflare) are focused
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;

            var clipboardImporter = AssetImporter.GetAtPath("Assets/Plugins/WebGL/ScoundrelClipboard.jslib") as PluginImporter;
            if (clipboardImporter != null && !clipboardImporter.GetCompatibleWithPlatform(BuildTarget.WebGL))
            {
                clipboardImporter.SetCompatibleWithAnyPlatform(false);
                clipboardImporter.SetCompatibleWithPlatform(BuildTarget.WebGL, true);
                clipboardImporter.SaveAndReimport();
            }

            AssetDatabase.SaveAssets();

            Debug.Log(
                "[WebGLItchBuilder] Applied & saved itch.io WebGL settings:\n" +
                $"  compressionFormat = {PlayerSettings.WebGL.compressionFormat}\n" +
                $"  decompressionFallback = {PlayerSettings.WebGL.decompressionFallback}\n" +
                $"  dataCaching = {PlayerSettings.WebGL.dataCaching}\n" +
                $"  exceptionSupport = {PlayerSettings.WebGL.exceptionSupport}\n" +
                $"  debugSymbolMode = {PlayerSettings.WebGL.debugSymbolMode}\n" +
                $"  managedStrippingLevel = {PlayerSettings.GetManagedStrippingLevel(target)}\n" +
                $"  il2cppCodeGeneration = {PlayerSettings.GetIl2CppCodeGeneration(target)}\n" +
                $"  stripEngineCode = {PlayerSettings.stripEngineCode}");
        }

        [MenuItem("Scoundrel/Build WebGL for itch.io")]
        public static void PerformBuild()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                Debug.Log("[WebGLItchBuilder] Switching active build target to WebGL...");
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
            }

            ApplyItchWebSettings();

            string outputPath = ResolveOutputPath();
            Directory.CreateDirectory(outputPath);

            var buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = new[] { MainScenePath },
                locationPathName = outputPath,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };

            Debug.Log($"[WebGLItchBuilder] Starting WebGL build -> {Path.GetFullPath(outputPath)}");
            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            BuildSummary summary = report.summary;

            Debug.Log(
                $"[WebGLItchBuilder] WebGL build finished: result={summary.result}, " +
                $"totalSize={summary.totalSize} bytes ({summary.totalSize / (1024f * 1024f):F2} MB), " +
                $"totalTime={summary.totalTime.TotalSeconds:F1}s, errors={summary.totalErrors}, warnings={summary.totalWarnings}");

            if (summary.result != BuildResult.Succeeded)
            {
                throw new Exception($"[WebGLItchBuilder] WebGL build failed with result: {summary.result}");
            }

            PackageItchZip(outputPath, DefaultItchZipPath);
        }

        [MenuItem("Scoundrel/Package WebGL Zip for itch.io")]
        public static void PackageDefaultItchZip()
        {
            PackageItchZip(DefaultOutputPath, DefaultItchZipPath);
        }

        public static void PackageItchZip(string buildFolder, string zipOutputPath)
        {
            string fullBuildDir = Path.GetFullPath(buildFolder);
            string fullZipPath = Path.GetFullPath(zipOutputPath);

            if (File.Exists(fullZipPath))
            {
                File.Delete(fullZipPath);
            }

            using (FileStream zipStream = new FileStream(fullZipPath, FileMode.CreateNew, FileAccess.Write))
            using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (string filePath in Directory.GetFiles(fullBuildDir, "*", SearchOption.AllDirectories))
                {
                    // Must use POSIX forward slashes ('/') so Linux hosts (itch.io) extract subdirectories properly
                    string relativePath = filePath.Substring(fullBuildDir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');

                    archive.CreateEntryFromFile(filePath, relativePath, System.IO.Compression.CompressionLevel.Optimal);
                }
            }

            Debug.Log($"[WebGLItchBuilder] Packaged POSIX itch.io archive -> {fullZipPath}");
        }

        private static string ResolveOutputPath()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-buildOutput", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return DefaultOutputPath;
        }
    }
}
