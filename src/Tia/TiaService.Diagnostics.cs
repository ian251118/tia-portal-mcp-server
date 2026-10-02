using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Siemens.Engineering.HmiUnified.UI.Widgets;

namespace TiaMcpServer
{
    // Reflection over the Openness assemblies, for discovering API shapes that are
    // not documented well enough to code against blind. Read-only by construction.
    internal sealed partial class TiaService
    {
        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        public object DebugSearchTypes(string pattern, string assemblyHint)
        {
            IEnumerable<Assembly> assemblies = new[] { typeof(HmiButton).Assembly };
            if (!string.IsNullOrEmpty(assemblyHint))
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name.IndexOf(assemblyHint, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var matches = assemblies.SelectMany(SafeTypes)
                .Where(t => t.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => new { name = t.Name, fullName = t.FullName, assembly = t.Assembly.GetName().Name })
                .OrderBy(t => t.fullName)
                .ToArray();

            return new { pattern, matchCount = matches.Length, matches };
        }

        public object DebugReflectType(string typeName)
        {
            // Make sure the Openness assembly is loaded even before the first connect.
            var anchor = typeof(HmiButton).Assembly;

            var type = AppDomain.CurrentDomain.GetAssemblies()
                .OrderByDescending(a => a == anchor)
                .SelectMany(SafeTypes)
                .FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);

            if (type == null)
                throw new InvalidOperationException(
                    $"Type '{typeName}' not found in any loaded assembly. Use tia_debug_search_types to find the exact name.");

            if (type.IsEnum)
            {
                return new
                {
                    typeName = type.Name,
                    typeFullName = type.FullName,
                    isEnum = true,
                    values = Enum.GetNames(type)
                        .Select(n => new { name = n, value = Convert.ToInt64(Convert.ChangeType(Enum.Parse(type, n), Enum.GetUnderlyingType(type))) })
                        .ToArray()
                };
            }

            var properties = type.GetProperties()
                .Select(p => new { p.Name, type = p.PropertyType.FullName, canRead = p.CanRead, canWrite = p.CanWrite })
                .OrderBy(p => p.Name)
                .ToArray();

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(m => new
                {
                    m.Name,
                    isStatic = m.IsStatic,
                    isGeneric = m.IsGenericMethodDefinition,
                    returnType = m.ReturnType.FullName,
                    parameters = m.GetParameters().Select(p => p.ParameterType.FullName).ToArray()
                })
                .OrderBy(m => m.Name)
                .ToArray();

            return new
            {
                typeName = type.Name,
                typeFullName = type.FullName,
                baseType = type.BaseType?.FullName,
                isEnum = false,
                properties,
                methods
            };
        }
    }
}
