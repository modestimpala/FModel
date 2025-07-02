using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.Utils;

namespace FModel.Framework;

public class BlueprintToCppConverter
{
    private bool _isVerse;

    public async Task<BlueprintConversionResult> ConvertBlueprintAsync(
        IFileProvider provider,
        string blueprintPath,
        string outputDirectory = null)
    {
        var results = new List<ConvertedFile>();
        var errors = new List<string>();

        try
        {
            var files = GetFilesToProcess(provider, blueprintPath);
            int totalGameFiles = files.Sum(kv => kv.Value.Length);

            foreach (var (folder, packages) in files)
            {
                Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    package =>
                    {
                        try
                        {
                            var result = ProcessPackage(provider, package, outputDirectory);
                            if (result != null)
                            {
                                lock (results)
                                {
                                    results.Add(result);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (errors)
                            {
                                errors.Add($"Error processing {package.Path}: {ex.Message}");
                            }
                        }
                    });
            }

            return new BlueprintConversionResult
            {
                ConvertedFiles = results, Errors = errors, Success = errors.Count == 0
            };
        }
        catch (Exception ex)
        {
            return new BlueprintConversionResult
            {
                ConvertedFiles = new List<ConvertedFile>(),
                Errors = new List<string> { $"Conversion failed: {ex.Message}" },
                Success = false
            };
        }
    }

    private Dictionary<string, GameFile[]> GetFilesToProcess(IFileProvider provider, string blueprintPath)
    {
        var files = new Dictionary<string, GameFile[]>();

        if (string.IsNullOrEmpty(blueprintPath))
        {
            // Process all compatible blueprints
            files = provider.Files.Values
                .Where(f => (f.Path.EndsWith(".uasset") || f.Path.EndsWith(".umap")) && !f.Path.Contains(".o."))
                .GroupBy(f => f.Path.SubstringBeforeLast('/'))
                .ToDictionary(g => g.Key, g => g.ToArray());
        }
        else if (provider.Files.ContainsKey(blueprintPath))
        {
            // Single file
            files = new Dictionary<string, GameFile[]> { [blueprintPath] = new[] { provider.Files[blueprintPath] } };
        }
        else
        {
            // Folder path
            files = provider.Files.Values
                .Where(f => f.Path.StartsWith(blueprintPath + "/") &&
                            (f.Path.EndsWith(".uasset") || f.Path.EndsWith(".umap")) &&
                            !f.Path.Contains(".o."))
                .GroupBy(f => f.Path.SubstringBeforeLast('/'))
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        return files;
    }

    private ConvertedFile ProcessPackage(IFileProvider provider, GameFile package, string outputDirectory)
    {
        if (!package.IsUePackage)
            return null;

        var pkg = provider.LoadPackage(package);
        _isVerse = false;

        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null)
                continue;

            var dummy = ((AbstractUePackage) pkg).ConstructObject(
                pointer.Class?.Object?.Value as UStruct, pkg);

            switch (dummy)
            {
                case UBlueprintGeneratedClass _:
                case UVerseClass _:
                {
                    var outputBuilder = new StringBuilder();
                    var blueprintGeneratedClass =
                        pkg.ExportsLazy.FirstOrDefault(e => e.Value is UBlueprintGeneratedClass)?.Value as
                            UBlueprintGeneratedClass;
                    var verseClass = pkg.ExportsLazy.Where(export => export.Value is UVerseClass)
                        .Select(export => (UVerseClass) export.Value).FirstOrDefault();

                    if (verseClass != null)
                        _isVerse = true;

                    if (blueprintGeneratedClass != null || _isVerse)
                    {
                        var cppContent = GenerateCppContent(pkg, blueprintGeneratedClass, verseClass);
                        var fileName = $"{package.Name.Replace(".uasset", "")}.cpp";

                        string outputPath = null;
                        if (!string.IsNullOrEmpty(outputDirectory))
                        {
                            string blueprintDirRel = Path.GetDirectoryName(package.Path ?? string.Empty);
                            string blueprintDirOutput = Path.Combine(outputDirectory, blueprintDirRel ?? string.Empty);
                            Directory.CreateDirectory(blueprintDirOutput);
                            outputPath = Path.Combine(blueprintDirOutput, fileName);
                            File.WriteAllText(outputPath, cppContent);
                        }

                        return new ConvertedFile
                        {
                            OriginalPath = package.Path,
                            FileName = fileName,
                            CppContent = cppContent,
                            OutputPath = outputPath
                        };
                    }

                    break;
                }
            }
        }

        return null;
    }

    private string GenerateCppContent(IPackage pkg, UBlueprintGeneratedClass blueprintGeneratedClass,
        UVerseClass verseClass)
    {
        var outputBuilder = new StringBuilder();

        var mainClass = blueprintGeneratedClass?.Name ?? verseClass?.Name;
        var superStructName =
            blueprintGeneratedClass?.SuperStruct?.Name ?? verseClass?.SuperStruct?.Name ?? string.Empty;

        outputBuilder.AppendLine(
            $"class {BlueprintToCppUtils.GetPrefix(blueprintGeneratedClass?.GetType().Name ?? verseClass?.GetType().Name)}{mainClass} : public {BlueprintToCppUtils.GetPrefix(blueprintGeneratedClass?.GetType().Name ?? verseClass?.GetType().Name)}{superStructName}\n{{\npublic:");

        // Process properties
        var stringsarray = ProcessProperties(pkg, outputBuilder, mainClass, blueprintGeneratedClass, verseClass);

        // Process functions
        ProcessFunctions(pkg, outputBuilder, blueprintGeneratedClass, verseClass, stringsarray);

        outputBuilder.Append("\n\n}");

        // Clean up placeholders
        string pattern = @"\w+placenolder";
        return Regex.Replace(outputBuilder.ToString(), pattern, "nullptr");
    }

    private List<string> ProcessProperties(IPackage pkg, StringBuilder outputBuilder, string mainClass,
        UBlueprintGeneratedClass blueprintGeneratedClass, UVerseClass verseClass)
    {
        var stringsarray = new List<string>();

        foreach (var export in pkg.ExportsLazy)
        {
            if (export.Value is not UBlueprintGeneratedClass)
            {
                if (export.Value.Name.StartsWith("Default__") && export.Value.Name.EndsWith(mainClass ?? string.Empty))
                {
                    var exportObject = export.Value;
                    foreach (var key in exportObject.Properties)
                    {
                        ProcessSingleProperty(key, outputBuilder, stringsarray);
                    }
                }
            }
        }

        var childProperties = blueprintGeneratedClass?.ChildProperties ?? verseClass?.ChildProperties;
        if (childProperties != null)
        {
            foreach (FProperty property in childProperties)
            {
                if (!stringsarray.Contains(property.Name.PlainText))
                    outputBuilder.AppendLine(
                        $"\t{BlueprintToCppUtils.GetPrefix(property.GetType().Name)}{BlueprintToCppUtils.GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || BlueprintToCppUtils.GetPropertyProperty(property) ? "*" : string.Empty)} {property.Name.PlainText.Replace(" ", "")} = {property.Name.PlainText.Replace(" ", "")}placenolder;");
            }
        }

        return stringsarray;
    }

