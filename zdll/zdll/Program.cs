using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Zdll;

internal static class Program
{
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // WriteIndented = true,  // ❌ 注释掉这一行
        // 或者显式设置为 false
        WriteIndented = false //按照单行输出
    };

    private const int DefaultStringBuilderCapacity = 2048;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        var commandArgs = args.Skip(1).ToArray();

        using var context = new LoaderContext();

        try
        {
            return command switch
            {
                "call" => HandleCall(commandArgs, context),
                "run" => HandleRun(commandArgs, context),
                "serve" => HandleServe(commandArgs, context),
                "list-types" => HandleListTypes(),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            var error = InvocationResult.CreateFailure("unhandled-error", ex.Message, ex.ToString());
            Console.WriteLine(JsonSerializer.Serialize(error, JsonOptions));
            return 1;
        }
    }

    private static int HandleCall(string[] args, LoaderContext context)
    {
        string? dllPath = null;
        string? functionName = null;
        string returnType = "int";
        var argumentTokens = new List<string>();
        var searchPaths = new List<string>();
        bool unloadAfterCall = true;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--dll":
                case "-d":
                    dllPath = RequireNext(args, ref i, token);
                    break;
                case "--function":
                case "-f":
                    functionName = RequireNext(args, ref i, token);
                    break;
                case "--arg":
                case "-a":
                    argumentTokens.Add(RequireNext(args, ref i, token));
                    break;
                case "--return":
                case "-r":
                    returnType = RequireNext(args, ref i, token);
                    break;
                case "--search-path":
                case "-s":
                    searchPaths.Add(RequireNext(args, ref i, token));
                    break;
                case "--keep-loaded":
                    unloadAfterCall = false;
                    break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"未知参数: {token}");
                    }
                    argumentTokens.Add(token);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dllPath))
        {
            throw new ArgumentException("未指定 DLL 路径。使用 --dll <path> 提供。");
        }

        if (string.IsNullOrWhiteSpace(functionName))
        {
            throw new ArgumentException("未指定函数名称。使用 --function <name> 提供。");
        }

        foreach (var path in searchPaths)
        {
            context.AddSearchPath(path);
        }

        var library = context.LoadLibrary(dllPath);
        var request = InvocationRequest.FromTokens(library, functionName, returnType, argumentTokens);
        var result = context.Invoke(request);

        if (unloadAfterCall)
        {
            context.UnloadLibrary(library.Name);
        }

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return result.Succeeded ? 0 : 1;
    }

    private static int HandleRun(string[] args, LoaderContext context)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("运行脚本需要提供 JSON 文件或以 --stdin 指定从标准输入读取。");
        }

        string? json = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--stdin")
            {
                json = Console.In.ReadToEnd();
            }
            else
            {
                json = File.ReadAllText(args[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("未提供有效的脚本内容。");
        }

        var script = JsonSerializer.Deserialize<LoaderScript>(json, JsonOptions)
                     ?? throw new InvalidOperationException("无法解析脚本 JSON。");

        foreach (var path in script.SearchPaths ?? Enumerable.Empty<string>())
        {
            context.AddSearchPath(path);
        }

        var results = new List<InvocationResult>();
        foreach (var step in script.Commands ?? Array.Empty<LoaderScriptCommand>())
        {
            switch (step.Type?.ToLowerInvariant())
            {
                case "load":
                    context.LoadLibrary(step.Path ?? throw new ArgumentException("load 命令缺少 path 字段。"), step.Alias);
                    results.Add(InvocationResult.CreateSuccess("load", 0, Array.Empty<ArgumentSnapshot>()));
                    break;
                case "unload":
                    context.UnloadLibrary(step.Dll ?? step.Alias ?? throw new ArgumentException("unload 命令缺少 dll 字段。"));
                    results.Add(InvocationResult.CreateSuccess("unload", 0, Array.Empty<ArgumentSnapshot>()));
                    break;
                case "call":
                    if (step.Dll is null && step.Alias is null)
                    {
                        throw new ArgumentException("call 命令缺少 dll 字段。");
                    }
                    var library = context.ResolveLibrary(step.Dll ?? step.Alias!);
                    var argTokens = step.Args ?? Array.Empty<string>();
                    var request = InvocationRequest.FromTokens(library, step.Function ?? throw new ArgumentException("call 命令缺少 function 字段。"), step.ReturnType ?? "int", argTokens);
                    var result = context.Invoke(request);
                    results.Add(result);
                    break;
                default:
                    throw new ArgumentException($"脚本命令类型不支持: {step.Type}");
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
        return results.All(r => r.Succeeded) ? 0 : 1;
    }
    private static int HandleServe(string[] args, LoaderContext context)
    {
        // 解析启动参数（如 --dll, --alias, --search-path 等）
        string? dllPath = null;
        string? alias = null;
        var searchPaths = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--dll":
                case "-d":
                    dllPath = RequireNext(args, ref i, token);
                    break;
                case "--alias":
                case "-a":
                    alias = RequireNext(args, ref i, token);
                    break;
                case "--search-path":
                case "-s":
                    searchPaths.Add(RequireNext(args, ref i, token));
                    break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"未知参数: {token}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("必须指定 --dll 路径");

        alias ??= Path.GetFileNameWithoutExtension(dllPath);

        foreach (var path in searchPaths)
            context.AddSearchPath(path);

        // 加载 DLL（只加载一次）
        context.LoadLibrary(dllPath, alias);

        Console.WriteLine(JsonSerializer.Serialize(
            InvocationResult.CreateSuccess("serve", 0, Array.Empty<ArgumentSnapshot>()),
            JsonOptions));

        Console.Out.Flush();

        // 主循环：从 stdin 读取命令
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var cmd = JsonSerializer.Deserialize<ServeCommand>(line, JsonOptions);
                if (cmd == null) throw new InvalidOperationException("无效命令");

                InvocationResult result;
                switch (cmd.Action?.ToLowerInvariant())
                {
                    case "call":
                        var lib = context.ResolveLibrary(alias);
                        var request = InvocationRequest.FromTokens(
                            lib,
                            cmd.Function ?? throw new ArgumentException("缺少 function"),
                            cmd.ReturnType ?? "int",
                            cmd.Args ?? Array.Empty<string>()
                        );
                        result = context.Invoke(request);
                        break;

                    case "unload":
                        context.UnloadLibrary(alias);
                        result = InvocationResult.CreateSuccess("unload", 0, Array.Empty<ArgumentSnapshot>());
                        // 可选择是否退出
                        break;

                    case "exit":
                        return 0;

                    default:
                        result = InvocationResult.CreateFailure("unknown-action", $"未知操作: {cmd.Action}", null);
                        break;
                }

                //Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));

                if (!string.IsNullOrEmpty(cmd.RequestId))
                {
                    // 创建带 requestId 的 wrapper
                    var wrapped = new { requestId = cmd.RequestId, result = result };
                    Console.WriteLine(JsonSerializer.Serialize(wrapped, JsonOptions));
                }
                else
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                }

                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                var error = InvocationResult.CreateFailure("parse-error", ex.Message, ex.ToString());
                Console.WriteLine(JsonSerializer.Serialize(error, JsonOptions));
                Console.Out.Flush();
            }
        }

        return 0;
    }
    private static int HandleListTypes()
    {
        var supported = new[]
        {
            "int", "uint", "short", "ushort", "long", "ulong", "byte", "sbyte", "bool",
            "string", "stringbuilder", "intptr", "double", "float", "void"
        };
        Console.WriteLine(JsonSerializer.Serialize(supported, JsonOptions));
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        var error = InvocationResult.CreateFailure("unknown-command", $"未知命令: {command}", null);
        Console.WriteLine(JsonSerializer.Serialize(error, JsonOptions));
        PrintUsage();
        return 1;
    }

    private static string RequireNext(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} 缺少值。");
        }

        return args[++index];
    }

    private static void PrintUsage()
    {
        const string text =
@"通用 DLL 调用工具
====================

基本调用:
  Zdll call --dll <path> --function <name> [--arg type=value ...]

脚本执行:
  Zdll run <script.json>
  Zdll run --stdin   # 从标准输入读取脚本 JSON

其他命令:
  Zdll list-types    # 查看支持的参数类型
  Zdll --help        # 显示本帮助

参数书写规则:
  - 参数使用 type=value 格式，按函数入参顺序书写。
  - StringBuilder 支持容量写法，例如: StringBuilder[4096]=
  - 整数支持十六进制 (0x...) 或十进制表示。

示例 (调用 WinSocketServer.dll -> LgServer):
  Zdll call --dll .\WinSocketServer.dll --function LgServer \
    --arg string=192.168.1.10 --arg ushort=8001 --arg int=8 --arg string=11111111

脚本示例:
{
  ""searchPaths"": [""./EncryptionMachine/ServerModelEncryptionMachine""],
  ""commands"": [
    { ""type"": ""load"", ""path"": ""./EncryptionMachine/ServerModelEncryptionMachine/WinSocketServer.dll"", ""alias"": ""WinSocket"" },
    { ""type"": ""call"", ""alias"": ""WinSocket"", ""function"": ""OpenUsbkey"" },
    { ""type"": ""call"", ""alias"": ""WinSocket"", ""function"": ""LgServer"", ""args"": [""string=22.58.244.70"", ""ushort=8001"", ""int=8"", ""string=11111111""] },
    { ""type"": ""call"", ""alias"": ""WinSocket"", ""function"": ""CreateRand"", ""args"": [""int=16"", ""StringBuilder[1024]="" ] },
    { ""type"": ""call"", ""alias"": ""WinSocket"", ""function"": ""LgoutServer"" },
    { ""type"": ""unload"", ""alias"": ""WinSocket"" }
  ]
}";
        Console.WriteLine(text);
    }

    private sealed class LoaderContext : IDisposable
    {
        private readonly Dictionary<string, LoadedLibrary> _libraries = new(StringComparer.OrdinalIgnoreCase);

        public LoadedLibrary LoadLibrary(string dllPath, string? alias = null)
        {
            var fullPath = Path.GetFullPath(dllPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"找不到 DLL: {dllPath}", dllPath);
            }

            var name = alias ?? Path.GetFileName(fullPath);
            if (_libraries.ContainsKey(name))
            {
                return _libraries[name];
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                AddSearchPath(directory);
            }

            var handle = NativeLibrary.Load(fullPath);
            var library = new LoadedLibrary(name, fullPath, handle);
            _libraries[name] = library;
            return library;
        }

        public void UnloadLibrary(string name)
        {
            if (_libraries.TryGetValue(name, out var library))
            {
                library.Dispose();
                _libraries.Remove(name);
            }
        }

        public LoadedLibrary ResolveLibrary(string nameOrPath)
        {
            if (_libraries.TryGetValue(nameOrPath, out var library))
            {
                return library;
            }

            return LoadLibrary(nameOrPath);
        }

        public void AddSearchPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"搜索路径不存在: {path}");
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var pathSegments = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (!pathSegments.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", fullPath + Path.PathSeparator + currentPath);
            }
        }

        public InvocationResult Invoke(InvocationRequest request)
        {
            var functionPtr = NativeLibrary.GetExport(request.Library.Handle, request.FunctionName);
            var delegateType = request.CreateDelegateType();
            var parameters = request.CreateParameterValues();

            var functionDelegate = Marshal.GetDelegateForFunctionPointer(functionPtr, delegateType);
            try
            {
                var returnValue = functionDelegate.DynamicInvoke(parameters.Values);
                var retCode = request.ReturnType == typeof(void)
                    ? 0
                    : Convert.ToInt32(returnValue, CultureInfo.InvariantCulture);
                return InvocationResult.CreateSuccess(
                    request.FunctionName,
                    retCode,
                    parameters.TakeSnapshots());
            }
            catch (Exception ex)
            {
                return InvocationResult.CreateFailure(request.FunctionName, ex.Message, ex.ToString());
            }
        }

        public void Dispose()
        {
            foreach (var library in _libraries.Values)
            {
                library.Dispose();
            }
            _libraries.Clear();
        }
    }

    private sealed record LoadedLibrary(string Name, string Path, IntPtr Handle) : IDisposable
    {
        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                NativeLibrary.Free(Handle);
            }
        }
    }

    private sealed record InvocationRequest(
        LoadedLibrary Library,
        string FunctionName,
        Type ReturnType,
        IReadOnlyList<ArgumentSpecification> Arguments)
    {
        public static InvocationRequest FromTokens(LoadedLibrary library, string functionName, string returnType, IEnumerable<string> tokens)
        {
            var argumentList = tokens.Select((token, index) => ArgumentSpecification.Parse(token, index)).ToList();
            return new InvocationRequest(library, functionName, TypeResolver.Resolve(returnType), argumentList);
        }

        public DelegateTypeInfo CreateParameterValues()
        {
            var values = new object?[Arguments.Count];
            for (var i = 0; i < Arguments.Count; i++)
            {
                values[i] = Arguments[i].CreateValue();
            }

            return new DelegateTypeInfo(values, Arguments);
        }

        public Type CreateDelegateType()
        {
            var typeList = new List<Type>(Arguments.Count);
            foreach (var argument in Arguments)
            {
                typeList.Add(argument.ClrType);
            }

            return DelegateFactory.GetDelegate(ReturnType, typeList);
        }
    }

    private sealed class DelegateTypeInfo
    {
        public object?[] Values { get; }
        private IReadOnlyList<ArgumentSpecification> Specifications { get; }

        public DelegateTypeInfo(object?[] values, IReadOnlyList<ArgumentSpecification> specs)
        {
            Values = values;
            Specifications = specs;
        }

        public IReadOnlyList<ArgumentSnapshot> TakeSnapshots()
        {
            var snapshots = new List<ArgumentSnapshot>(Specifications.Count);
            for (var i = 0; i < Specifications.Count; i++)
            {
                var spec = Specifications[i];
                snapshots.Add(new ArgumentSnapshot(
                    i,
                    spec.TypeName,
                    spec.ReadValue(Values[i]),
                    spec.IsOutputType));
            }

            return snapshots;
        }
    }

    private static class DelegateFactory
    {
        private static readonly ModuleBuilder ModuleBuilder;
        private static readonly Dictionary<string, Type> Cache = new(StringComparer.Ordinal);

        static DelegateFactory()
        {
            var assemblyName = new AssemblyName("Zdll.DynamicDelegates");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            ModuleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        }

        public static Type GetDelegate(Type returnType, IReadOnlyList<Type> parameterTypes)
        {
            var key = BuildKey(returnType, parameterTypes);

            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var typeName = $"UniversalDllDelegate_{Cache.Count}";
                var typeBuilder = ModuleBuilder.DefineType(
                    typeName,
                    TypeAttributes.Sealed | TypeAttributes.Public | TypeAttributes.Class,
                    typeof(MulticastDelegate));

                var attrCtor = typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[] { typeof(CallingConvention) });
                var attrBuilder = new CustomAttributeBuilder(attrCtor!, new object[] { CallingConvention.Winapi });
                typeBuilder.SetCustomAttribute(attrBuilder);

                var ctorBuilder = typeBuilder.DefineConstructor(
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    CallingConventions.Standard,
                    new[] { typeof(object), typeof(IntPtr) });
                ctorBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                var invokeBuilder = typeBuilder.DefineMethod(
                    "Invoke",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
                    returnType,
                    parameterTypes.ToArray());
                invokeBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

                var delegateType = typeBuilder.CreateTypeInfo()!.AsType();
                Cache[key] = delegateType;
                return delegateType;
            }
        }

        private static string BuildKey(Type returnType, IReadOnlyList<Type> parameterTypes)
        {
            var builder = new StringBuilder(returnType.FullName);
            foreach (var parameterType in parameterTypes)
            {
                builder.Append('|').Append(parameterType.FullName);
            }
            return builder.ToString();
        }
    }

    private sealed record ArgumentSpecification(string TypeName, Type ClrType, string? RawValue, int? Capacity, int Position)
    {
        public static ArgumentSpecification Parse(string token, int position)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("参数不能为空白。");
            }

            var parts = token.Split('=', 2);
            var typeSpec = parts[0].Trim();
            var rawValue = parts.Length > 1 ? parts[1] : string.Empty;

            int? capacity = null;
            string typeName = typeSpec;
            var bracketStart = typeSpec.IndexOf('[');
            if (bracketStart >= 0)
            {
                var bracketEnd = typeSpec.IndexOf(']', bracketStart + 1);
                if (bracketEnd > bracketStart)
                {
                    var capture = typeSpec[(bracketStart + 1)..bracketEnd];
                    if (int.TryParse(capture, out var parsed))
                    {
                        capacity = parsed;
                    }
                    typeName = typeSpec[..bracketStart];
                }
            }

            var clrType = TypeResolver.Resolve(typeName);
            return new ArgumentSpecification(typeName, clrType, rawValue, capacity, position);
        }

        public object? CreateValue()
        {
            return TypeName.ToLowerInvariant() switch
            {
                "string" => RawValue,
                "stringbuilder" => CreateStringBuilder(),
                "int" => ParseInteger<int>(RawValue),
                "uint" => ParseInteger<uint>(RawValue),
                "short" => ParseInteger<short>(RawValue),
                "ushort" => ParseInteger<ushort>(RawValue),
                "long" => ParseInteger<long>(RawValue),
                "ulong" => ParseInteger<ulong>(RawValue),
                "byte" => ParseInteger<byte>(RawValue),
                "sbyte" => ParseInteger<sbyte>(RawValue),
                "bool" => ParseBoolean(RawValue),
                "intptr" => ParseIntPtr(RawValue),
                "double" => ParseFloating<double>(RawValue),
                "float" => ParseFloating<float>(RawValue),
                _ => throw new NotSupportedException($"不支持的参数类型: {TypeName}")
            };
        }

        public bool IsOutputType => string.Equals(TypeName, "stringbuilder", StringComparison.OrdinalIgnoreCase);

        public object? ReadValue(object? value)
        {
            return value switch
            {
                StringBuilder sb => sb.ToString(),
                _ => value
            };
        }

        private object CreateStringBuilder()
        {
            var capacity = Capacity ?? Math.Max(DefaultStringBuilderCapacity, RawValue?.Length ?? 0);
            var sb = new StringBuilder(capacity);
            if (!string.IsNullOrEmpty(RawValue))
            {
                sb.Append(RawValue);
            }
            return sb;
        }

        private static T ParseInteger<T>(string? text) where T : struct, IConvertible
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            var trimmed = text.Trim();
            var sign = 1;

            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                sign = -1;
                trimmed = trimmed[1..];
            }

            if (trimmed.StartsWith("+", StringComparison.Ordinal))
            {
                trimmed = trimmed[1..];
            }

            var fromBase = 10;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                fromBase = 16;
                trimmed = trimmed[2..];
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return default;
            }

            var numeric = System.Convert.ToInt64(trimmed, fromBase) * sign;
            var value = Convert.ChangeType(numeric, typeof(T), CultureInfo.InvariantCulture);
            return (T)value;
        }

        private static T ParseFloating<T>(string? text) where T : struct, IConvertible
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            return (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);
        }

        private static bool ParseBoolean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (bool.TryParse(text, out var value))
            {
                return value;
            }

            try
            {
                var numeric = ParseInteger<int>(text);
                return numeric != 0;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr ParseIntPtr(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return IntPtr.Zero;
            }

            var value = ParseInteger<long>(text);
            return new IntPtr(value);
        }
    }

    private sealed class TypeResolver
    {
        private static readonly Dictionary<string, Type> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["int"] = typeof(int),
            ["uint"] = typeof(uint),
            ["short"] = typeof(short),
            ["ushort"] = typeof(ushort),
            ["long"] = typeof(long),
            ["ulong"] = typeof(ulong),
            ["byte"] = typeof(byte),
            ["sbyte"] = typeof(sbyte),
            ["bool"] = typeof(bool),
            ["string"] = typeof(string),
            ["stringbuilder"] = typeof(StringBuilder),
            ["intptr"] = typeof(IntPtr),
            ["double"] = typeof(double),
            ["float"] = typeof(float),
            ["void"] = typeof(void)
        };

        public static Type Resolve(string typeName)
        {
            if (Map.TryGetValue(typeName, out var type))
            {
                return type;
            }

            throw new NotSupportedException($"不支持的类型: {typeName}");
        }
    }

    private sealed record ArgumentSnapshot(int Index, string Type, object? Value, bool IsOutput);

    private sealed record InvocationResult(bool Succeeded, string Command, int ReturnCode, string? Error, string? Detail, IReadOnlyList<ArgumentSnapshot> Outputs)
    {
        public static InvocationResult CreateSuccess(string command, int ret, IReadOnlyList<ArgumentSnapshot> outputs)
            => new(true, command, ret, null, null, outputs);

        public static InvocationResult CreateFailure(string command, string error, string? detail)
            => new(false, command, -1, error, detail, Array.Empty<ArgumentSnapshot>());
    }

    private sealed record LoaderScript(string[]? SearchPaths, LoaderScriptCommand[]? Commands);

    private sealed record LoaderScriptCommand
    {
        public string? Type { get; init; }
        public string? Path { get; init; }
        public string? Dll { get; init; }
        public string? Alias { get; init; }
        public string? Function { get; init; }
        public string[]? Args { get; init; }
        public string? ReturnType { get; init; }
    }

    private sealed record ServeCommand
    {
        public string? RequestId { get; init; } 

        public string? Action { get; init; }      // "call", "unload", "exit"
        public string? Function { get; init; }
        public string? ReturnType { get; init; }
        public string[]? Args { get; init; }
    }
}
