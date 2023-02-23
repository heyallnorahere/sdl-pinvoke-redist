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
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SDLPackageBuilder
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class RegisteredCommandAttribute : Attribute
    {
        // nothing
    }

    internal interface ICommand
    {
        Task<int> InvokeAsync(string[] args);
    }

    public static class Program
    {
        public const string VersionFileName = "version.txt";

        public static string ArtifactsDirectory => Path.Join(Environment.CurrentDirectory, "artifacts");
        public static JsonSerializerSettings JsonSettings { get; }

        static Program()
        {
            JsonSettings = new JsonSerializerSettings
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
        }

        private static IReadOnlyDictionary<string, ConstructorInfo> FindRegisteredCommands()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var types = assembly.GetTypes();

            var constructors = new Dictionary<string, ConstructorInfo>();
            foreach (var type in types)
            {
                var attribute = type.GetCustomAttribute<RegisteredCommandAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                var interfaces = type.GetInterfaces();
                if (!interfaces.Contains(typeof(ICommand)))
                {
                    continue;
                }

                var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public, Array.Empty<Type>());
                if (constructor is null)
                {
                    continue;
                }

                constructors.Add(type.Name, constructor);
            }

            return constructors;
        }
        
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("No command specified!");
            }

            var constructors = FindRegisteredCommands();
            string commandId = args[0];
            if (!constructors.ContainsKey(commandId))
            {
                throw new ArgumentException($"Invalid command: {commandId}");
            }

            var instance = (ICommand)constructors[commandId].Invoke(null);
            return await instance.InvokeAsync(args[1..]);
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

        public static async Task<int> RunCommandAsync(string command, string? cwd = null, Action<string>? onLine = null, bool dryRun = false)
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
    }
}