    private void ProcessSingleProperty(FPropertyTag key, StringBuilder outputBuilder, List<string> stringsarray)
    {
        stringsarray.Add(key.Name.PlainText);
        string placeholder = $"{key.Name}placenolder";
        string result = key.Tag.GenericValue.ToString();
        string keyName = key.Name.PlainText.Replace(" ", "");

        var propertyTag = key.Tag.GetValue(typeof(object));

        void ShouldAppend(string? value)
        {
            if (value == null)
                return;
            if (outputBuilder.ToString().Contains(placeholder))
            {
                outputBuilder.Replace(placeholder, value);
            }
            else
            {
                outputBuilder.AppendLine($"\t{BlueprintToCppUtils.GetPropertyType(propertyTag)} {keyName} = {value};");
            }
        }

        if (key.Tag.GenericValue is FScriptStruct structTag)
        {
            if (structTag.StructType is FVector vector)
            {
                ShouldAppend($"FVector({vector.X}, {vector.Y}, {vector.Z})");
            }
            else if (structTag.StructType is FGuid guid)
            {
                ShouldAppend($"FGuid({guid.A}, {guid.B}, {guid.C}, {guid.D})");
            }
            else if (structTag.StructType is TIntVector3<int> vector3)
            {
                ShouldAppend($"FVector({vector3.X}, {vector3.Y}, {vector3.Z})");
            }
            else if (structTag.StructType is TIntVector3<float> floatVector3)
            {
                ShouldAppend($"FVector({floatVector3.X}, {floatVector3.Y}, {floatVector3.Z})");
            }
            else if (structTag.StructType is TIntVector2<float> floatVector2)
            {
                ShouldAppend($"FVector2D({floatVector2.X}, {floatVector2.Y})");
            }
            else if (structTag.StructType is FVector2D vector2d)
            {
                ShouldAppend($"FVector2D({vector2d.X}, {vector2d.Y})");
            }
            else if (structTag.StructType is FRotator rotator)
            {
                ShouldAppend($"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})");
            }
            else if (structTag.StructType is FStructFallback fallback)
            {
                string formattedTags;
                if (fallback.Properties.Count > 0)
                {
                    formattedTags = "[\n" + string.Join(",\n",
                        fallback.Properties.Select(tag =>
                        {
                            string tagDataFormatted;
                            if (tag.Tag is TextProperty text)
                            {
                                tagDataFormatted = $"\"{text.Value.Text}\"";
                            }
                            else if (tag.Tag is NameProperty name)
                            {
                                tagDataFormatted = $"\"{name.Value.Text}\"";
                            }
                            else if (tag.Tag is ObjectProperty objectproperty)
                            {
                                tagDataFormatted = $"\"{objectproperty.Value}\"";
                            }
                            else if (tag.Tag.GenericValue is FScriptStruct innerStruct &&
                                     innerStruct.StructType is FStructFallback nestedFallback)
                            {
                                if (nestedFallback.Properties.Count > 0)
                                {
                                    tagDataFormatted = "{ " + string.Join(", ",
                                        nestedFallback.Properties.Select(nested =>
                                        {
                                            string nestedVal;
                                            if (nested.Tag is TextProperty textProp)
                                                nestedVal = $"\"{textProp.Value.Text}\"";
                                            else if (nested.Tag is NameProperty nameProp)
                                                nestedVal = $"\"{nameProp.Value.Text}\"";
                                            else
                                                nestedVal = $"\"{nested.Tag.GenericValue}\"";

                                            return $"\"{nested.Name}\": {nestedVal}";
                                        })) + " }";
                                }
                                else
                                {
                                    tagDataFormatted = "{}";
                                }
                            }
                            else
                            {
                                tagDataFormatted = tag.Tag.GenericValue != null
                                    ? tag.Tag.GenericValue.ToString()
                                    : "{}";
                            }

                            return $"\t\t{{ \"{tag.Name}\": {tagDataFormatted} }}";
                        })) + "\n\t]";
                }
                else
                {
                    formattedTags = "[]";
                }

                ShouldAppend(formattedTags);
            }
            else if (structTag.StructType is FGameplayTagContainer gameplayTag)
            {
                var tags = gameplayTag.GameplayTags.ToList();
                if (tags.Count > 1)
                {
                    var formattedTags = "[\n" + string.Join(",\n",
                        tags.Select(tag => $"\t\t\"{tag.TagName}\"")) + "\n\t]";
                    ShouldAppend(formattedTags);
                }
                else if (tags.Any())
                {
                    ShouldAppend($"\"{tags.First().TagName}\"");
                }
                else
                {
                    ShouldAppend("[]");
                }
            }
            else if (structTag.StructType is FLinearColor color)
            {
                ShouldAppend($"FLinearColor({color.R}, {color.G}, {color.B}, {color.A})");
            }
            else
            {
                ShouldAppend($"\"{result}\"");
            }
        }
        else if (key.Tag.GetType().Name == "ObjectProperty" ||
                 key.Tag.GetType().Name == "TextProperty" ||
                 key.PropertyType == "StrProperty" ||
                 key.PropertyType == "NameProperty" ||
                 key.PropertyType == "ClassProperty")
        {
            ShouldAppend($"\"{result}\"");
        }
        else if (key.Tag.GenericValue is UScriptSet set)
        {
            var formattedSet = "[\n" + string.Join(",\n",
                set.Properties.Select(p => $"\t\"{p.GenericValue}\"")) + "\n\t]";
            ShouldAppend(formattedSet);
        }
        else if (key.Tag.GenericValue is UScriptMap map)
        {
            var formattedMap = "[\n" + string.Join(",\n",
                map.Properties.Select(kvp => $"\t{{\n\t\t\"{kvp.Key}\": \"{kvp.Value}\"\n\t}}")) + "\n\t]";
            ShouldAppend(formattedMap);
        }
        else if (key.Tag.GenericValue is UScriptArray array)
        {
            var formattedArray = "[\n" + string.Join(",\n",
                array.Properties.Select(p =>
                {
                    if (p.GenericValue is FScriptStruct vectorInArray && vectorInArray.StructType is FVector vector)
                    {
                        return $"FVector({vector.X}, {vector.Y}, {vector.Z})";
                    }

                    if (p.GenericValue is FScriptStruct vector2dInArray &&
                        vector2dInArray.StructType is FVector2D vector2d)
                    {
                        return $"FVector2D({vector2d.X}, {vector2d.Y})";
                    }

                    if (p.GenericValue is FScriptStruct structInArray && structInArray.StructType is FRotator rotator)
                    {
                        return $"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})";
                    }
                    else if (p.GenericValue is FScriptStruct fallbacksInArray &&
                             fallbacksInArray.StructType is FStructFallback fallback)
                    {
                        string formattedTags;
                        if (fallback.Properties.Count > 0)
                        {
                            formattedTags = "\t[\n" + string.Join(",\n",
                                fallback.Properties.Select(tag =>
                                {
                                    string tagDataFormatted;
                                    if (tag.Tag is TextProperty text)
                                    {
                                        tagDataFormatted = $"\"{text.Value.Text}\"";
                                    }
                                    else if (tag.Tag is NameProperty name)
                                    {
                                        tagDataFormatted = $"\"{name.Value.Text}\"";
                                    }
                                    else if (tag.Tag is ObjectProperty objectproperty)
                                    {
                                        tagDataFormatted = $"\"{objectproperty.Value}\"";
                                    }
                                    else
                                    {
                                        tagDataFormatted = $"\"{tag.Tag.GenericValue}\"";
                                    }

                                    return $"\t\t\"{tag.Name}\": {tagDataFormatted}";
                                })) + "\n\t]";
                        }
                        else
                        {
                            formattedTags = "{}";
                        }

                        return formattedTags;
                    }
                    else if (p.GenericValue is FScriptStruct gameplayTagsInArray &&
                             gameplayTagsInArray.StructType is FGameplayTagContainer gameplayTag)
                    {
                        var tags = gameplayTag.GameplayTags.ToList();
                        if (tags.Count > 1)
                        {
                            var formattedTags = "[\n" + string.Join(",\n",
                                tags.Select(tag => $"\t\t\"{tag.TagName}\"")) + "\n\t]";
                            return formattedTags;
                        }
                        else
                        {
                            return $"\"{tags.First().TagName}\"";
                        }
                    }

                    return $"\t\t\"{p.GenericValue}\"";
                })) + "\n\t]";
            ShouldAppend(formattedArray);
        }
        else if (key.Tag.GenericValue is FMulticastScriptDelegate multicast)
        {
            var list = multicast.InvocationList;
            ShouldAppend(list.Length == 0 ? "[]" : $"[{string.Join(", ", list.Select(x => $"\"{x.FunctionName}\""))}]");
        }
        else if (key.Tag.GenericValue is bool boolResult)
        {
            ShouldAppend(boolResult.ToString().ToLower());
        }
        else
        {
            ShouldAppend(result);
        }
    }

    private void ProcessFunctions(IPackage pkg, StringBuilder outputBuilder,
        UBlueprintGeneratedClass blueprintGeneratedClass, UVerseClass verseClass, List<string> stringsarray)
    {
        var funcMapOrder = blueprintGeneratedClass?.FuncMap?.Keys.Select(fname => fname.ToString()).ToList()
                           ?? verseClass?.FuncMap.Keys.Select(fname => fname.ToString()).ToList();

        var functions = pkg.ExportsLazy
            .Where(e => e.Value is UFunction)
            .Select(e => (UFunction) e.Value)
            .OrderBy(f =>
            {
                if (funcMapOrder != null)
                {
                    var functionName = f.Name.ToString();
                    int index = funcMapOrder.IndexOf(functionName);
                    return index >= 0 ? index : int.MaxValue;
                }

                return int.MaxValue;
            })
            .ThenBy(f => f.Name.ToString())
            .ToList();

        var jumpCodeOffsetsMap = BuildJumpCodeOffsetsMap(functions);

        foreach (var function in functions)
        {
            ProcessSingleFunction(function, outputBuilder, jumpCodeOffsetsMap);
        }
    }

    private Dictionary<string, List<int>> BuildJumpCodeOffsetsMap(List<UFunction> functions)
    {
        var jumpCodeOffsetsMap = new Dictionary<string, List<int>>();

        foreach (var function in functions.AsEnumerable().Reverse())
        {
            if (function?.ScriptBytecode == null)
                continue;

            foreach (var property in function.ScriptBytecode)
            {
                string? label = null;
                int? offset = null;

                switch (property.Token)
                {
                    case EExprToken.EX_JumpIfNot:
                        label = ((EX_JumpIfNot) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                        offset = (int) ((EX_JumpIfNot) property).CodeOffset;
                        break;

                    case EExprToken.EX_Jump:
                        label = ((EX_Jump) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                        offset = (int) ((EX_Jump) property).CodeOffset;
                        break;

                    case EExprToken.EX_LocalFinalFunction:
                    {
                        EX_FinalFunction op = (EX_FinalFunction) property;
                        label = op.StackNode?.Name?.ToString()?.Split('.').Last().Split('[')[0];

                        if (op.Parameters.Length == 1 && op.Parameters[0] is EX_IntConst intConst)
                            offset = intConst.Value;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(label) && offset.HasValue)
                {
                    if (!jumpCodeOffsetsMap.TryGetValue(label, out var list))
                        jumpCodeOffsetsMap[label] = list = new List<int>();

                    list.Add(offset.Value);
                }
            }
        }

        return jumpCodeOffsetsMap;
    }

    private void ProcessSingleFunction(UFunction function, StringBuilder outputBuilder,
        Dictionary<string, List<int>> jumpCodeOffsetsMap)
    {
        string argsList = BuildArgumentsList(function);
        string returnFunc = GetReturnType(function);

        outputBuilder.AppendLine($"\n\t{returnFunc} {function.Name.Replace(" ", "")}({argsList})\n\t{{");

        if (function?.ScriptBytecode != null)
        {
            var jumpCodeOffsets = jumpCodeOffsetsMap.TryGetValue(function.Name, out var list) ? list : new List<int>();
            foreach (KismetExpression property in function.ScriptBytecode)
            {
                ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
            }
        }
        else
        {
            outputBuilder.Append("\n\t // This function does not have Bytecode \n\n");
            outputBuilder.Append("\t}\n");
        }


    }

    private string BuildArgumentsList(UFunction function)
    {
        string argsList = "";
        if (function?.ChildProperties != null)
        {
            foreach (FProperty property in function.ChildProperties)
            {
                if (property.Name.PlainText == "ReturnValue")
                {
                    // Skip return value, handled in GetReturnType
                }
                else if (!(property.Name.ToString().EndsWith("_ReturnValue") ||
                           property.Name.ToString().StartsWith("CallFunc_") ||
                           property.Name.ToString().StartsWith("K2Node_") ||
                           property.Name.ToString().StartsWith("Temp_")) || // removes useless args
                         property.PropertyFlags.HasFlag(EPropertyFlags.Edit))
                {
                    argsList +=
                        $"{(property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "const " : string.Empty)}{BlueprintToCppUtils.GetPrefix(property.GetType().Name)}{BlueprintToCppUtils.GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || BlueprintToCppUtils.GetPrefix(property.GetType().Name) == "U" ? "*" : string.Empty)}{(property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) ? "&" : string.Empty)} {Regex.Replace(property.Name.ToString(), @"^__verse_0x[0-9A-Fa-f]+_", "")}, ";
                }
            }
        }

        return argsList.TrimEnd(',', ' ');
    }

    private string GetReturnType(UFunction function)
    {
        string returnFunc = "void";
        if (function?.ChildProperties != null)
        {
            foreach (FProperty property in function.ChildProperties)
            {
                if (property.Name.PlainText == "ReturnValue")
                {
                    returnFunc =
                        $"{(property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "const " : string.Empty)}{BlueprintToCppUtils.GetPrefix(property.GetType().Name)}{BlueprintToCppUtils.GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || BlueprintToCppUtils.GetPrefix(property.GetType().Name) == "U" ? "*" : string.Empty)}";
                }
            }
        }

        return returnFunc;
    }

    private string ProcessTextProperty(FKismetPropertyPointer property)
    {
        if (property.New is null)
        {
            return property.Old?.Name ?? string.Empty;
        }

        if (_isVerse)
        {
            return Regex.Replace(string.Join('.', property.New.Path.Select(n => n.Text)), @"^__verse_0x[0-9A-Fa-f]+_",
                "");
        }

        return string.Join('.', property.New.Path.Select(n => n.Text)).Replace(" ", "");
    }

    private void ProcessExpression(EExprToken token, KismetExpression expression, StringBuilder outputBuilder,
        List<int> jumpCodeOffsets, bool isParameter = false)
    {
        if (jumpCodeOffsets.Contains(expression.StatementIndex))
        {
            outputBuilder.Append("\t\tLabel_" + expression.StatementIndex + ":\n");
        }

        switch (token)
        {
            case EExprToken.EX_LetValueOnPersistentFrame:
            {
                EX_LetValueOnPersistentFrame op = (EX_LetValueOnPersistentFrame) expression;
                EX_VariableBase opp = (EX_VariableBase) op.AssignmentExpression;
                var destination = ProcessTextProperty(op.DestinationProperty);
                var variable = ProcessTextProperty(opp.Variable);

                if (!isParameter)
                {
                    outputBuilder.Append(
                        $"\t\t{(destination.Contains("K2Node_") ? $"UberGraphFrame->{destination}" : destination)} = {variable};\n\n"); // hardcoded but works
                }
                else
                {
                    outputBuilder.Append(
                        $"\t\t{(destination.Contains("K2Node_") ? $"UberGraphFrame->{destination}" : destination)} = {variable}");
                }

                break;
            }
            case EExprToken.EX_LocalFinalFunction:
            {
                EX_FinalFunction op = (EX_FinalFunction) expression;
                KismetExpression[] opp = op.Parameters;
                //Console.WriteLine(op.StackNode.Index);
                //Console.WriteLine(op.StackNode.Name);
                if (isParameter)
                {
                    outputBuilder.Append($"{op.StackNode.Name.Replace(" ", "")}(");
                }
                else if (opp.Length < 1)
                {
                    outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");
                }
                else
                {
                    outputBuilder.Append(
                        $"\t\t{BlueprintToCppUtils.GetPrefix(op?.StackNode?.ResolvedObject?.Outer?.GetType()?.Name ?? string.Empty)}{op?.StackNode?.Name.Replace(" ", "")}(");
                }

                for (int i = 0; i < opp.Length; i++)
                {
                    if (opp.Length > 4)
                        outputBuilder.Append("\n\t\t");
                    ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                    if (i < opp.Length - 1)
                    {
                        outputBuilder.Append(", ");
                    }
                }

                outputBuilder.Append(isParameter ? ")" : ");\n");
                break;
            }
            case EExprToken.EX_FinalFunction:
            {
                EX_FinalFunction op = (EX_FinalFunction) expression;
                KismetExpression[] opp = op.Parameters;

                // ignore this but I use it to Modify bytecode
                //Console.WriteLine(op.StackNode.Index);
                //Console.WriteLine(op.StackNode.Name);
                //if (op.StackNode.Name == "CanRespawnOnStarterIsland")
                //{
                //Console.WriteLine("a");
                //}
                if (isParameter)
                {
                    outputBuilder.Append($"{op.StackNode.Name.Replace(" ", "")}(");
                }
                else if (opp.Length < 1)
                {
                    outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");
                }
                else
                {
                    outputBuilder.Append(
                        $"\t\t{op?.StackNode?.Name.Replace(" ", "")}("); //{Utils.GetPrefix(op?.StackNode?.ResolvedObject?.Outer?.GetType()?.Name)}
                }

                for (int i = 0; i < opp.Length; i++)
                {
                    if (opp.Length > 4)
                        outputBuilder.Append("\n\t\t");
                    ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                    if (i < opp.Length - 1)
                    {
                        outputBuilder.Append(", ");
                    }
                }

                outputBuilder.Append(isParameter ? ")" : ");\n\n");
                break;
            }
            case EExprToken.EX_CallMath:
            {
                EX_FinalFunction op = (EX_FinalFunction) expression;
                KismetExpression[] opp = op.Parameters;
                outputBuilder.Append(isParameter ? string.Empty : "\t\t");
                outputBuilder.Append(
                    $"{BlueprintToCppUtils.GetPrefix(op.StackNode.ResolvedObject.Outer.GetType().Name)}{op.StackNode.ResolvedObject.Outer.Name.ToString().Replace(" ", "")}::{op.StackNode.Name}(");

                for (int i = 0; i < opp.Length; i++)
                {
                    if (opp.Length > 4)
                        outputBuilder.Append("\n\t\t\t");
                    ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                    if (i < opp.Length - 1)
                    {
                        outputBuilder.Append(", ");
                    }
                }

                outputBuilder.Append(isParameter ? ")" : ");\n\n");
                break;
            }
            case EExprToken.EX_LocalVirtualFunction:
            case EExprToken.EX_VirtualFunction:
            {
                EX_VirtualFunction op = (EX_VirtualFunction) expression;
                KismetExpression[] opp = op.Parameters;

                //Console.WriteLine(op.VirtualFunctionName.Index);
                //Console.WriteLine(op.VirtualFunctionName.PlainText);
                if (isParameter)
                {
                    outputBuilder.Append($"{op.VirtualFunctionName.PlainText.Replace(" ", "")}(");
                }
                else
                {
                    outputBuilder.Append($"\t\t{op.VirtualFunctionName.PlainText.Replace(" ", "")}(");
                }

                for (int i = 0; i < opp.Length; i++)
                {
                    if (opp.Length > 4)
                        outputBuilder.Append("\n\t\t");

                    ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                    if (i < opp.Length - 1)
                    {
                        outputBuilder.Append(", ");
                    }
                }

                outputBuilder.Append(isParameter ? ")" : ");\n\n");
                break;
            }
            case EExprToken.EX_ComputedJump:
            {
                EX_ComputedJump op = (EX_ComputedJump) expression;
                if (op.CodeOffsetExpression is EX_VariableBase opp)
                {
                    outputBuilder.AppendLine($"\t\tgoto {ProcessTextProperty(opp.Variable)};\n");
                }
                else if (op.CodeOffsetExpression is EX_CallMath oppMath)
                {
                    ProcessExpression(oppMath.Token, oppMath, outputBuilder, jumpCodeOffsets, true);
                }
                else
                {
                    Console.WriteLine("no idea how you reached this");
                }

                break;
            }
            case EExprToken.EX_PopExecutionFlowIfNot:
            {
                EX_PopExecutionFlowIfNot op = (EX_PopExecutionFlowIfNot) expression;
                outputBuilder.Append("\t\tif (!");
                ProcessExpression(op.BooleanExpression.Token, op.BooleanExpression, outputBuilder, jumpCodeOffsets,
                    true);
                outputBuilder.Append(") \r\n");
                outputBuilder.Append($"\t\t    FlowStack.Pop();\n\n");
                break;
            }
            case EExprToken.EX_Cast:
            {
                EX_Cast
                    op = (EX_Cast) expression; // support CST_ObjectToInterface when I have an example of how it works

                if (ECastToken.CST_ObjectToBool == op.ConversionType ||
                    ECastToken.CST_InterfaceToBool == op.ConversionType)
                {
                    outputBuilder.Append("(bool)");
                }

                if (ECastToken.CST_DoubleToFloat == op.ConversionType)
                {
                    outputBuilder.Append("(float)");
                }

                if (ECastToken.CST_FloatToDouble == op.ConversionType)
                {
                    outputBuilder.Append("(double)");
                }

                ProcessExpression(op.Target.Token, op.Target, outputBuilder, jumpCodeOffsets);
                break;
            }
            case EExprToken.EX_InterfaceContext:
            {
                EX_InterfaceContext op = (EX_InterfaceContext) expression;
                ProcessExpression(op.InterfaceValue.Token, op.InterfaceValue, outputBuilder, jumpCodeOffsets);
                break;
            }
            case EExprToken.EX_ArrayConst:
            {
                EX_ArrayConst op = (EX_ArrayConst) expression;
                outputBuilder.Append("TArray {");
                foreach (KismetExpression element in op.Elements)
                {
                    outputBuilder.Append(' ');
                    ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);
                }

                outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                outputBuilder.Append("}");
                break;
            }
            case EExprToken.EX_SetArray:
            {
                EX_SetArray op = (EX_SetArray) expression;
                outputBuilder.Append("\t\t");
                ProcessExpression(op.AssigningProperty.Token, op.AssigningProperty, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append(" = ");
                outputBuilder.Append("TArray {");
                for (int i = 0; i < op.Elements.Length; i++)
                {
                    KismetExpression element = op.Elements[i];
                    outputBuilder.Append(' ');
                    ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);

                    outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                }

                outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                outputBuilder.Append("};\n\n");
                break;
            }
            case EExprToken.EX_SetSet:
            {
                EX_SetSet op = (EX_SetSet) expression;
                outputBuilder.Append("\t\t");
                ProcessExpression(op.SetProperty.Token, op.SetProperty, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append(" = ");
                outputBuilder.Append("TArray {");
                for (int i = 0; i < op.Elements.Length; i++)
                {
                    KismetExpression element = op.Elements[i];
                    outputBuilder.Append(' ');
                    ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);

                    outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                }

                outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                outputBuilder.Append("};\n\n");
                break;
            }
            case EExprToken.EX_SetConst:
            {
                EX_SetConst op = (EX_SetConst) expression;
                outputBuilder.Append("TArray {");
                for (int i = 0; i < op.Elements.Length; i++)
                {
                    KismetExpression element = op.Elements[i];
                    outputBuilder.Append(' ');
                    ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets, true);

                    outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                }

                outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                outputBuilder.Append("};\n\n");
                break;
            }
            case EExprToken.EX_SetMap:
            {
                EX_SetMap op = (EX_SetMap) expression;
                outputBuilder.Append("\t\t");
                ProcessExpression(op.MapProperty.Token, op.MapProperty, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append(" = ");
                outputBuilder.Append("TMap {");
                for (int i = 0; i < op.Elements.Length; i++)
                {
                    var element = op.Elements[i];
                    outputBuilder.Append(' ');
                    ProcessExpression(element.Token, element, outputBuilder,
                        jumpCodeOffsets); // sometimes the start of an array is a byte not a variable

                    if (i < op.Elements.Length - 1)
                    {
                        outputBuilder.Append(element.Token == EExprToken.EX_InstanceVariable ? ": " : ", ");
                    }
                    else
                    {
                        outputBuilder.Append(' ');
                    }
                }

                if (op.Elements.Length < 1)
                    outputBuilder.Append("  ");
                outputBuilder.Append("}\n");
                break;
            }
            case EExprToken.EX_MapConst:
            {
                EX_MapConst op = (EX_MapConst) expression;
                outputBuilder.Append("TMap {");
                for (int i = 0; i < op.Elements.Length; i++)
                {
                    var element = op.Elements[i];
                    outputBuilder.Append(' ');
                    ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets,
                        true); // sometimes the start of an array is a byte not a variable

                    if (i < op.Elements.Length - 1)
                    {
                        outputBuilder.Append(element.Token == EExprToken.EX_InstanceVariable ? ": " : ", ");
                    }
                    else
                    {
                        outputBuilder.Append(' ');
                    }
                }

                if (op.Elements.Length < 1)
                    outputBuilder.Append("  ");
                outputBuilder.Append("}\n");
                break;
            }
            case EExprToken.EX_SwitchValue:
            {
                EX_SwitchValue op = (EX_SwitchValue) expression;

                bool useTernary = op.Cases.Length <= 2
                                  && op.Cases.All(c =>
                                      c.CaseIndexValueTerm.Token == EExprToken.EX_True ||
                                      c.CaseIndexValueTerm.Token == EExprToken.EX_False);

                if (useTernary)
                {
                    ProcessExpression(op.IndexTerm.Token, op.IndexTerm, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" ? ");

                    bool isFirst = true;
                    foreach (var caseItem in op.Cases.Where(c => c.CaseIndexValueTerm.Token == EExprToken.EX_True))
                    {
                        if (!isFirst)
                            outputBuilder.Append(" : ");

                        ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets,
                            true);
                        isFirst = false;
                    }

                    foreach (var caseItem in op.Cases.Where(c => c.CaseIndexValueTerm.Token == EExprToken.EX_False))
                    {
                        if (!isFirst)
                            outputBuilder.Append(" : ");

                        ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets,
                            true);
                    }
                }
                else
                {
                    outputBuilder.Append("switch (");
                    ProcessExpression(op.IndexTerm.Token, op.IndexTerm, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(")\n");
                    outputBuilder.Append("\t\t{\n");

                    foreach (var caseItem in op.Cases)
                    {
                        if (caseItem.CaseIndexValueTerm.Token == EExprToken.EX_IntConst)
                        {
                            int caseValue = ((EX_IntConst) caseItem.CaseIndexValueTerm).Value;
                            outputBuilder.Append($"\t\t\tcase {caseValue}:\n");
                        }
                        else
                        {
                            outputBuilder.Append("\t\t\tcase ");
                            ProcessExpression(caseItem.CaseIndexValueTerm.Token, caseItem.CaseIndexValueTerm,
                                outputBuilder, jumpCodeOffsets);
                            outputBuilder.Append(":\n");
                        }

                        outputBuilder.Append("\t\t\t{\n");
                        outputBuilder.Append("\t\t\t    ");
                        ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(";\n");
                        outputBuilder.Append("\t\t\t    break;\n");
                        outputBuilder.Append("\t\t\t}\n");
                    }

                    outputBuilder.Append("\t\t\tdefault:\n");
                    outputBuilder.Append("\t\t\t{\n");
                    outputBuilder.Append("\t\t\t    ");
                    ProcessExpression(op.DefaultTerm.Token, op.DefaultTerm, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append("\n\t\t\t}\n");

                    outputBuilder.Append("\t\t}");
                }

                break;
            }
            case EExprToken.EX_ArrayGetByRef: // I assume get array with index
            {
                EX_ArrayGetByRef
                    op = (EX_ArrayGetByRef) expression; // FortniteGame/Plugins/GameFeatures/FM/PilgrimCore/Content/Player/Components/BP_PilgrimPlayerControllerComponent.uasset
                ProcessExpression(op.ArrayVariable.Token, op.ArrayVariable, outputBuilder, jumpCodeOffsets, true);
                outputBuilder.Append("[");
                ProcessExpression(op.ArrayIndex.Token, op.ArrayIndex, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append("]");
                break;
            }
            case EExprToken.EX_MetaCast:
            case EExprToken.EX_DynamicCast:
            case EExprToken.EX_ObjToInterfaceCast:
            case EExprToken.EX_CrossInterfaceCast:
            case EExprToken.EX_InterfaceToObjCast:
            {
                EX_CastBase op = (EX_CastBase) expression;
                outputBuilder.Append($"Cast<U{op.ClassPtr.Name}*>("); // m?
                ProcessExpression(op.Target.Token, op.Target, outputBuilder, jumpCodeOffsets, true);
                outputBuilder.Append(")");
                break;
            }
            case EExprToken.EX_StructConst:
            {
                EX_StructConst op = (EX_StructConst) expression;
                outputBuilder.Append($"{BlueprintToCppUtils.GetPrefix(op.Struct.GetType().Name)}{op.Struct.Name}");
                outputBuilder.Append($"(");
                for (int i = 0; i < op.Properties.Length; i++)
                {
                    var property = op.Properties[i];
                    ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
                    if (i < op.Properties.Length - 1 && property.Token != EExprToken.EX_ArrayConst)
                        outputBuilder.Append(", ");
                }

                outputBuilder.Append($")");
                break;
            }
            case EExprToken.EX_ObjectConst:
            {
                EX_ObjectConst op = (EX_ObjectConst) expression;
                outputBuilder.Append(!isParameter ? "\t\tFindObject<" :
                    outputBuilder.ToString().EndsWith("\n") ? "\t\tFindObject<" :
                    "FindObject<"); // please don't complain, i know this is bad but i MUST do it.
                string classString = op?.Value?.ResolvedObject?.Class?.ToString()?.Replace("'", "");

                if (classString?.Contains(".") == true)
                {
                    outputBuilder.Append(BlueprintToCppUtils.GetPrefix(op?.Value?.ResolvedObject?.Class?.GetType().Name) +
                                         classString.Split(".")[1]);
                }
                else
                {
                    outputBuilder.Append(
                        BlueprintToCppUtils.GetPrefix(op?.Value?.ResolvedObject?.Class?.GetType().Name) + classString);
                }

                outputBuilder.Append(">(\"");
                var resolvedObject = op?.Value?.ResolvedObject;
                var outerString = resolvedObject?.Outer?.ToString()?.Replace("'", "") ?? "UNKNOWN";
                var outerClassString = resolvedObject?.Class?.ToString()?.Replace("'", "") ?? "UNKNOWN";
                var name = op?.Value?.Name ?? string.Empty;

                outputBuilder.Append(outerString.Replace(outerClassString, "") + "." + name);

                if (isParameter)
                {
                    outputBuilder.Append("\")");
                }
                else
                {
                    outputBuilder.Append("\")");
                }

                break;
            }
            case EExprToken.EX_BindDelegate:
            {
                EX_BindDelegate op = (EX_BindDelegate) expression;
                outputBuilder.Append("\t\t");
                ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append($".BindUFunction(");
                ProcessExpression(op.ObjectTerm.Token, op.ObjectTerm, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append($", \"{op.FunctionName}\"");
                outputBuilder.Append($");\n\n");
                break;
            }
            // all the delegate functions suck
            case EExprToken.EX_AddMulticastDelegate:
            {
                EX_AddMulticastDelegate op = (EX_AddMulticastDelegate) expression;
                if (op.Delegate.Token == EExprToken.EX_LocalVariable ||
                    op.Delegate.Token == EExprToken.EX_InstanceVariable)
                {
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(".AddDelegate(");
                    ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($");\n\n");
                }
                else if (op.Delegate.Token != EExprToken.EX_Context)
                {
                    Console.WriteLine(
                        $"Issue: EX_AddMulticastDelegate missing info: {op.StatementIndex}, {op.Delegate.Token}");
                }
                else
                {
                    //EX_Context opp = (EX_Context) op.Delegate;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                    //outputBuilder.Append("->");
                    //ProcessExpression(opp.ContextExpression.Token, opp.ContextExpression, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(".AddDelegate(");
                    ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($");\n\n");
                }

                break;
            }
            case EExprToken.EX_RemoveMulticastDelegate
                : // everything here has been guessed not compared to actual UE but does work fine and displays all information
            {
                EX_RemoveMulticastDelegate op = (EX_RemoveMulticastDelegate) expression;
                if (op.Delegate.Token == EExprToken.EX_LocalVariable ||
                    op.Delegate.Token == EExprToken.EX_InstanceVariable)
                {
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(".RemoveDelegate(");
                    ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($");\n\n");
                }
                else if (op.Delegate.Token != EExprToken.EX_Context)
                {
                    Console.WriteLine("Issue: EX_RemoveMulticastDelegate missing info: {0}", op.StatementIndex);
                }
                else
                {
                    EX_Context opp = (EX_Context) op.Delegate;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append("->");
                    ProcessExpression(opp.ContextExpression.Token, opp.ContextExpression, outputBuilder,
                        jumpCodeOffsets);
                    outputBuilder.Append(".RemoveDelegate(");
                    ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($");\n\n");
                }

                break;
            }
            case EExprToken.EX_ClearMulticastDelegate: // this also
            {
                EX_ClearMulticastDelegate op = (EX_ClearMulticastDelegate) expression;
                outputBuilder.Append("\t\t");
                ProcessExpression(op.DelegateToClear.Token, op.DelegateToClear, outputBuilder, jumpCodeOffsets, true);
                outputBuilder.Append(".Clear();\n\n");
                break;
            }
            case EExprToken.EX_CallMulticastDelegate: // this also
            {
                EX_CallMulticastDelegate op = (EX_CallMulticastDelegate) expression;
                KismetExpression[] opp = op.Parameters;
                if (op.Delegate.Token == EExprToken.EX_LocalVariable ||
                    op.Delegate.Token == EExprToken.EX_InstanceVariable)
                {
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(".Call(");
                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }

                    outputBuilder.Append($");\n\n");
                }
                else if (op.Delegate.Token != EExprToken.EX_Context)
                {
                    Console.WriteLine("Issue: EX_CallMulticastDelegate missing info: {0}", op.StatementIndex);
                }
                else
                {
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(".Call(");
                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }

                    outputBuilder.Append($");\n\n");
                }

                break;
            }
            case EExprToken.EX_ClassContext:
            case EExprToken.EX_Context:
            {
                EX_Context op = (EX_Context) expression;
                outputBuilder.Append(outputBuilder.ToString().EndsWith("\n") ? "\t\t" : "");
                ProcessExpression(op.ObjectExpression.Token, op.ObjectExpression, outputBuilder, jumpCodeOffsets, true);

                outputBuilder.Append("->");
                ProcessExpression(op.ContextExpression.Token, op.ContextExpression, outputBuilder, jumpCodeOffsets,
                    true);
                if (!isParameter)
                {
                    outputBuilder.Append(";\n\n");
                }

                break;
            }
            case EExprToken.EX_Context_FailSilent:
            {
                EX_Context op = (EX_Context) expression;
                outputBuilder.Append("\t\t");
                ProcessExpression(op.ObjectExpression.Token, op.ObjectExpression, outputBuilder, jumpCodeOffsets, true);
                if (!isParameter)
                {
                    outputBuilder.Append("->");
                    ProcessExpression(op.ContextExpression.Token, op.ContextExpression, outputBuilder, jumpCodeOffsets,
                        true);
                    outputBuilder.Append($";\n\n");
                }

                break;
            }
            case EExprToken.EX_Let:
            {
                EX_Let op = (EX_Let) expression;
                if (!isParameter)
                {
                    outputBuilder.Append("\t\t");
                }

                ProcessExpression(op.Variable.Token, op.Variable, outputBuilder, jumpCodeOffsets, true);
                outputBuilder.Append(" = ");
                ProcessExpression(op.Assignment.Token, op.Assignment, outputBuilder, jumpCodeOffsets, true);
                if (!isParameter)
                {
                    outputBuilder.Append(";\n\n");
                }

                break;
            }
            case EExprToken.EX_LetObj:
            case EExprToken.EX_LetWeakObjPtr:
            case EExprToken.EX_LetBool:
            case EExprToken.EX_LetDelegate:
            case EExprToken.EX_LetMulticastDelegate:
            {
                EX_LetBase op = (EX_LetBase) expression;
                if (!isParameter)
                {
                    outputBuilder.Append("\t\t");
                }

                ProcessExpression(op.Variable.Token, op.Variable, outputBuilder, jumpCodeOffsets, true);
                outputBuilder.Append(" = ");
                ProcessExpression(op.Assignment.Token, op.Assignment, outputBuilder, jumpCodeOffsets, true);
                if (!isParameter || op.Assignment.Token == EExprToken.EX_LocalFinalFunction ||
                    op.Assignment.Token == EExprToken.EX_FinalFunction || op.Assignment.Token == EExprToken.EX_CallMath)
                {
                    outputBuilder.Append($";\n\n");
                }
                else
                {
                    outputBuilder.Append($";");
                }

                break;
            }
            case EExprToken.EX_JumpIfNot:
            {
                EX_JumpIfNot op = (EX_JumpIfNot) expression;
                outputBuilder.Append("\t\tif (!");
                ProcessExpression(op.BooleanExpression.Token, op.BooleanExpression, outputBuilder, jumpCodeOffsets,
                    true);
                outputBuilder.Append(") \r\n");
                outputBuilder.Append("\t\t    goto Label_");
                outputBuilder.Append(op.CodeOffset);
                outputBuilder.Append(";\n\n");
                break;
            }
            case EExprToken.EX_Jump:
            {
                EX_Jump op = (EX_Jump) expression;
                outputBuilder.Append($"\t\tgoto Label_{op.CodeOffset};\n\n");
                break;
            }
            // Static expressions

            case EExprToken.EX_TextConst:
            {
                EX_TextConst op = (EX_TextConst) expression;

                if (op.Value is FScriptText scriptText)
                {
                    if (scriptText.SourceString == null)
                    {
                        outputBuilder.Append("nullptr");
                    }
                    else
                        ProcessExpression(scriptText.SourceString.Token, scriptText.SourceString, outputBuilder,
                            jumpCodeOffsets, true);
                }
                else
                {
                    outputBuilder.Append(op.Value);
                }
            }
                break;
            case EExprToken.EX_StructMemberContext:
            {
                EX_StructMemberContext op = (EX_StructMemberContext) expression;
                ProcessExpression(op.StructExpression.Token, op.StructExpression, outputBuilder, jumpCodeOffsets);
                outputBuilder.Append('.');
                outputBuilder.Append(ProcessTextProperty(op.Property));
                break;
            }
            case EExprToken.EX_Return:
            {
                EX_Return op = (EX_Return) expression;
                bool check = op.ReturnExpression.Token == EExprToken.EX_Nothing;
                outputBuilder.Append($"\t\treturn");
                if (!check)
                    outputBuilder.Append(' ');
                ProcessExpression(op.ReturnExpression.Token, op.ReturnExpression, outputBuilder, jumpCodeOffsets, true);
                outputBuilder.AppendLine(";\n\n");
                break;
            }
            case EExprToken.EX_RotationConst:
            {
                EX_RotationConst op = (EX_RotationConst) expression;
                FRotator value = op.Value;
                outputBuilder.Append($"FRotator({value.Pitch}, {value.Yaw}, {value.Roll})");
                break;
            }
            case EExprToken.EX_VectorConst:
            {
                EX_VectorConst op = (EX_VectorConst) expression;
                FVector value = op.Value;
                outputBuilder.Append($"FVector({value.X}, {value.Y}, {value.Z})");
                break;
            }
            case EExprToken.EX_Vector3fConst:
            {
                EX_Vector3fConst op = (EX_Vector3fConst) expression;
                FVector value = op.Value;
                outputBuilder.Append($"FVector3f({value.X}, {value.Y}, {value.Z})");
                break;
            }
            case EExprToken.EX_TransformConst:
            {
                EX_TransformConst op = (EX_TransformConst) expression;
                FTransform value = op.Value;
                outputBuilder.Append(
                    $"FTransform(FQuat({value.Rotation.X}, {value.Rotation.Y}, {value.Rotation.Z}, {value.Rotation.W}), FVector({value.Translation.X}, {value.Translation.Y}, {value.Translation.Z}), FVector({value.Scale3D.X}, {value.Scale3D.Y}, {value.Scale3D.Z}))");
                break;
            }


            case EExprToken.EX_LocalVariable:
            case EExprToken.EX_DefaultVariable:
            case EExprToken.EX_InstanceVariable:
            case EExprToken.EX_LocalOutVariable:
            case EExprToken.EX_ClassSparseDataVariable:
                outputBuilder.Append(ProcessTextProperty(((EX_VariableBase) expression).Variable));
                break;

            case EExprToken.EX_ByteConst:
            case EExprToken.EX_IntConstByte:
                outputBuilder.Append($"0x{((KismetExpression<byte>) expression).Value.ToString("X")}");
                break;
            case EExprToken.EX_SoftObjectConst:
                ProcessExpression(((EX_SoftObjectConst) expression).Value.Token,
                    ((EX_SoftObjectConst) expression).Value, outputBuilder, jumpCodeOffsets);
                break;
            case EExprToken.EX_DoubleConst:
            {
                double value = ((EX_DoubleConst) expression).Value;
                outputBuilder.Append(Math.Abs(value - Math.Floor(value)) < 1e-10 ? (int) value : value.ToString("R"));
                break;
            }
            case EExprToken.EX_NameConst:
                outputBuilder.Append($"\"{((EX_NameConst) expression).Value}\"");
                break;
            case EExprToken.EX_IntConst:
                outputBuilder.Append(((EX_IntConst) expression).Value.ToString());
                break;
            case EExprToken.EX_PropertyConst:
                outputBuilder.Append(ProcessTextProperty(((EX_PropertyConst) expression).Property));
                break;
            case EExprToken.EX_StringConst:
                outputBuilder.Append($"\"{((EX_StringConst) expression).Value}\"");
                break;
            case EExprToken.EX_FieldPathConst:
                ProcessExpression(((EX_FieldPathConst) expression).Value.Token, ((EX_FieldPathConst) expression).Value,
                    outputBuilder, jumpCodeOffsets);
                break;
            case EExprToken.EX_Int64Const:
                outputBuilder.Append(((EX_Int64Const) expression).Value.ToString());
                break;
            case EExprToken.EX_UInt64Const:
                outputBuilder.Append(((EX_UInt64Const) expression).Value.ToString());
                break;
            case EExprToken.EX_SkipOffsetConst:
                outputBuilder.Append(((EX_SkipOffsetConst) expression).Value.ToString());
                break;
            case EExprToken.EX_FloatConst:
                outputBuilder.Append(((EX_FloatConst) expression).Value.ToString(CultureInfo.GetCultureInfo("en-US")));
                break;
            case EExprToken.EX_BitFieldConst:
                outputBuilder.Append(((EX_BitFieldConst) expression).ConstValue);
                break;
            case EExprToken.EX_UnicodeStringConst:
                outputBuilder.Append(((EX_UnicodeStringConst) expression).Value);
                break;
            case EExprToken.EX_InstanceDelegate:
                outputBuilder.Append($"\"{((EX_InstanceDelegate) expression).FunctionName}\"");
                break;
            case EExprToken.EX_EndOfScript:
            case EExprToken.EX_EndParmValue:
                outputBuilder.Append("\t}\n");
                break;
            case EExprToken.EX_NoObject:
            case EExprToken.EX_NoInterface:
                outputBuilder.Append("nullptr");
                break;
            case EExprToken.EX_IntOne:
                outputBuilder.Append(1);
                break;
            case EExprToken.EX_IntZero:
                outputBuilder.Append(0);
                break;
            case EExprToken.EX_True:
                outputBuilder.Append("true");
                break;
            case EExprToken.EX_False:
                outputBuilder.Append("false");
                break;
            case EExprToken.EX_Self:
                outputBuilder.Append("this");
                break;

            case EExprToken.EX_Nothing:
            case EExprToken.EX_NothingInt32:
            case EExprToken.EX_EndFunctionParms:
            case EExprToken.EX_EndStructConst:
            case EExprToken.EX_EndArray:
            case EExprToken.EX_EndArrayConst:
            case EExprToken.EX_EndSet:
            case EExprToken.EX_EndMap:
            case EExprToken.EX_EndMapConst:
            case EExprToken.EX_EndSetConst:
            case EExprToken.EX_PushExecutionFlow:
            case EExprToken.EX_PopExecutionFlow:
            case EExprToken.EX_DeprecatedOp4A:
            case EExprToken.EX_WireTracepoint:
            case EExprToken.EX_Tracepoint:
            case EExprToken.EX_Breakpoint:
            case EExprToken.EX_AutoRtfmStopTransact:
            case EExprToken.EX_AutoRtfmTransact:
            case EExprToken.EX_AutoRtfmAbortIfNot:
                // some here are "useful" and unsupported
                break;
            /*
            EExprToken.EX_Assert
            EExprToken.EX_Skip
            EExprToken.EX_InstrumentationEvent
            EExprToken.EX_FieldPathConst
            */
            default:
                Console.WriteLine($"Error: Unknown bytecode {token}");
                outputBuilder.Append($"{token}");
                break;
        }
    }
}

