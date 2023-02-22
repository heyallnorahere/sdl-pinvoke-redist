using Newtonsoft.Json;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SDLPackageBuilder.Commands
{
    internal struct PackageSourceSettings
    {
        public string? Url { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    internal sealed class PackageBuilderLogger : ILogger
    {
        public void LogDebug(string message) => Log(LogLevel.Debug, message);
        public void LogVerbose(string message) => Log(LogLevel.Verbose, message);
        public void LogInformation(string message) => Log(LogLevel.Information, message);
        public void LogMinimal(string message) => Log(LogLevel.Minimal, message);
        public void LogWarning(string message) => Log(LogLevel.Warning, message);
        public void LogError(string message) => Log(LogLevel.Error, message);
        public void LogInformationSummary(string message)
        {
            // what?
            Console.WriteLine($"NuGet: InformationSummary: {message}");
        }

        public void Log(LogLevel level, string message)
        {
            Console.WriteLine(GetLogMessage(level, message));
        }

        public async Task LogAsync(LogLevel level, string message)
        {
            await Console.Out.WriteLineAsync(GetLogMessage(level, message));
        }

        public void Log(ILogMessage message) => Log(message.Level, message.Message);
        public async Task LogAsync(ILogMessage message) => await LogAsync(message.Level, message.Message);

        private static string GetLogMessage(LogLevel level, string message) => $"NuGet: {level}: {message}";
    }

    [RegisteredCommand]
    internal sealed class PublishPackage : ICommand
    {
        private static async Task<int> ExtractArtifactsAsync(string artifactsDir, string outputDir)
        {
            var buffer = new byte[256];
            var artifacts = Directory.GetFiles(artifactsDir, "artifact-*.zip");

            foreach (var artifact in artifacts)
            {
                using var archive = ZipFile.Open(artifact, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    var entryPath = entry.FullName;

                    var directoryPath = Path.GetDirectoryName(entryPath);
                    if (directoryPath != null)
                    {
                        Directory.CreateDirectory(Path.Join(outputDir, directoryPath));
                    }

                    string newPath = Path.Join(outputDir, entryPath);
                    using var outputStream = new FileStream(newPath, FileMode.Create);
                    using var sourceStream = entry.Open();

                    while (true)
                    {
                        int bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                        {
                            break;
                        }

                        await outputStream.WriteAsync(buffer, 0, bytesRead);
                    }
                }
            }

            return artifacts.Length;
        }

        private static async Task PushPackageAsync(string packagePath, string source)
        {
            var sourceConfig = JsonConvert.DeserializeObject<PackageSourceSettings>(source, Program.JsonSettings);
            
            var nullMembers = string.Empty;
            var properties = sourceConfig.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // i have zero fucks left to give at this point
            foreach (var property in properties)
            {
                var value = property.GetValue(sourceConfig);
                if (value is null)
                {
                    if (nullMembers.Length > 0)
                    {
                        nullMembers += " ";
                    }

                    nullMembers += property.Name;
                }
            }

            if (nullMembers.Length > 0)
            {
                throw new ArgumentException($"Null package source config members: {nullMembers}");
            }

            var packageSource = new PackageSource(sourceConfig.Url!)
            {
                Credentials = new PackageSourceCredential(
                    source: sourceConfig.Url!,
                    username: sourceConfig.Username!,
                    passwordText: sourceConfig.Password!,
                    isPasswordClearText: true,
                    validAuthenticationTypesText: null
                )
            };

            var repository = Repository.CreateSource(Repository.Provider.GetCoreV3(), packageSource);
            var resource = await repository.GetResourceAsync<PackageUpdateResource>();

            await resource.Push(
                packagePaths: new string[] { packagePath },
                symbolSource: null,
                timeoutInSecond: 5 * 60, // 5 minutes
                disableBuffering: false,
                getApiKey: uri => packageSource.Credentials.Password,
                getSymbolApiKey: uri => null,
                noServiceEndpoint: false,
                skipDuplicate: false,
                symbolPackageUpdateResource: null,
                log: new PackageBuilderLogger()
            );
        }

        public async Task<int> InvokeAsync(string[] args)
        {
            if (args.Length < 1)
            {
                throw new ArgumentException("No package source provided!");
            }

            string artifactDir = Program.ArtifactsDirectory;
            string packageDir = Path.Join(artifactDir, "package");

            if (Directory.Exists(packageDir))
            {
                Directory.Delete(packageDir, true);
            }

            Directory.CreateDirectory(packageDir);
            int artifactCount = await ExtractArtifactsAsync(artifactDir, packageDir);

            if (artifactCount <= 0)
            {
                throw new FileNotFoundException("No artifacts to consolidate!");
            }

            Version version;
            string versionFilePath = Path.Join(packageDir, Program.VersionFileName);
            using (var versionStream = new FileStream(versionFilePath, FileMode.Open))
            {
                using var reader = new StreamReader(versionStream, encoding: Encoding.UTF8, leaveOpen: true);
                string content = await reader.ReadToEndAsync();

                version = Version.Parse(content.Trim());
            }

            Console.WriteLine("Building package...");
            var builder = new PackageBuilder
            {
                Id = "SDLPInvokeRedist",
                Version = new NuGetVersion(version),
                Description = "SDL2 redistributables for P/Invoke",
                Readme = "README.md",
                Repository = new RepositoryMetadata
                {
                    Type = "git",
                    Url = "https://github.com/yodasoda1219/sdl-pinvoke-redist"
                },
            };

            builder.Authors.Add("Nora Beda");
            builder.AddFiles(packageDir, "runtimes/**", "runtimes");
            builder.AddFiles(Environment.CurrentDirectory, "README.md", string.Empty);

            string packageName = $"{builder.Id}.{version}.nupkg";
            string packagePath = Path.Join(artifactDir, packageName);

            using (var outputStream = new FileStream(packagePath, FileMode.Create))
            {
                builder.Save(outputStream);
            }

            Console.WriteLine("Package built! Pushing package...");
            await PushPackageAsync(packagePath, args[0]);
            Console.WriteLine("Successfully pushed package!");

            return 0;
        }
    }
}