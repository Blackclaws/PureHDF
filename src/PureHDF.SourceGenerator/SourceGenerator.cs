using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PureHDF.SourceGenerator;

[Generator]
public class SourceGenerator : ISourceGenerator
{
    private static readonly Regex _replacePossiblyInvalidCharacters = new("[^a-zA-Z0-9]");
    private static readonly Regex _startsWithInvalidCharacter = new("^[^a-zA-Z]");

    public void Execute(GeneratorExecutionContext context)
    {
        /* (1) Uncomment "while ..." below.
         * (2) Create breakpoint after the while block.
         * (3) Start compilation ... it will wait for the debugger to become attached.
         * (4) Select the "Debug Source Generator" profile and then launch hit
         * (5) Select the process that has the paramter "exec" in it from the process list.
         */

        // while (!Debugger.IsAttached)
        //     Thread.Sleep(1000);

        var attributeFullName = typeof(H5SourceGeneratorAttribute).FullName;

        foreach (var syntaxTree in context.Compilation.SyntaxTrees)
        {
            var model = context.Compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            // find attribute syntax
            var collector = new H5SourceGeneratorAttributeCollector();
            collector.Visit(root);

            foreach (var attributeSyntax in collector.Attributes)
            {
                try
                {
                    var classDeclarationSyntax = (ClassDeclarationSyntax)attributeSyntax.Parent!.Parent!;
                    var sourceFilePath = classDeclarationSyntax.SyntaxTree.FilePath;
                    var sourceFolderPath = Path.GetDirectoryName(sourceFilePath);

                    var isPartial = classDeclarationSyntax.Modifiers
                        .Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));

                    // TODO Create diagnostic here to notify user that the partial keyword is missing.
                    if (!isPartial)
                        continue;

                    var classSymbol = model.GetDeclaredSymbol(classDeclarationSyntax)!;
                    var accessibility = classSymbol.DeclaredAccessibility;

                    var isPublic = accessibility == Accessibility.Public;
                    var isInternal = accessibility == Accessibility.Internal;

                    // TODO Create diagnostic here to notify user about the accessibility problem.
                    var accessibilityString = isPublic
                        ? "public"
                        : isInternal
                            ? "internal"
                            : throw new Exception("Class accessibility must be public or internal.");

                    var className = classSymbol.Name;
                    var classNamespace = classSymbol.ContainingNamespace.ToDisplayString();
                    var attributes = classSymbol.GetAttributes();

                    var attribute = attributes
                        .Where(attribute =>
                            attribute.AttributeClass is not null &&
                            attribute.AttributeClass.ToDisplayString() == attributeFullName)
                        .FirstOrDefault();

                    if (attribute is null)
                        continue;

                    var h5FilePath = attribute.ConstructorArguments[0].Value!.ToString();

                    if (!Path.IsPathRooted(h5FilePath))
                        h5FilePath = Path.Combine(sourceFolderPath, h5FilePath);

                    using var h5File = H5File.OpenRead(h5FilePath);
                    var source = GenerateSource(className, classNamespace, accessibilityString, h5File);

                    context.AddSource($"{classSymbol.ToDisplayString()}.g.cs", source);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                }
            }
        }
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        // No initialization required for this one
    }

    public static string NormalizeName(string input)
    {
        var output = _replacePossiblyInvalidCharacters.Replace(input, "_");

        if (_startsWithInvalidCharacter.IsMatch(output))
            output = "_" + output;

        return output;
    }

    private static string GenerateSource(string className, string classNamespace, string accessibilityString, INativeFile root)
    {
        var classDefinitions = new List<string>();

        ProcessGroup(className, root, accessibilityString, classDefinitions);

        string source;

        if (classNamespace == "<global namespace>")
        {
            source =
            $$"""
            // <auto-generated/>
            using PureHDF;
            using Generated{{className}}Bindings;

            {{classDefinitions.Last()}}

            namespace Generated{{className}}Bindings
            {
            {{string.Join("\n\n", classDefinitions.Take(classDefinitions.Count - 1))}}
            }
            """;
        }

        else
        {
            var globalNamespaceString = "<global namespace>";

            if (classNamespace.StartsWith(globalNamespaceString))
                classNamespace = classNamespace.Substring(globalNamespaceString.Length);

            source = $$"""
            // <auto-generated/>
            using PureHDF;
            using {{classNamespace}}.Generated{{className}}Bindings;

            namespace {{classNamespace}}
            {
            {{classDefinitions.Last()}}
            }

            namespace {{classNamespace}}.Generated{{className}}Bindings
            {
            {{string.Join("\n\n", classDefinitions.Take(classDefinitions.Count - 1))}}
            }
            """;
        }


        return source;
    }

    private static void ProcessGroup(
        string path,
        IH5Group group,
        string accessibilityString,
        List<string> classDefinitions)
    {
        var propertyNameMap = new Dictionary<string, string>();

        // ensure unique property names
        foreach (var link in group.Children())
        {
            var propertyName = NormalizeName(link.Name);
            var modifiedPropertyName = propertyName;
            var counter = 0;

            while (propertyNameMap.ContainsKey(modifiedPropertyName))
            {
                if (counter == 100)
                    throw new Exception("Too many similar link names");

                modifiedPropertyName = propertyName + $"_{counter:D0}";
                counter++;
            }

            propertyNameMap[modifiedPropertyName] = link.Name;
        }

        // build properties
        var constructorBuilder = new StringBuilder();
        var properties = new List<string>();

        foreach (var link in group.Children())
        {
            var propertyName = propertyNameMap.First(entry => entry.Value == link.Name).Key;

            var constructor = link switch
            {
                IH5Group subGroup => $"""            {propertyName} = new {GetPath(path, group, subGroup).Replace('/', '_')}(_parent.Group("{link.Name}"));""",
                IH5Dataset => $"""            {propertyName} = _parent.Get<IH5Dataset>("{link.Name}");""",
                IH5CommitedDatatype => $"""            {propertyName} = _parent.Get<IH5CommitedDatatype>("{link.Name}");""",
                IH5UnresolvedLink => $"""            {propertyName} = _parent.Get<IH5UnresolvedLink>("{link.Name}");""",
                _ => throw new Exception("Unknown link type")
            };

            constructorBuilder.AppendLine(constructor);

            var propertyDefinition = link switch
            {
                IH5Group subGroup => $$"""public {{GetPath(path, group, subGroup).Replace('/', '_')}} {{NormalizeName(subGroup.Name)}} { get; }""",
                IH5Dataset => $$"""public IH5Dataset {{propertyName}} { get; }""",
                IH5CommitedDatatype => $$"""public IH5CommitedDatatype {{propertyName}} { get; }""",
                IH5UnresolvedLink => $$"""public IH5UnresolvedLink {{propertyName}} { get; }""",
                _ => throw new Exception("Unknown link type")
            };

            if (link is IH5Group subGroup2)
                ProcessGroup(
                path: GetPath(path, group, subGroup2),
                subGroup2,
                accessibilityString,
                classDefinitions);

            var property =
            $"""
                    /// <summary>
                    /// Gets the value of link /{link.Name}.
                    /// </summary>
                    {propertyDefinition}
            """;

            properties.Add(property);
        }

        var className = path.Replace('/', '_');

        string constructorDocString;
        string constructorAccessibilityString;
        string partialString;
        string parentGroupString;

        if (group.Name == "/")
        {
            constructorDocString =
            $"""
                    /// <summary>
                    /// Initializes a new instance of the <see cref="{className}"/> class.
                    /// </summary>
                    /// <param name="path">The path of the object.</param>
                    /// <param name="linkAccess">The link access properties.</param>
            """;

            constructorAccessibilityString = "public ";
            partialString = "partial ";
            parentGroupString = "INativeFile file";
        }

        else
        {
            constructorDocString = "";
            constructorAccessibilityString = "internal ";
            partialString = "";
            parentGroupString = "IH5Group parent";
        }

        var classSource =
        $$"""
            /// <summary>
            /// Represents the links in group /{{path}}.
            /// </summary>
            {{accessibilityString}} {{partialString}}class {{className}}
            {
                private IH5Group _parent;

        {{constructorDocString}}
                {{constructorAccessibilityString}} {{className}}({{parentGroupString}})
                {
        {{(group.Name == "/" ? "            _parent = file;\n\n" : "            _parent = parent;\n\n")}}{{constructorBuilder}}        }

        {{string.Join("\n\n", properties)}}

                /// <summary>
                /// Gets the current group.
                /// </summary>
                public IH5Group Get() => _parent;
            }
        """;

        classDefinitions.Add(classSource);
    }

    private static string GetPath(string path, IH5Group group, IH5Group subGroup)
    {
        return group.Name == "/"
            ? subGroup.Name
            : $"{path}/{subGroup.Name}";
    }
}