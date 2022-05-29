using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ILLoader
{
    internal class Program
    {
        private static MethodBase CurrentMethodBase;
        private static Assembly Assembly;
        private static Dictionary<string, Type> TypeDict;

        private static void Main(string[] args)
        {
            string path = "test.exe", programName = "";
            if (args.Length >= 1 && args[0].Split(new[] { '/', '\\' }).Last().Contains(".exe"))
            {
                path = args[0];
                if (path != null && path.StartsWith("\"") && path[path.Length - 1] == '"')
                    path = path.Substring(1, path.Length - 2);
                args = args.Skip(1).ToArray();
            }
            else if (!Assembly.GetEntryAssembly().Location.StartsWith(Environment.CurrentDirectory)
                && !File.Exists(path))
            {
                return;
            }
            if (args.Length >= 1 && args[0].Contains(".") && (args[0].EndsWith("Program") || args[0].Contains('*')))
            {
                programName = args[0];
                args = args.Skip(1).ToArray();
            }
            var argsEnd = Array.FindIndex(args, (s) => s == "--");
            if (argsEnd >= 0)
                args = args.Skip(argsEnd + 1).ToArray();
            if (path == null || !File.Exists(path))
            {
                MessageBox.Show($"Error: `{path ?? "path (argv[1])"}` is not found");
                return;
            }
            Assembly = System.Reflection.Assembly.LoadFrom(path);

            HookSystemRuntimeTypeGetMethodBase();
            Memory.Hook(
                typeof(StackTrace).Module.GetType("System.Diagnostics.StackFrameHelper")
                    .GetMethod("GetMethodBase", BindingFlags.Instance | BindingFlags.Public),
                typeof(Program).GetMethod(nameof(Hook4), BindingFlags.Instance | BindingFlags.Public)
            );

            var module = Assembly.ManifestModule;
            MethodInfo main;
            if (programName.Length > 0)
            {
                var program = module.GetType(programName);
                if (program == null && (programName.Contains("*") || programName.Contains("?")))
                {
                    var pattern = "^" + Regex.Escape(programName).Replace("\\?", ".").Replace("\\*", ".*") + "$";
                    var re = new Regex(pattern);
                    program = module.FindTypes((type, o) => re.IsMatch(type.FullName), null).FirstOrDefault();
                }
                if (program == null)
                {
                    MessageBox.Show($"Error: The entry named `{program}` is not found");
                    return;
                }
                main = program.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (main == null)
                {
                    MessageBox.Show($"Error: The entry named `{program}` has no .Main");
                    return;
                }
            }
            else
            {
                main = Assembly.EntryPoint;
                if (main == null)
                {
                    MessageBox.Show($"Error: The assembly has no entry point");
                    return;
                }
            }
            var consoleType = AttachConsole(-1) ? 1 : AllocConsole() ? 2 : 0;
            if (consoleType == 2)
            {
                var stdHandle = GetStdHandle(-11);
                var safeFileHandle = new SafeFileHandle(stdHandle, true);
                var fileStream = new FileStream(safeFileHandle, FileAccess.Write);
                var standardOutput = new StreamWriter(fileStream) { AutoFlush = true };
                Console.SetOut(standardOutput);
                Console.SetError(Console.Out);
            }
            var err = ReplaceMethods(typeof(Replacer), out var succeed);
            if (err != 0)
                Console.Error.WriteLine("[error] Can not replace {0} method(s)", err);
            else
            {
                Console.Error.WriteLine("[info] Has replaced {0} method(s)", succeed);
                CurrentMethodBase = main;
                main.Invoke(null, main.GetParameters().Length == 1 ? new object[] { args } : new object[] { });
            }
            Console.Out.Flush();
            if (consoleType >= 2 || err != 0)
                Console.ReadKey(false);
            FreeConsole();
        }

        public static int ReplaceMethods(Type replacerClass, out int replacedCount)
        {
            int err = 0, count = 0;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in replacerClass.GetMethods(flags))
            {
                var attr = method.GetCustomAttributes(typeof(ReplaceMethodAttribute), false)
                    .FirstOrDefault() as ReplaceMethodAttribute;
                if (attr != null)
                {
                    if (ReplaceMethod(attr.Name, method))
                        count++;
                    else
                        err += 1;
                }
            }
            replacedCount = count;
            return err;
        }

        private static bool ReplaceMethod(string name, MethodInfo newMethod)
        {
            Type clsType = null;
            string fieldName;
            PrepareTypeInfo();
            try
            {
                var index = name.LastIndexOf('.');
                if (index > 0 && name[index - 1] == '.')
                    index--;
                var clsName = name.Substring(0, index);
                fieldName = name.Substring(index + 1);
                index = clsName.LastIndexOf('.');
                if (TypeDict.ContainsKey(clsName))
                    clsType = TypeDict[clsName];
                // else if (clsName == "<Module>")
                //     clsType = Assembly.ManifestModule.;
                if (clsType == null)
                {
                    Console.Error.WriteLine("Can not find the class: {0} (for .{1})", clsName, fieldName);
                    return false;
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine("Can not locate a target method: " + name);
                Console.Error.Write(ex.ToString());
                return false;
            }
            if (newMethod.IsConstructor || fieldName == clsType.Name || fieldName == ".ctor")
            {
                Console.Error.WriteLine("Can not hook a constructor: " + name);
                return false;
            }
            var flags = (newMethod.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic)
                | (newMethod.IsStatic ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo oldMethod;
            try
            {
                oldMethod = clsType.GetMethod(fieldName, flags);
            }
            catch (AmbiguousMatchException)
            {
                Console.Error.WriteLine("The name has multiple signatures: " + name);
                return false;
            }
            if (oldMethod == null)
            {
                Console.Error.WriteLine("The method is not found: " + name);
                return false;
            }
            long addressSrc;
            try
            {
                addressSrc = Memory.GdhReplace(oldMethod, newMethod);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("Can not replace: {0} , error = {1}", name, ex.Message);
                return false;
            }
            ReplaceMethodAttribute.Remember(newMethod, new MethodHistory() {
                instance = oldMethod, body = oldMethod.GetMethodBody(), address = addressSrc
             });
            return true;
        }

        private static void PrepareTypeInfo()
        {
            if (TypeDict != null) { return; }
            lock (typeof(Program))
            {
                if (TypeDict == null)
                {
                    var dict = new Dictionary<string, Type>();
                    try
                    {
                        foreach (var type in Assembly.GetTypes())
                            if (type.IsClass && !type.IsSubclassOf(typeof(Delegate)) && !dict.ContainsKey(type.FullName))
                                dict.Add(type.FullName, type);
                        System.Threading.Thread.MemoryBarrier();
                    } catch (Exception ex)
                    {
                        Console.Error.WriteLine("Can not collect types: {0}", ex.GetType().FullName);
                        Console.Error.Flush();
                        Console.Error.WriteLine("  - Message: {0}", ex.Message);
                        Console.Error.Flush();
                        Console.Error.WriteLine("  - StackTrace: <<EOF\n{0}\nEOF", ex.ToString());
                        if (ex is ReflectionTypeLoadException lex)
                        {
                            foreach (var i in lex.LoaderExceptions)
                            {
                                Console.Error.WriteLine(">>> {0}", i.ToString());
                            }
                            Console.Error.WriteLine("EOF");
                        }
                        throw;
                    }
                    TypeDict = dict;
                }
            }
        }

        public MethodBase Hook4(int i)
        {
            var rgMethodHandle = (IntPtr[])typeof(StackTrace).Module
                .GetType("System.Diagnostics.StackFrameHelper")
                .GetField("rgMethodHandle", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(this);

            var methodHandleValue = rgMethodHandle?[i];

            var runtimeMethodInfoStub =
                typeof(StackTrace).Module.GetType("System.RuntimeMethodInfoStub").GetConstructors()[1]
                    .Invoke(new object[] { methodHandleValue, this });

            var typicalMethodDefinition = typeof(StackTrace).Module.GetType("System.RuntimeMethodHandle")
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "GetTypicalMethodDefinition" && m.GetParameters().Length == 1).ToArray()[0]
                .Invoke(null, new[] { runtimeMethodInfoStub });

            var result = (MethodBase)typeof(StackTrace).Module.GetType("System.RuntimeType")
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "GetMethodBase" && m.GetParameters().Length == 1).ToArray()[0]
                .Invoke(null, new[] { typicalMethodDefinition });

            if (result.Name == "InvokeMethod")
                result = CurrentMethodBase;
            return result;
        }

        public static void Hook5(ref MethodBase methodBase)
        {
            if (methodBase.Name == "InvokeMethod" && methodBase.DeclaringType == typeof(RuntimeMethodHandle))
            {
                methodBase = CurrentMethodBase;
            }
        }

        private static void HookSystemRuntimeTypeGetMethodBase()
        {
            var systemRuntimeTypeType = typeof(Type).Assembly.GetType("System.RuntimeType");

            var getMethodBase1 = systemRuntimeTypeType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(m => m.Name == "GetMethodBase")
                .Where(m => m.GetParameters().Length == 2)
                .FirstOrDefault(m =>
                    m.GetParameters().First().ParameterType == systemRuntimeTypeType &&
                    m.GetParameters().Last().ParameterType.Name == "IRuntimeMethodInfo");

            var getMethodBase2 = systemRuntimeTypeType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetMethodBase" && m.GetParameters().Length == 1);

            var myMethod = typeof(Program).GetMethod(nameof(Hook5), BindingFlags.Static | BindingFlags.Public);

            var replacementMethod = new MonoMod.Utils.DynamicMethodDefinition(
                getMethodBase2.Name,
                getMethodBase2.ReturnType,
                getMethodBase2.GetParameters().Select(x => x.ParameterType).ToArray()
            )
            {
                OwnerType = getMethodBase1.DeclaringType
            };

            var iLGenerator = replacementMethod.GetILGenerator();

            iLGenerator.DeclareLocal(typeof(MethodBase), false);

            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Ldnull);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Stloc_0);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Ldnull);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Call, getMethodBase1);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Stloc_0);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Ldloca, 0);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Call, myMethod);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Ldloc_0);
            iLGenerator.Emit(System.Reflection.Emit.OpCodes.Ret);

            var replacementMethodInfo = replacementMethod.Generate();

            Memory.SimpleHook(getMethodBase2, replacementMethodInfo);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
    }

}
