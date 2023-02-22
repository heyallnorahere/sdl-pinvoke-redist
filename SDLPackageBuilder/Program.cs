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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    }
}