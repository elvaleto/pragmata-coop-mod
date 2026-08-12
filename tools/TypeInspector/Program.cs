using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Immutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: TypeInspector <assembly.dll> <type-name-filter> [member-filter]");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var typeFilter = args[1];
var memberFilter = args.Length >= 3 ? args[2] : string.Empty;

using var stream = File.OpenRead(assemblyPath);
using var peReader = new PEReader(stream);
var metadata = peReader.GetMetadataReader();
var typeNames = new TypeNameProvider();

foreach (var typeHandle in metadata.TypeDefinitions)
{
    var type = metadata.GetTypeDefinition(typeHandle);
    var fullName = typeNames.GetTypeFromDefinition(metadata, typeHandle, 0);

    if (!fullName.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
        continue;

    Console.WriteLine($"TYPE {fullName}");

    foreach (var fieldHandle in type.GetFields())
    {
        var field = metadata.GetFieldDefinition(fieldHandle);
        var name = metadata.GetString(field.Name);
        if (Matches(name, memberFilter))
            Console.WriteLine($"  FIELD {field.DecodeSignature(typeNames, null)} {name}");
    }

    foreach (var propertyHandle in type.GetProperties())
    {
        var property = metadata.GetPropertyDefinition(propertyHandle);
        var name = metadata.GetString(property.Name);
        if (Matches(name, memberFilter))
        {
            var signature = property.DecodeSignature(typeNames, null);
            Console.WriteLine($"  PROPERTY {signature.ReturnType} {name}");
        }
    }

    foreach (var methodHandle in type.GetMethods())
    {
        var method = metadata.GetMethodDefinition(methodHandle);
        var name = metadata.GetString(method.Name);
        if (Matches(name, memberFilter))
        {
            var signature = method.DecodeSignature(typeNames, null);
            Console.WriteLine($"  METHOD {signature.ReturnType} {name}({string.Join(", ", signature.ParameterTypes)})");
        }
    }
}

return 0;

static bool Matches(string value, string filter) =>
    string.IsNullOrEmpty(filter) || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => $"{elementType}&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => $"{elementType}*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => $"{elementType}[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
            return $"{GetTypeFromDefinition(reader, declaringType, rawTypeKind)}+{reader.GetString(type.Name)}";
        return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    private static string FullName(string typeNamespace, string typeName) =>
        string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";
}
