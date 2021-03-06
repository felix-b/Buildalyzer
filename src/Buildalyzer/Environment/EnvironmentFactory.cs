﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Buildalyzer.Construction;
using Microsoft.Build.Utilities;

namespace Buildalyzer.Environment
{
    public class EnvironmentFactory
    {
        private readonly AnalyzerManager _manager;
        private readonly ProjectFile _projectFile;

        internal EnvironmentFactory(AnalyzerManager manager, ProjectFile projectFile)
        {
            _manager = manager;
            _projectFile = projectFile;
        }
        
        public BuildEnvironment GetBuildEnvironment() =>
            GetBuildEnvironment(null, null);

        public BuildEnvironment GetBuildEnvironment(string targetFramework) =>
            GetBuildEnvironment(targetFramework, null);

        public BuildEnvironment GetBuildEnvironment(EnvironmentOptions options) =>
            GetBuildEnvironment(null, options);
        
        public BuildEnvironment GetBuildEnvironment(string targetFramework, EnvironmentOptions options)
        {
            options = options ?? new EnvironmentOptions();

            // If this project file uses a target known to require .NET Framework, use the framework regardless of anything else
            if (_projectFile.RequiresNetFramework)
            {
                return CreateFrameworkEnvironment(options);
            }

            // If we're running on .NET Core, use the .NET Core SDK regardless of the project file
            if (BuildEnvironment.IsRunningOnCore)
            {
                return CreateCoreEnvironment(options);
            }           

            // If this is an SDK project, check the target framework
            if (_projectFile.UsesSdk)
            {
                // Use the Framework tools if this project ONLY targets .NET Framework ("net" followed by a digit)
                // (see https://docs.microsoft.com/en-us/dotnet/standard/frameworks)
                if (targetFramework == null
                    ? _projectFile.TargetFrameworks.All(x => IsFrameworkTargetFramework(x))
                    : IsFrameworkTargetFramework(targetFramework))
                {
                    return CreateFrameworkEnvironment(options);
                }

                // Otherwise use the .NET Core SDK
                return CreateCoreEnvironment(options);
            }

            // Use Framework tools if a ToolsVersion attribute
            if (_projectFile.ToolsVersion != null)
            {
                return CreateFrameworkEnvironment(options);
            }

            throw new Exception("Could not determine build environment");
        }