public class BlueprintConversionResult
{
    public List<ConvertedFile> ConvertedFiles { get; set; } = new List<ConvertedFile>();
    public List<string> Errors { get; set; } = new List<string>();
    public bool Success { get; set; }
}

public class ConvertedFile
{
    public string OriginalPath { get; set; }
    public string FileName { get; set; }
    public string CppContent { get; set; }
    public string OutputPath { get; set; }
}

public static class BlueprintToCppUtils
{
    public static string GetPrefix(string? type, string? extra = "")
    {
        return type switch
        {
            "FNameProperty" or "FPackageIndex" or "FTextProperty" or "FStructProperty" => "F",
            "UBlueprintGeneratedClass" or "FActorProperty" => "A",
            "FObjectProperty" when extra.Contains("Actor") => "A",
            "ResolvedScriptObject" or "ResolvedLoadedObject" or "FSoftObjectProperty" or "FObjectProperty" => "U",
            _ => ""
        };
    }

    // These methods were taken from
    // https://github.com/CrystalFerrai/UeBlueprintDumper/blob/main/UeBlueprintDumper/BlueprintDumper.cs#L352
    // nothing else in this repository is from UeBlueprintDumper

    public static string GetUnknownFieldType(object field)
    {
        string typeName = field.GetType().Name;
        int suffixIndex = typeName.IndexOf("Property", StringComparison.Ordinal);
        if (suffixIndex < 0)
            return typeName;
        return typeName.Substring(1, suffixIndex - 1);
    }

