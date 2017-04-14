namespace VSharp

open System
open System.Collections.Generic
open System.Reflection
open JetBrains.Metadata.Reader.API

[<StructuralEquality;NoComparison>]
type public TermType =
    | Void
    | Bottom
    | Object
    | Bool
    | Numeric of System.Type
    | String
    | StructType of System.Type
    | ClassType of System.Type
    | ArrayType of TermType * int
    | Func of TermType list * TermType

    override this.ToString() =
        match this with
        | Void -> "void"
        | Bottom -> "exception"
        | Object -> "object"
        | Bool -> "bool"
        | Numeric t -> t.Name.ToLower()
        | String -> "string"
        | Func(domain, range) -> String.Join(" -> ", List.append domain [range])
        | StructType t -> t.ToString()
        | ClassType t -> t.ToString()
        | ArrayType(t, dim) -> t.ToString() + "[" + new string(',', dim) + "]"

module public Types =
    let private integerTypes =
        new HashSet<System.Type>(
                          [typedefof<byte>; typedefof<sbyte>;
                           typedefof<int16>; typedefof<uint16>;
                           typedefof<int32>; typedefof<uint32>;
                           typedefof<int64>; typedefof<uint64>;
                           typedefof<char>])

    let private realTypes =
        new HashSet<System.Type>([typedefof<single>; typedefof<double>; typedefof<decimal>])

    let private numericTypes = new HashSet<System.Type>(Seq.append integerTypes realTypes)

    let private primitiveTypes = new HashSet<Type>(Seq.append numericTypes [typedefof<bool>; typedefof<string>])

    let rec public ToDotNetType t =
        match t with
        | Object -> typedefof<obj>
        | Bool -> typedefof<bool>
        | Numeric res -> res
        | String -> typedefof<string>
        | StructType t -> t
        | ClassType t -> t
        | ArrayType(t, dim) -> (ToDotNetType t).MakeArrayType(dim)
        | _ -> typedefof<obj>

    let rec public FromDotNetType = function
        | b when b.Equals(typedefof<bool>) -> Bool
        | n when numericTypes.Contains(n) -> Numeric n
        | s when s.Equals(typedefof<string>) -> String
        | f when f.IsSubclassOf(typedefof<System.Delegate>) ->
            let methodInfo = f.GetMethod("Invoke") in
            let returnType = methodInfo.ReturnType |> FromDotNetType in
            let parameters = methodInfo.GetParameters() |> Array.map (fun (p : System.Reflection.ParameterInfo) -> FromDotNetType p.ParameterType) in
            Func(List.ofArray parameters, returnType)
        | a when a.IsArray -> ArrayType(FromDotNetType(a.GetElementType()), a.GetArrayRank())
        | s when s.IsValueType -> StructType s
        // Actually interface is not nessesary reference type, but if the implementation is unknown we consider it to be class (to check non-null).
        | c when c.IsClass || c.IsInterface -> ClassType c
        | _ -> __notImplemented__()

    let public FromQualifiedTypeName = System.Type.GetType >> FromDotNetType

    let rec public FromMetadataType (t : IMetadataType) =
        if t = null then Object
        else
            match t with
            | _ when t.AssemblyQualifiedName = "__Null" -> Object
            | _ when t.FullName = "System.Object" -> Object
            | :? IMetadataGenericArgumentReferenceType as g ->
                let constraints = g.Argument.TypeConstraints in
                if not(Array.isEmpty constraints) then
                    __notImplemented__()
                Object
            | :? IMetadataArrayType as a ->
                let elementType = FromMetadataType a.ElementType in
                ArrayType(elementType, int(a.Rank))
            | :? IMetadataClassType as c ->
                Type.GetType(c.Type.AssemblyQualifiedName, true) |> FromDotNetType
            | _ ->
                if (t.FullName = "T" || t.FullName = ".T") then
                    Console.WriteLine(t.GetType())
                Type.GetType(t.AssemblyQualifiedName, true) |> FromDotNetType

    let public MetadataToDotNetType (t : IMetadataType) = t |> FromMetadataType |> ToDotNetType

    let public FromDecompiledSignature (signature : JetBrains.Decompiler.Ast.IFunctionSignature) (returnMetadataType : IMetadataType) =
        let returnType = FromMetadataType returnMetadataType in
        let paramToType (param : JetBrains.Decompiler.Ast.IMethodParameter) =
            param.Type |> FromMetadataType
        let args = Seq.map paramToType signature.Parameters |> List.ofSeq in
        Func(args, returnType)

    let public FromMetadataMethodSignature (m : IMetadataMethod) =
        let returnType = FromMetadataType m.ReturnValue.Type in
        let paramToType (param : IMetadataParameter) =
            param.Type |> FromMetadataType
        let args = Seq.map paramToType m.Parameters |> List.ofSeq in
        Func(args, returnType)

    let public IsInteger = ToDotNetType >> integerTypes.Contains

    let public IsReal = ToDotNetType >> realTypes.Contains

    let public IsNumeric = function
        | Numeric _ -> true
        | _ -> false

    let public IsBool = function
        | Bool -> true
        | _ -> false

    let public IsString = function
        | String -> true
        | _ -> false

    let public IsFunction = function
        | Func _ -> true
        | _ -> false

    let public IsClass = function
        | ClassType _ -> true
        | _ -> false

    let public IsStruct = function
        | StructType _ -> true
        | _ -> false

    let public IsArray = function
        | ArrayType _ -> true
        | _ -> false

    let public IsObject = function
        | Object _ -> true
        | _ -> false

    let public IsVoid = function
        | Void -> true
        | _ -> false

    let public IsBottom = function
        | Bottom -> true
        | _ -> false

    let public IsReference t = IsClass t || IsObject t || IsFunction t

    let public IsPrimitive = ToDotNetType >> primitiveTypes.Contains

    let public DomainOf = function
        | Func(domain, _) -> domain
        | _ -> []

    let public RangeOf = function
        | Func(_, range) -> range
        | t -> t

    let public IsRelation = RangeOf >> IsBool

    let public GetMetadataTypeOfNode (node : JetBrains.Decompiler.Ast.INode) =
        DecompilerServices.getTypeOfNode node

    let public GetSystemTypeOfNode (node : JetBrains.Decompiler.Ast.INode) =
        let mt = GetMetadataTypeOfNode node in
        if mt = null then typedefof<obj>
        else ToDotNetType (FromMetadataType mt)

    let public GetFieldsOf (t : System.Type) isStatic =
        let staticFlag = if isStatic then BindingFlags.Static else BindingFlags.Instance in
        let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| staticFlag in
        let fields = t.GetFields(flags) in
        let extractFieldInfo (field : FieldInfo) =
            (sprintf "%s.%s" field.DeclaringType.FullName field.Name, FromDotNetType field.FieldType)
        fields |> Array.map extractFieldInfo |> Map.ofArray
