using System;
using System.IO;
using Mono.Cecil;

namespace ScamWYF.Modding.Tools
{
    /// <summary>
    /// Refuses to pass a dll whose [BepInPlugin] version BepInEx cannot parse.
    /// </summary>
    /// <remarks>
    /// BepInEx 5's BepInPlugin constructor does `new System.Version(versionString)` in a try/catch, and
    /// Chainloader skips any type whose attribute Version came back null:
    ///
    ///   [Warning: BepInEx] Skipping type [ScamWYF.ModHandler.ModHandlerPlugin] because its version is invalid.
    ///   [Info   : BepInEx] 0 plugins to load
    ///
    /// System.Version takes two to four dot-separated integers and nothing else. A SemVer string with
    /// build metadata - "1.2.3+commitsha" - is valid SemVer, correct for the assembly's
    /// AssemblyInformationalVersion, and fatal here. Nothing fails at build time and nothing fails at
    /// install time; the whole symptom is one line in BepInEx\LogOutput.log, which is a file people do
    /// not have open.
    ///
    /// So this runs as part of every build rather than as something a release checklist remembers. It
    /// reads the built dll with the same Cecil Switchboard uses, and exits non-zero on a version the CLR
    /// constructor will reject.
    ///
    ///     .\tools\test-bepinex-version.ps1 bin\ScamWYF.ModHandler.dll
    /// </remarks>
    internal static class BepInExVersionCheck
    {
        internal static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: test-bepinex-version.ps1 <dll> [<dll> ...]");
                return 2;
            }

            var failed = 0;
            var checkedCount = 0;

            foreach (var path in args)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine("  FAIL  not found: " + path);
                    failed++;
                    continue;
                }

                Console.WriteLine("  " + Path.GetFileName(path));

                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    var informational = Informational(assembly);

                    var found = false;
                    foreach (var type in AllTypes(assembly.MainModule.Types))
                    {
                        foreach (var attribute in type.CustomAttributes)
                        {
                            if (attribute.AttributeType.FullName != "BepInEx.BepInPlugin") continue;

                            found = true;
                            checkedCount++;

                            var declared = attribute.ConstructorArguments.Count >= 3
                                ? attribute.ConstructorArguments[2].Value as string
                                : null;

                            var name = attribute.ConstructorArguments.Count >= 2
                                ? attribute.ConstructorArguments[1].Value as string
                                : "(unnamed)";

                            if (Accepts(declared))
                            {
                                Console.WriteLine("    PASS  [BepInPlugin] \"" + name + "\" version '" +
                                                  declared + "' is parseable by System.Version");
                                Console.WriteLine("          assembly informational version: " + informational);
                            }
                            else
                            {
                                failed++;
                                Console.WriteLine("    FAIL  [BepInPlugin] \"" + name + "\" version '" +
                                                  (declared ?? "(null)") + "'");
                                Console.WriteLine("          BepInEx constructs new System.Version(that string) and");
                                Console.WriteLine("          will throw, leaving the attribute null, and Chainloader will");
                                Console.WriteLine("          skip this plugin. It needs two to four dot-separated");
                                Console.WriteLine("          integers - no '+', no '-', no 'v', not a single number.");
                                Console.WriteLine("          The commit belongs in AssemblyInformationalVersion (" +
                                                  informational + "), not here.");
                            }
                        }
                    }

                    if (!found)
                    {
                        // Correct for the shared library, which must not be loaded as a plugin.
                        Console.WriteLine("    --    no [BepInPlugin]; correct for the shared library");
                    }
                }
            }

            Console.WriteLine();

            if (checkedCount == 0)
            {
                Console.WriteLine("no [BepInPlugin] attributes were checked");
                return failed == 0 ? 0 : 1;
            }

            if (failed == 0)
            {
                Console.WriteLine(checkedCount + " plugin attribute(s) checked, all loadable by BepInEx");
                return 0;
            }

            Console.WriteLine(failed + " problem(s). BepInEx would load nothing.");
            return 1;
        }

        /// <summary>
        /// BepInEx's own rule, applied by doing what BepInEx does rather than by a table of allowed forms.
        /// </summary>
        private static bool Accepts(string declared)
        {
            if (string.IsNullOrEmpty(declared)) return false;

            try
            {
                return new Version(declared) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Informational(AssemblyDefinition assembly)
        {
            foreach (var attribute in assembly.CustomAttributes)
            {
                if (attribute.AttributeType.FullName != "System.Reflection.AssemblyInformationalVersionAttribute")
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Count < 1) return "(none)";

                var value = attribute.ConstructorArguments[0].Value as string;
                return string.IsNullOrEmpty(value) ? "(none)" : value;
            }

            return "(none)";
        }

        private static System.Collections.Generic.IEnumerable<TypeDefinition> AllTypes(
            System.Collections.Generic.IEnumerable<TypeDefinition> roots)
        {
            foreach (var type in roots)
            {
                foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
                yield return type;
            }
        }
    }
}