    public static string GetUnknownFieldType(FField field)
    {
        string typeName = field.GetType().Name;
        int suffixIndex = typeName.IndexOf("Property", StringComparison.Ordinal);
        if (suffixIndex < 0) return typeName;
        return typeName.Substring(1, suffixIndex - 1);
    }

    public static string GetPropertyType(object? property)
    {
        if (property is null) return "None";

        return property switch
        {
            FIntProperty => "int",
            FInt8Property => "int8",
            FInt16Property => "int16",
            FInt64Property => "int64",
            FUInt16Property => "uint16",
            FUInt32Property => "uint32",
            FUInt64Property => "uint64",
            FBoolProperty or Boolean => "bool",
            FStrProperty => "FString",
            FFloatProperty or Single => "float",
            FDoubleProperty or Double => "double",
            FObjectProperty objct => property switch
            {
                FClassProperty clss => $"{clss.MetaClass?.Name ?? "UNKNOWN"}",
                FSoftClassProperty softClass => $"{softClass.MetaClass?.Name ?? "UNKNOWN"}",
                _ => objct.PropertyClass?.Name ?? "UNKNOWN"
            },
            FPackageIndex pkg => pkg?.ResolvedObject?.Class?.Name.ToString() ?? "Package",
            FName fme => fme.PlainText.Contains("::") ? fme.PlainText.Split("::")[0] : fme.PlainText ?? "FName",
            FEnumProperty enm => enm.Enum?.Name.ToString() ?? "Enum",
            FByteProperty bt => bt.Enum.ResolvedObject?.Name.Text ?? "Byte",
            FInterfaceProperty intrfc => $"{intrfc.InterfaceClass.Name} interface",
            FStructProperty strct => strct.Struct.ResolvedObject?.Name.Text ?? "Struct",
            FFieldPathProperty fieldPath => $"{fieldPath.PropertyClass.Text} field path",
            FDelegateProperty dlgt => $"{dlgt.SignatureFunction?.Name ?? "UNKNOWN"} (Delegate)",
            FMulticastDelegateProperty mdlgt =>
                $"{mdlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastDelegateProperty)",
            FMulticastInlineDelegateProperty midlgt =>
                $"{midlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastInlineDelegateProperty)",
            _ => GetUnknownFieldType(property)
        };
    }

