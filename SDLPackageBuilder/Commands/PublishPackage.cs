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

using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SDLPackageBuilder.Commands
{
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

            var assembly = Assembly.GetExecutingAssembly();
            using (var readmeStream = assembly.GetManifestResourceStream("SDLPackageBuilder.README.md"))
            {
                if (readmeStream is null)
                {
                    throw new FileNotFoundException("Unable to find README in manifest!");
                }

                string outputReadmePath = Path.Join(packageDir, "README.md");
                using var outputReadmeStream = new FileStream(outputReadmePath, FileMode.Create);

                var buffer = new byte[256];
                while (true)
                {
                    int bytesRead = await readmeStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    await outputReadmeStream.WriteAsync(buffer, 0, bytesRead);
                }
            }

            builder.Authors.Add("Nora Beda");
            builder.AddFiles(packageDir, "runtimes/**", "runtimes");
            builder.AddFiles(packageDir, "README.md", string.Empty);

            string packageName = $"{builder.Id}.{version}.nupkg";
            string packagePath = Path.Join(artifactDir, packageName);

            using (var outputStream = new FileStream(packagePath, FileMode.Create))
            {
                builder.Save(outputStream);
            }

            Console.WriteLine("Package built! Pushing package...");
            if (await Program.RunCommandAsync($"dotnet nuget push \"{packagePath}\" -s \"{args[0]}\"") != 0)
            {
                return 1;
            }

            Console.WriteLine("Successfully pushed package!");
            return 0;
        }
    }
}