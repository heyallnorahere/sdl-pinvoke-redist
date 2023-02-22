using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        private static readonly JsonSerializerSettings sJsonSettings;
        private static readonly IReadOnlyDictionary<string, string> sCMakeOptions;
        static CompileArtifact()
        {
            sJsonSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy
                    {
                        OverrideSpecifiedNames = false
                    }
                }
            };

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

        private static async Task RelayTextAsync(TextReader input, params Action<string>[] callbacks)
        {
            while (true)
            {
                var line = await input.ReadLineAsync();
                if (line is null)
                {
                    return;
                }

                foreach (var callback in callbacks)
                {
                    callback.Invoke(line);
                }
            }
        }

        private static async Task<int> RunCommandAsync(string command, string? cwd = null, Action<string>? onLine = null, bool dryRun = false)
        {
            Console.WriteLine($">{command}");
            if (dryRun)
            {
                return 0;
            }

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "cmd.exe" : "/bin/bash",
                    UseShellExecute = false,
                    WorkingDirectory = cwd ?? Environment.CurrentDirectory,
                    RedirectStandardOutput = onLine != null,
                    RedirectStandardError = onLine != null
                }
            };

            process.StartInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            process.StartInfo.ArgumentList.Add(command);
            process.Start();

            var tasks = new List<Task>();
            if (onLine != null)
            {
                var readers = new TextReader[]
                {
                    process.StandardOutput,
                    process.StandardError
                };

                foreach (var reader in readers)
                {
                    tasks.Add(Task.Run(async () => await RelayTextAsync(reader, Console.WriteLine, onLine)));
                }
            }

            tasks.Add(process.WaitForExitAsync());
            await Task.WhenAll(tasks);

            return process.ExitCode;
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

            var dependencies = JsonConvert.DeserializeObject<Dictionary<string, PlatformPackageSpec>>(json, sJsonSettings);
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
                if (await RunCommandAsync(spec.UpdateCommand, dryRun: Debugger.IsAttached) != 0)
                {
                    return false;
                }
            }

            string installCommand = spec.InstallCommand;
            foreach (var packageId in spec.Packages)
            {
                installCommand += $" {packageId}";
            }

            return await RunCommandAsync(installCommand, dryRun: Debugger.IsAttached) == 0;
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
            int configureResult = await RunCommandAsync(cmakeCommand, onLine: line =>
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
            if (await RunCommandAsync(buildCommand) != 0)
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
            var artifactsDir = Path.Join(cwd, "artifacts");
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
            var entry = archive.CreateEntry("version.txt");
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