    public static string GetPropertyType(FProperty? property)
    {
        if (property is null) return "None";

        return property switch
        {
            FIntProperty => "int",
            FBoolProperty => "bool",
            FStrProperty => "FString",
            FFloatProperty => "float",
            FDoubleProperty => "double",
            FObjectProperty objct => property switch
            {
                FClassProperty clss => $"{clss.MetaClass?.Name ?? "UNKNOWN"} Class",
                FSoftClassProperty softClass => $"{softClass.MetaClass?.Name ?? "UNKNOWN"} Class (soft)",
                _ => objct.PropertyClass?.Name ?? "UNKNOWN"
            },
            FEnumProperty enm => enm.Enum?.Name.ToString() ?? "Enum",
            FSetProperty set =>
                $"TSet<{GetPrefix(set.ElementProp.GetType().Name)}{GetPropertyType(set.ElementProp)}{(set.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || set.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) ? "*" : string.Empty)}>",
            FByteProperty bt => bt.Enum.ResolvedObject?.Name.Text ?? "Byte",
            FInterfaceProperty intrfc => $"{intrfc.InterfaceClass.Name} interface",
            FStructProperty strct => strct.Struct.ResolvedObject?.Name.Text ?? "Struct",
            FFieldPathProperty fieldPath => $"{fieldPath.PropertyClass.Text} field path",
            FDelegateProperty dlgt => $"{dlgt.SignatureFunction?.Name ?? "UNKNOWN"} (Delegate)",
            FMapProperty map =>
                $"TMap<{GetPrefix(map.ValueProp.GetType().Name)}{GetPropertyType(map.KeyProp)}, {GetPrefix(map.ValueProp.GetType().Name)}{GetPropertyType(map.ValueProp)}{(map.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || map.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) ? "*" : string.Empty)}>",
            FMulticastDelegateProperty mdlgt =>
                $"{mdlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastDelegateProperty)",
            FMulticastInlineDelegateProperty midlgt =>
                $"{midlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastInlineDelegateProperty)",
            FArrayProperty array =>
                $"TArray<{GetPrefix(array.Inner.GetType().Name)}{GetPropertyType(array.Inner)}{(array.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || array.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) || GetPropertyProperty(array.Inner.GetType().Name) ? "*" : string.Empty)}>",
            _ => GetUnknownFieldType(property)
        };
    }

    public static bool GetPropertyProperty(object? property)
    {
        if (property is null) return false;

        return property switch
        {
            FObjectProperty objct => true,
            _ => false
        };
    }

    public static bool GetPropertyProperty(FProperty? property)
    {
        if (property is null) return false;

        return property switch
        {
            FObjectProperty objct => true,
            _ => false
        };
    }
}