        // Based on code from OmniSharp
        // https://github.com/OmniSharp/omnisharp-roslyn/blob/78ccc8b4376c73da282a600ac6fb10fce8620b52/src/OmniSharp.Abstractions/Services/DotNetCliService.cs
        private BuildEnvironment CreateCoreEnvironment(EnvironmentOptions options)
        {
            // Clone the options global properties dictionary so we can add to it
            Dictionary<string, string> additionalGlobalProperties = options.GlobalProperties.ToDictionary(x => x.Key, x => x.Value);

            // Tweak targets
            List<string> targets = new List<string>(options.TargetsToBuild);
            if (targets.Contains("Restore", StringComparer.OrdinalIgnoreCase) && _projectFile.Virtual)
            {
                // NuGet.Targets can't handle virtual project files:
                // C:\Program Files\dotnet\sdk\2.1.300\NuGet.targets(239,5): error MSB3202: The project file "E:\Code\...\...csproj" was not found.
                targets.RemoveAll(x => x.Equals("Restore", StringComparison.OrdinalIgnoreCase));
            }
            if (targets.Contains("Clean", StringComparer.OrdinalIgnoreCase))
            {
                // Required to force CoreCompile target when it calculates everything is already built
                // This can happen if the file wasn't previously generated (Clean only cleans what was in that file)
                additionalGlobalProperties.Add(MsBuildProperties.NonExistentFile, @"__NonExistentSubDir__\__NonExistentFile__");
            }

            // Get paths
            string dotnetPath = DotnetPathResolver.ResolvePath(_projectFile.Path);
            string msBuildExePath = Path.Combine(dotnetPath, "MSBuild.dll");
            string extensionsPath = dotnetPath;
            string sdksPath = Path.Combine(dotnetPath, "Sdks");
            string roslynTargetsPath = Path.Combine(dotnetPath, "Roslyn");
            if(!BuildEnvironment.IsRunningOnCore)
            {
                // If the host is .NET Framework, we need to try and set the MSBuild path to the framework if we can
                // See https://github.com/Microsoft/msbuild/issues/3510#issuecomment-404671740
                if (GetFrameworkMsBuildExePath(out string frameworkMsBuildExePath))
                {
                    msBuildExePath = frameworkMsBuildExePath;

                    // Also need to set the Roslyn and Extensions paths to match
                    string toolsPath = Path.GetDirectoryName(msBuildExePath);
                    roslynTargetsPath = Path.Combine(toolsPath, "Roslyn");
                    extensionsPath = Path.GetFullPath(Path.Combine(toolsPath, @"..\..\"));
                }
            }

            // Required to find and import the Restore target
            additionalGlobalProperties.Add(MsBuildProperties.NuGetRestoreTargets, $@"{ dotnetPath }\NuGet.targets");

            return new BuildEnvironment(
                options.DesignTime,
                targets.ToArray(),
                msBuildExePath,
                extensionsPath,
                sdksPath,
                roslynTargetsPath,
                additionalGlobalProperties,
                options.EnvironmentVariables);
        }

        private BuildEnvironment CreateFrameworkEnvironment(EnvironmentOptions options)
        {
            // Clone the options global properties dictionary so we can add to it
            Dictionary<string, string> additionalGlobalProperties = options.GlobalProperties.ToDictionary(x => x.Key, x => x.Value);

            // Tweak targets
            List<string> targets = new List<string>(options.TargetsToBuild);
            if (targets.Contains("Restore", StringComparer.OrdinalIgnoreCase) && _projectFile.Virtual)
            {
                // NuGet.Targets can't handle virtual project files:
                // C:\Program Files\dotnet\sdk\2.1.300\NuGet.targets(239,5): error MSB3202: The project file "E:\Code\...\...csproj" was not found.

                targets.RemoveAll(x => x.Equals("Restore", StringComparison.OrdinalIgnoreCase));
            }
            if (targets.Contains("Clean", StringComparer.OrdinalIgnoreCase))
            {
                // Required to force CoreCompile target when it calculates everything is already built
                // This can happen if the file wasn't previously generated (Clean only cleans what was in that file)
                additionalGlobalProperties.Add(MsBuildProperties.NonExistentFile, @"__NonExistentSubDir__\__NonExistentFile__");
            }

            // Get paths
            if (!GetFrameworkMsBuildExePath(out string msBuildExePath))
            {
                throw new InvalidOperationException("Could not locate the tools (msbuild.exe) path");
            }
            string toolsPath = Path.GetDirectoryName(msBuildExePath);
            string extensionsPath = Path.GetFullPath(Path.Combine(toolsPath, @"..\..\"));
            string sdksPath = Path.Combine(_projectFile.UsesSdk ? DotnetPathResolver.ResolvePath(_projectFile.Path) : extensionsPath, "Sdks");
            string roslynTargetsPath = Path.Combine(toolsPath, "Roslyn");
            
            // Need to set directories for default code analysis rulset (see https://github.com/dotnet/roslyn/issues/6774)
            string vsRoot = Path.Combine(extensionsPath, @"..\");
            additionalGlobalProperties.Add(MsBuildProperties.CodeAnalysisRuleDirectories, Path.GetFullPath(Path.Combine(vsRoot, @"Team Tools\Static Analysis Tools\FxCop\\Rules")));
            additionalGlobalProperties.Add(MsBuildProperties.CodeAnalysisRuleSetDirectories, Path.GetFullPath(Path.Combine(vsRoot, @"Team Tools\Static Analysis Tools\\Rule Sets")));

            // This is required to trigger NuGet package resolution and regeneration of project.assets.json
            additionalGlobalProperties.Add(MsBuildProperties.ResolveNuGetPackages, "true");

            // Required to find and import the Restore target
            additionalGlobalProperties.Add(MsBuildProperties.NuGetRestoreTargets, $@"{ toolsPath }\..\..\..\Common7\IDE\CommonExtensions\Microsoft\NuGet\NuGet.targets");

            return new BuildEnvironment(
                options.DesignTime,
                targets.ToArray(),
                msBuildExePath,
                extensionsPath,
                sdksPath,
                roslynTargetsPath,
                additionalGlobalProperties,
                options.EnvironmentVariables);
        }

        private bool IsFrameworkTargetFramework(string targetFramework) =>
            targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                && targetFramework.Length > 3
                && char.IsDigit(targetFramework[4]);

        private bool GetFrameworkMsBuildExePath(out string msBuildExePath)
        {
            msBuildExePath = ToolLocationHelper.GetPathToBuildToolsFile("msbuild.exe", ToolLocationHelper.CurrentToolsVersion);
            if (string.IsNullOrEmpty(msBuildExePath))
            {
                // Could not find the tools path, possibly due to https://github.com/Microsoft/msbuild/issues/2369
                // Try to poll for it. From https://github.com/KirillOsenkov/MSBuildStructuredLog/blob/4649f55f900a324421bad5a714a2584926a02138/src/StructuredLogViewer/MSBuildLocator.cs
                string programFilesX86 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
                msBuildExePath = new[]
                {
                    Path.Combine(programFilesX86, @"Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\Bin\MSBuild.exe"),
                    Path.Combine(programFilesX86, @"Microsoft Visual Studio\2017\Professional\MSBuild\15.0\Bin\MSBuild.exe"),
                    Path.Combine(programFilesX86, @"Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe")
                }
                .Where(File.Exists)
                .FirstOrDefault();
            }
            if (string.IsNullOrEmpty(msBuildExePath))
            {
                return false;
            }
            return true;
        }
    }
}