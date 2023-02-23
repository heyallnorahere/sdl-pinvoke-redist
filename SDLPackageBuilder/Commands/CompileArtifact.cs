/*
   Copyright 2023 Nora Beda

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SDLPackageBuilder.Commands
{
    internal struct PlatformPackageSpec
    {
        public string? UpdateCommand { get; set; }
        public string? InstallCommand { get; set; }
        public string[]? Packages { get; set; }
    }

    [RegisteredCommand]
    internal sealed class CompileArtifact : ICommand
    {
        private static readonly IReadOnlyDictionary<string, string> sCMakeOptions;
        static CompileArtifact()
        {
            sCMakeOptions = new Dictionary<string, string>
            {
                ["SDL_STATIC"] = "OFF",
                ["SDL_SHARED"] = "ON",
                ["SDL_TEST"] = "OFF"
            };
        }

        private static string GetSharedLibraryName(string name)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return $"{name}.dll";
            }
            else
            {
                return $"lib{name}.{(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "dylib" : "so")}";
            }
        }

        private static string RuntimeIdentifier
        {
            get
            {
                // i understand it's explicitly stated that you shouldnt do this
                // but honey i couldnt get anything better
                string platform;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    platform = "win";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    platform = "osx";
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    platform = "linux";
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }

                var arch = RuntimeInformation.OSArchitecture.ToString();
                return $"{platform}-{arch.ToLower()}";
            }
        }

        private static async Task<bool> InstallPlatformDependenciesAsync()
        {
            const string specFileId = "SDLPackageBuilder.Resources.dependencies.json";

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(specFileId);

            if (stream is null)
            {
                throw new IOException("Failed to open dependencies specification file!");
            }

            using var reader = new StreamReader(stream, encoding: Encoding.UTF8, leaveOpen: true);
            var json = await reader.ReadToEndAsync();

            var dependencies = JsonConvert.DeserializeObject<Dictionary<string, PlatformPackageSpec>>(json, Program.JsonSettings);
            if (dependencies is null)
            {
                throw new ArgumentException("Malformed JSON!");
            }

            string? platformId = null;
            foreach (var id in dependencies.Keys)
            {
                var platform = OSPlatform.Create(id);
                if (RuntimeInformation.IsOSPlatform(platform))
                {
                    platformId = id;
                    break;
                }
            }

            var runtimeId = RuntimeIdentifier;
            if (platformId is null)
            {
                Console.WriteLine($"No packages to install for platform {runtimeId}");
                return true;
            }

            Console.WriteLine($"Installing dependencies for platform \"{runtimeId}\"");
            var spec = dependencies[platformId];

            if (spec.Packages == null || spec.Packages.Length == 0)
            {
                throw new ArgumentException("No packages to install!");
            }

            if (string.IsNullOrEmpty(spec.InstallCommand))
            {
                throw new ArgumentException("No install command was provided!");
            }

            if (!string.IsNullOrEmpty(spec.UpdateCommand))
            {
                if (await Program.RunCommandAsync(spec.UpdateCommand, dryRun: Debugger.IsAttached) != 0)
                {
                    return false;
                }
            }

            string installCommand = spec.InstallCommand;
            foreach (var packageId in spec.Packages)
            {
                installCommand += $" {packageId}";
            }

            return await Program.RunCommandAsync(installCommand, dryRun: Debugger.IsAttached) == 0;
        }

        private static async Task<Version?> BuildArtifactAsync(string sourceDir, string buildDir, string config)
        {
            if (!Directory.Exists(sourceDir))
            {
                throw new DirectoryNotFoundException($"{sourceDir} does not exist!");
            }

            string cmakeCommand = $"cmake {sourceDir} -B {buildDir}";
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // makefiles arent multiconfig
                cmakeCommand += " -G \"Ninja Multi-Config\"";
            }

            foreach (var key in sCMakeOptions.Keys)
            {
                var value = sCMakeOptions[key];
                cmakeCommand += $" -D{key}={value}";
            }

            Version? version = null;
            int configureResult = await Program.RunCommandAsync(cmakeCommand, onLine: line =>
            {
                if (version != null)
                {
                    return;
                }

                const string sdlVersionDeclaration = "Revision: SDL-";
                int index = line.IndexOf(sdlVersionDeclaration);

                if (index < 0)
                {
                    return;
                }

                const string releasePrefix = "release-";
                string versionString = line[(index + sdlVersionDeclaration.Length)..];

                if (versionString.StartsWith(releasePrefix))
                {
                    versionString = versionString[releasePrefix.Length..];
                }

                index = versionString.IndexOf('-');
                if (index >= 0)
                {
                    versionString = versionString[0..index];
                }

                version = Version.Parse(versionString);
            });

            if (configureResult != 0)
            {
                return null;
            }

            if (version is null)
            {
                throw new ArgumentException("Failed to find a version from CMake output!");
            }

            string buildCommand = $"cmake --build {buildDir} --config {config}";
            if (await Program.RunCommandAsync(buildCommand) != 0)
            {
                return null;
            }

            return version;
        }

        public async Task<int> InvokeAsync(string[] args)
        {
            if (!await InstallPlatformDependenciesAsync())
            {
                return 1;
            }

            var cwd = Environment.CurrentDirectory;
            var sourceDir = Path.Join(cwd, "SDL");
            var artifactsDir = Program.ArtifactsDirectory;
            var buildDir = Path.Join(artifactsDir, "build");
            const string config = "Release";

            var version = await BuildArtifactAsync(sourceDir, buildDir, config);
            if (version is null)
            {
                return 1;
            }

            string rid = RuntimeIdentifier;
            const string baseLibraryName = "SDL2";

            string platformBaseLibraryName = baseLibraryName;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                platformBaseLibraryName += "-2.0";
            }

            string libraryName = GetSharedLibraryName(platformBaseLibraryName);
            string libraryPath = Path.Join(buildDir, config, libraryName);

            string zipPath = Path.Join(artifactsDir, $"artifact-{rid}.zip");
            if (Path.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry(Program.VersionFileName);
            using (var versionStream = entry.Open())
            {
                using var writer = new StreamWriter(versionStream, encoding: Encoding.UTF8, leaveOpen: true);
                await writer.WriteAsync(version.ToString());
            }

            entry = archive.CreateEntry($"runtimes/{rid}/{GetSharedLibraryName(baseLibraryName)}");
            using (var libraryStream = entry.Open())
            {
                using var input = new FileStream(libraryPath, FileMode.Open);

                var buffer = new byte[256];
                while (true)
                {
                    int countRead = await input.ReadAsync(buffer, 0, buffer.Length);
                    if (countRead <= 0)
                    {
                        break;
                    }

                    await libraryStream.WriteAsync(buffer, 0, countRead);
                }
            }

            Console.WriteLine($"Successfully built SDL v{version} artifact!");
            return 0;
        }
    }
}